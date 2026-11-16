// Full-stack load test: HTTP -> ASP.NET Core -> native engine -> PostgreSQL.
//
//   docker compose --profile bench run --rm k6
//
// Environment:
//   BASE_URL      API base URL                         (default http://localhost:8080)
//   LOAD_USERNAME   an Admin user                        (default ops.admin)
//   LOAD_PASSWORD   its password                         (required)
//   LOAD_VUS        concurrent virtual users             (default 32)
//   LOAD_DURATION   measured duration                    (default 60s)
//   LOAD_WARMUP     warm-up duration, not measured       (default 15s)
//   LOAD_RESULTS    directory for k6-summary.json        (default /results)
//
// The variables are deliberately not named K6_*: k6 reserves K6_VUS and K6_DURATION
// and would replace the scenarios below with its default executor.
//
// The API rate limiter must be disabled for the run (RATE_LIMITING_ENABLED=false),
// otherwise the test measures the limiter.
import http from 'k6/http';
import { check, fail } from 'k6';
import exec from 'k6/execution';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const VUS = parseInt(__ENV.LOAD_VUS || '32', 10);
const DURATION = __ENV.LOAD_DURATION || '60s';
const WARMUP = __ENV.LOAD_WARMUP || '15s';
const RESULTS = __ENV.LOAD_RESULTS || '/results';

const transferDuration = new Trend('transfer_duration', true);
const completed = new Counter('transfers_completed');
const rejected = new Counter('transfers_rejected');
const errors = new Counter('transfers_error');
const errorRate = new Rate('transfer_error_rate');
const ledgerConsistent = new Rate('ledger_consistent_after_run');

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    warmup: { executor: 'constant-vus', vus: VUS, duration: WARMUP, gracefulStop: '5s' },
    main: { executor: 'constant-vus', vus: VUS, duration: DURATION, startTime: WARMUP, gracefulStop: '10s' },
  },
  // Declaring thresholds on the tagged sub-metrics makes k6 report them separately,
  // so the warm-up phase is excluded from the published numbers.
  thresholds: {
    'transfer_duration{scenario:main}': ['p(99)<5000'],
    'transfers_completed{scenario:main}': ['count>0'],
    'transfers_rejected{scenario:main}': ['count>=0'],
    'transfers_error{scenario:main}': ['count==0'],
    'transfer_error_rate{scenario:main}': ['rate==0'],
    'ledger_consistent_after_run': ['rate==1'],
  },
};

function login() {
  const res = http.post(`${BASE_URL}/api/auth/login`,
    JSON.stringify({ username: __ENV.LOAD_USERNAME || 'ops.admin', password: __ENV.LOAD_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  if (res.status !== 200) fail(`login failed with HTTP ${res.status}`);
  return res.json('accessToken');
}

export function setup() {
  if (!__ENV.LOAD_PASSWORD) fail('LOAD_PASSWORD is not set');
  const token = login();
  const res = http.get(`${BASE_URL}/api/accounts?pageSize=200&status=Active`, { headers: { Authorization: `Bearer ${token}` } });
  if (res.status !== 200) fail(`listing accounts failed with HTTP ${res.status}`);
  const accounts = res.json('items').map((a) => a.id);
  if (accounts.length < 2) fail('at least two active accounts are required (run the demo seed)');
  return { token, accounts, run: `${Date.now()}` };
}

export default function (data) {
  const from = data.accounts[Math.floor(Math.random() * data.accounts.length)];
  let to = from;
  while (to === from) to = data.accounts[Math.floor(Math.random() * data.accounts.length)];
  // Rp 1,000 - Rp 50,000 expressed in minor units.
  const amount = (1000 + Math.floor(Math.random() * 49001)) * 100;

  const res = http.post(`${BASE_URL}/api/transfers`,
    JSON.stringify({ sourceAccountId: from, destinationAccountId: to, amountMinorUnits: amount, currency: 'IDR' }),
    {
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${data.token}`,
        'Idempotency-Key': `k6-${data.run}-${exec.scenario.name}-${__VU}-${__ITER}`,
      },
      // 422 is a valid business outcome (insufficient funds), not a failure.
      responseCallback: http.expectedStatuses(201, 422),
    });

  transferDuration.add(res.timings.duration);
  if (res.status === 201) completed.add(1);
  else if (res.status === 422) rejected.add(1);
  else errors.add(1);
  errorRate.add(res.status !== 201 && res.status !== 422);
  check(res, { 'transfer answered 201 or 422': (r) => r.status === 201 || r.status === 422 });
}

export function teardown(data) {
  // After the load, the ledger must still satisfy every invariant.
  const token = login();
  const res = http.get(`${BASE_URL}/api/ledger/verify`, { headers: { Authorization: `Bearer ${token}` }, timeout: '120s' });
  ledgerConsistent.add(res.status === 200 && res.json('isConsistent') === true);
}

function metric(data, name) {
  return data.metrics[name] ? data.metrics[name].values : null;
}

export function handleSummary(data) {
  const duration = metric(data, 'transfer_duration{scenario:main}') || {};
  const done = (metric(data, 'transfers_completed{scenario:main}') || {}).count || 0;
  const declined = (metric(data, 'transfers_rejected{scenario:main}') || {}).count || 0;
  const failed = (metric(data, 'transfers_error{scenario:main}') || {}).count || 0;
  const total = done + declined + failed;
  const seconds = parseDuration(DURATION);

  const summary = {
    benchmark: 'k6_full_stack_transfers',
    recordedAt: new Date().toISOString(),
    baseUrl: BASE_URL,
    virtualUsers: VUS,
    measuredDuration: DURATION,
    warmupDuration: WARMUP,
    requests: total,
    requestsPerSecond: seconds > 0 ? total / seconds : null,
    completed: done,
    rejectedByEngine: declined,
    errors: failed,
    successRate: total > 0 ? done / total : null,
    businessRejectionRate: total > 0 ? declined / total : null,
    errorRate: total > 0 ? failed / total : null,
    latencyMs: {
      avg: duration.avg, min: duration.min, p50: duration.med, p90: duration['p(90)'],
      p95: duration['p(95)'], p99: duration['p(99)'], max: duration.max,
    },
    ledgerConsistentAfterRun: (metric(data, 'ledger_consistent_after_run') || {}).rate === 1,
  };

  const text = [
    '',
    `k6 full-stack transfers: ${VUS} VUs, ${DURATION} measured after ${WARMUP} warm-up`,
    `  requests            ${total}  (${summary.requestsPerSecond ? summary.requestsPerSecond.toFixed(1) : '-'} req/s)`,
    `  completed           ${done}`,
    `  rejected by engine  ${declined}`,
    `  errors              ${failed}`,
    `  latency ms          p50 ${fmt(duration.med)}  p95 ${fmt(duration['p(95)'])}  p99 ${fmt(duration['p(99)'])}  max ${fmt(duration.max)}`,
    `  ledger consistent   ${summary.ledgerConsistentAfterRun}`,
    '',
  ].join('\n');

  const out = { stdout: text };
  out[`${RESULTS}/k6-summary.json`] = JSON.stringify(summary, null, 2);
  return out;
}

function fmt(v) {
  return typeof v === 'number' ? v.toFixed(2) : '-';
}

function parseDuration(text) {
  const m = /^(\d+)(ms|s|m|h)$/.exec(text);
  if (!m) return 0;
  const n = parseInt(m[1], 10);
  return m[2] === 'ms' ? n / 1000 : m[2] === 's' ? n : m[2] === 'm' ? n * 60 : n * 3600;
}
