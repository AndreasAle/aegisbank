# Benchmarks

All numbers in this document were measured on the machine described below on
2026-11-16. They describe that machine on that day. They are not targets,
not guarantees, and not comparable with other hardware. Raw outputs are in
`benchmarks/results/`.

## Environment

| | |
|---|---|
| CPU | AMD Ryzen 7 6800H (8 cores / 16 threads), laptop |
| Host OS | Windows 11 Home (build 26200) |
| Execution | Docker Engine 28.4 inside WSL2 (kernel 6.6.87.2-microsoft-standard-WSL2), Ubuntu 24.04 containers |
| Memory available to WSL2 | 6.6 GiB |
| C++ compiler | GCC 13.3, `-O3`, LTO, `-fno-tree-slp-vectorize` |
| .NET | SDK 10.0.404, runtime 10.0.15, RyuJIT x86-64-v3 |
| BenchmarkDotNet | 0.15.8 |
| PostgreSQL | 17 (alpine image), default settings except `max_connections=200` |
| Load generator | k6 (grafana/k6 image), same machine |

The load generator, the API and the database share one laptop CPU and 6.6 GiB
of memory inside a virtual machine. Full-stack figures are therefore
pessimistic about the service and optimistic about nothing; treat them as an
order of magnitude.

## What is measured

| # | Layer | Tool | Includes | Excludes |
|---|---|---|---|---|
| 1 | Engine logic | `aegis_engine_bench` | validation, checked arithmetic, decision | runtime, database |
| 2 | Engine via C ABI | `aegis_engine_bench` | the above + struct conversion at the ABI | managed code |
| 3 | Engine via .NET | BenchmarkDotNet | the above + P/Invoke transition + managed wrapper | database |
| 4 | C# reference | BenchmarkDotNet | same rules written in C# | native code |
| 5 | Full stack | k6 | HTTP, JWT validation, coordinator, engine, PostgreSQL transaction, commit | network (loopback) |

Layers 1–4 are CPU-only. Layer 5 is dominated by the database.

## Method

**Native (`src/AegisBank.Engine/bench/engine_bench.cpp`).** A seeded dataset
of requests is generated once: random balances and amounts so that roughly
40% are approved, the rest rejected for insufficient funds, plus about 3%
closed-account, zero-amount and currency-mismatch cases. One untimed warm-up
pass, then 15 timed passes of at least 1,000,000 evaluations; the median pass
is reported. A checksum over every decision is accumulated so the work cannot
be optimised away, and the three code paths must produce the same checksum.
Two dataset sizes are run: 1,000,000 distinct requests (the branch predictor
cannot learn the outcomes) and 4,096 requests replayed (it can).

**BenchmarkDotNet (`benchmarks/AegisBank.Benchmarks`).** 4,096 seeded
requests of the same mix. 6 warm-up and 15 measured iterations, one process
launch, `MemoryDiagnoser` on. Results are per evaluated transfer
(`OperationsPerInvoke = 4096`). Before measuring, the program checks that the
native engine and the C# reference return identical decisions for 500,000
randomised requests and a set of edge cases, and aborts otherwise.

**k6 (`benchmarks/k6/transfers.js`).** Constant 32 virtual users, each
looping without think time: pick two different accounts at random from the
nine seeded active accounts, transfer a random Rp 1.000–50.000 with a unique
idempotency key. 15 s warm-up followed by 60 s measured; only the measured
phase is reported (k6 sub-metrics tagged by scenario). The API rate limiter
is disabled for the run. HTTP 201 and 422 are both valid outcomes; anything
else counts as an error. After the run, `GET /api/ledger/verify` must report
a consistent ledger.

The dataset is deliberately small (nine accounts), so most transfers contend
for the same few rows. This stresses the locking path; a wider account
population would contend less.

## Results

### Engine (CPU only)

| Path | Dataset | Time per decision |
|---|---|---:|
| C++ domain function, called from C++ | 1,000,000 distinct | 7.7 ns |
| C++ domain function, called from C++ | 4,096 replayed | 4.5 ns |
| C ABI (`aegis_evaluate_transfer`), called from C++ | 1,000,000 distinct | 25.4 ns |
| C ABI, called from C++ | 4,096 replayed | 26.1 ns |
| C ABI batch, called from C++ | 4,096 replayed | 28.7 ns |
| C ABI via raw P/Invoke from .NET | 4,096 replayed | 29.5 ns (σ 0.9) |
| C ABI batch via one P/Invoke per 4,096 | 4,096 replayed | 29.3 ns (σ 0.8) |
| **Production path:** `NativeTransferEngine.Evaluate` | 4,096 replayed | 64.9 ns (σ 2.4) |
| C# reference implementation | 4,096 replayed | 5.6 ns (σ 0.2) |

No managed allocation on any path.

### Reading these numbers

- **The C# reference is faster than the C++ engine as it is called in
  production: about 5.6 ns against 65 ns.** For a function this small, C++
  does not win. The logic itself is comparable in both languages (4.5 ns in
  C++, 5.6 ns in C#, same dataset size); the difference is everything around
  it.
- **The P/Invoke transition is cheap.** Raw P/Invoke (29.5 ns) is about 3 ns
  more than the same C function called from C++ (26.1 ns), and batching 4,096
  calls into one transition saves nothing measurable.
- **The C ABI layer costs about 20 ns.** Converting the C structs to the
  internal types and back accounts for most of the native time. Two changes
  were measured and kept: link-time optimisation and disabling GCC's SLP
  vectoriser for the engine (together 46 ns → 25 ns). The remaining gap
  between 4.5 ns and 25 ns has not been explained; it was not profiled with
  hardware counters. This is an open item, not a solved one.
- **The managed wrapper costs about 35 ns** on top of raw P/Invoke: filling
  the request struct from value types, converting two GUIDs to and from
  bytes, and mapping the result.
- **None of this matters to transfer latency.** One transfer costs a database
  transaction measured in milliseconds; 65 ns is roughly five orders of
  magnitude below that. The engine is native because the project is about
  the interop boundary and deterministic, checked integer arithmetic, not
  because it makes transfers faster.

### Full stack

Run: 32 virtual users, 15 s warm-up (excluded), 60 s measured, nine accounts,
rate limiter off, `Development` environment (per-transfer console logging on).

| Metric | Value |
|---|---:|
| Transfers submitted | 15,654 |
| Throughput | 260.9 requests/s |
| Completed (HTTP 201) | 15,654 (100%) |
| Rejected by the engine (HTTP 422) | 0 (0%) |
| Errors (any other status) | 0 (0%) |
| Latency p50 | 27.5 ms |
| Latency p95 | 745.4 ms |
| Latency p99 | 1,840 ms |
| Latency max | 4,783 ms |
| Deadlock / serialization retries logged by the API | 0 |
| Ledger verification after the run | consistent |

Reading these numbers:

- **Correctness held under load.** No errors, no retries, and all eleven
  ledger invariants passed afterwards with about 15,700 new journals posted
  per minute.
- **The median is the cost of one transfer** (about 27 ms on this setup:
  several round trips and a synchronous commit on a WSL2 virtual disk).
- **The tail is lock queueing, by construction.** 32 clients send transfers
  between only nine accounts, so most requests wait for a row lock held by
  another transfer. Requests on the same account are served one at a time;
  the p95 and p99 show the length of that queue, not a fault. A larger
  account population would shorten the tail. That experiment has not been
  run, so no number is claimed for it.
- No rejections occurred because amounts (up to Rp 50.000) were small
  relative to the seeded balances.

## Reproducing

See [benchmarks/README.md](../benchmarks/README.md).
