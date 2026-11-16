#!/usr/bin/env python3
"""Collects raw benchmark outputs into one summary.

Inputs (all optional; whatever exists under benchmarks/results is used):
  native-engine.json                         from aegis_engine_bench
  bdn/results/*-report-full.json             from BenchmarkDotNet
  k6-summary.json                            from the k6 load test

Outputs:
  summary.json   read by the dashboard's System monitoring page
  summary.csv    the same rows as CSV
  RESULTS.md     human-readable report

Nothing is estimated or filled in: a suite that was not run simply has no rows.

Usage:
  python benchmarks/summarize.py --machine "CPU, RAM, OS" [--notes "..."]
"""
import argparse
import csv
import datetime
import glob
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(ROOT, "results")


def load(path):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def native_rows():
    rows, metas = [], []
    for path in sorted(glob.glob(os.path.join(RESULTS, "native-engine*.json"))):
        data = load(path)
        if not data:
            continue
        label = f'dataset {data["dataset_size"]:,}'
        for result in data["results"]:
            name = f'{result["name"]} ({label})'
            rows.append(("Native C++ (no runtime)", name, "median time per decision", result["median_ns_per_op"], "ns"))
            rows.append(("Native C++ (no runtime)", name, "throughput", result["ops_per_second"], "decisions/s"))
        metas.append(f'{data["compiler"]}, {label} requests, {data["repetitions"]} timed passes after {data["warmup_passes"]} warm-up')
    return rows, "; ".join(metas) if metas else None


def bdn_rows():
    files = sorted(glob.glob(os.path.join(RESULTS, "bdn", "results", "*-report-full.json")))
    if not files:
        return [], None
    data = load(files[-1])
    if not data:
        return [], None
    rows = []
    for bench in data["Benchmarks"]:
        stats = bench.get("Statistics") or {}
        name = bench.get("MethodTitle") or bench.get("Method")
        if "Mean" in stats:
            rows.append(("Engine via .NET (BenchmarkDotNet)", name, "mean time per decision", stats["Mean"], "ns"))
            rows.append(("Engine via .NET (BenchmarkDotNet)", name, "standard deviation", stats["StandardDeviation"], "ns"))
        memory = bench.get("Memory") or {}
        if "BytesAllocatedPerOperation" in memory:
            rows.append(("Engine via .NET (BenchmarkDotNet)", name, "allocated per decision", memory["BytesAllocatedPerOperation"], "bytes"))
    env = data.get("HostEnvironmentInfo", {})
    meta = f'{env.get("BenchmarkDotNetCaption", "BenchmarkDotNet")} {env.get("BenchmarkDotNetVersion", "")}, {env.get("RuntimeVersion", "")}'
    return rows, meta


def k6_rows():
    data = load(os.path.join(RESULTS, "k6-summary.json"))
    if not data:
        return [], None
    suite = "Full stack (k6: HTTP + engine + PostgreSQL)"
    name = f'{data["virtualUsers"]} VUs, {data["measuredDuration"]}'
    latency = data["latencyMs"]
    rows = [
        (suite, name, "requests", data["requests"], "requests"),
        (suite, name, "throughput", data["requestsPerSecond"], "requests/s"),
        (suite, name, "latency p50", latency["p50"], "ms"),
        (suite, name, "latency p95", latency["p95"], "ms"),
        (suite, name, "latency p99", latency["p99"], "ms"),
        (suite, name, "latency max", latency["max"], "ms"),
        (suite, name, "completed (success rate)", data["successRate"] * 100 if data["successRate"] is not None else None, "%"),
        (suite, name, "rejected by engine", data["businessRejectionRate"] * 100 if data["businessRejectionRate"] is not None else None, "%"),
        (suite, name, "errors (failure rate)", data["errorRate"] * 100 if data["errorRate"] is not None else None, "%"),
    ]
    rows = [r for r in rows if r[3] is not None]
    meta = (f'warm-up {data["warmupDuration"]} (excluded), ledger consistent after run: '
            f'{"yes" if data["ledgerConsistentAfterRun"] else "NO"}')
    return rows, meta


def fmt(value):
    if value >= 1000:
        return f"{value:,.0f}"
    if value >= 10:
        return f"{value:.2f}"
    return f"{value:.3f}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--machine", default=None, help="hardware and OS the benchmarks ran on")
    parser.add_argument("--notes", default="", help="free-form remarks stored with the summary")
    args = parser.parse_args()

    sections = [("Native C++ engine", *native_rows()), ("Engine through .NET", *bdn_rows()), ("Full stack", *k6_rows())]
    rows = [row for _, section_rows, _ in sections for row in section_rows]
    if not rows:
        print("no benchmark results found under", RESULTS)
        return 1

    recorded = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    summary = {
        "recordedAt": recorded.isoformat().replace("+00:00", "Z"),
        "machine": args.machine,
        "notes": args.notes,
        "entries": [{"suite": s, "name": n, "metric": m, "value": v, "unit": u} for s, n, m, v, u in rows],
    }
    with open(os.path.join(RESULTS, "summary.json"), "w", encoding="utf-8") as handle:
        json.dump(summary, handle, indent=2)

    with open(os.path.join(RESULTS, "summary.csv"), "w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["suite", "benchmark", "metric", "value", "unit"])
        writer.writerows(rows)

    lines = ["# Benchmark results", "",
             f"Recorded {summary['recordedAt']}.", "",
             f"Machine: {args.machine or 'not recorded'}", ""]
    if args.notes:
        lines += [args.notes, ""]
    lines += ["These figures are measurements from one machine. They are not guarantees and are not",
              "comparable across machines. See `docs/benchmarks.md` for the method.", ""]
    for title, section_rows, meta in sections:
        if not section_rows:
            lines += [f"## {title}", "", "Not run.", ""]
            continue
        lines += [f"## {title}", "", meta or "", "", "| Benchmark | Metric | Value | Unit |", "|---|---|---:|---|"]
        lines += [f"| {n} | {m} | {fmt(v)} | {u} |" for _, n, m, v, u in section_rows]
        lines.append("")
    with open(os.path.join(RESULTS, "RESULTS.md"), "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))

    print(f"wrote {len(rows)} rows to summary.json, summary.csv and RESULTS.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
