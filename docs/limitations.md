# Known limitations

AegisBank is an educational simulation. It does not connect to any banking
network, does not process real payments and is not certified for production
use. The points below are deliberate scope limits or known gaps.

## Functional scope

- **Internal transfers only.** No external rails, clearing, settlement, FX,
  fees, interest, holds, scheduled or batch payments.
- **One currency (IDR).** The schema and engine are currency-aware, but
  nothing converts between currencies.
- **Two-line journals.** The engine produces exactly one debit and one credit
  per posting. The schema and triggers allow more lines; no code path creates
  them.
- **Available balance equals ledger balance.** There are no pending
  authorisations or reserved funds.
- **No overdraft facility.** Every account has a hard floor of zero.
- **Customers are not self-service.** There is no registration, password
  reset, MFA or customer-facing UI; customers exist as API users only.

## Consistency and operations

- **Idempotency records are never purged.** `expires_at` is stored but no job
  deletes expired rows, so replay works indefinitely and the table grows
  without bound.
- **Balance projections can be altered by a privileged database session.**
  Constraints keep them non-negative and reconciliation detects the drift,
  but nothing prevents the write itself.
- **Reconciliation scans the whole ledger.** `GET /api/ledger/verify` and the
  Ledger explorer page recompute every check on each call. That is fine for a
  demo dataset and becomes slow as the ledger grows; a real system would
  reconcile incrementally.
- **Hot accounts serialise.** All postings on one account queue on its row
  lock. Throughput for a single very busy account (such as Vault cash during
  mass deposits) is bounded by commit latency.
- **Single database.** No replication, failover, backup or partitioning is
  configured.
- **Migrations run at API start-up in the Compose stack** with the same
  database role the application uses.

## Security

- No independent security review or penetration test has been performed.
- **Access tokens cannot be revoked individually.** A disabled user keeps API
  access until the token expires (10 minutes).
- **Symmetric JWT signing key** shared by all API instances; no key rotation.
- **Rate limits are per process and in memory.**
- **Lockout is per account,** which lets an attacker lock a known username.
- The Compose stack serves plain HTTP on localhost.
- No field-level encryption, no secrets manager integration, no database
  row-level security.

## Dashboard

- The "Open account" page creates a customer, an account and an optional
  deposit in three steps. Only the deposit is idempotent, so a double submit
  can create a duplicate customer.
- The overview chart shows the last 14 days in UTC only.
- Benchmark results appear only when `benchmarks/results/summary.json` exists
  and is mounted into the container.

## Benchmarks

- Results come from one developer laptop running Docker inside WSL2, with the
  load generator, API and database sharing the same CPU and memory. They
  indicate orders of magnitude on that machine and nothing more.
- The native-versus-managed comparison measures a very small function. At
  this scale the cost of crossing the P/Invoke boundary and converting
  arguments is the same order as the work itself.

## Verification status

What was and was not executed during development is listed in the README
under *Verification status*.
