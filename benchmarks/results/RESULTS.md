# Benchmark results

Recorded 2026-11-16T05:14:12Z.

Machine: AMD Ryzen 7 6800H (8C/16T) laptop, Windows 11, Docker 28.4 in WSL2 (6.6 GiB RAM), Ubuntu 24.04 containers

Load generator, API and PostgreSQL shared this one machine. GCC 13.3, .NET 10.0.15, PostgreSQL 17.

These figures are measurements from one machine. They are not guarantees and are not
comparable across machines. See `docs/benchmarks.md` for the method.

## Native C++ engine

gcc 13.3, dataset 4,096 requests, 15 timed passes after 1 warm-up; gcc 13.3, dataset 1,000,000 requests, 15 timed passes after 1 warm-up

| Benchmark | Metric | Value | Unit |
|---|---|---:|---|
| cpp_domain_evaluate_transfer (dataset 4,096) | median time per decision | 4.512 | ns |
| cpp_domain_evaluate_transfer (dataset 4,096) | throughput | 221,628,703 | decisions/s |
| c_abi_evaluate_transfer (dataset 4,096) | median time per decision | 26.10 | ns |
| c_abi_evaluate_transfer (dataset 4,096) | throughput | 38,313,058 | decisions/s |
| c_abi_evaluate_transfer_batch (dataset 4,096) | median time per decision | 28.70 | ns |
| c_abi_evaluate_transfer_batch (dataset 4,096) | throughput | 34,842,834 | decisions/s |
| cpp_domain_evaluate_transfer (dataset 1,000,000) | median time per decision | 7.744 | ns |
| cpp_domain_evaluate_transfer (dataset 1,000,000) | throughput | 129,133,115 | decisions/s |
| c_abi_evaluate_transfer (dataset 1,000,000) | median time per decision | 25.45 | ns |
| c_abi_evaluate_transfer (dataset 1,000,000) | throughput | 39,297,196 | decisions/s |
| c_abi_evaluate_transfer_batch (dataset 1,000,000) | median time per decision | 39.99 | ns |
| c_abi_evaluate_transfer_batch (dataset 1,000,000) | throughput | 25,003,677 | decisions/s |

## Engine through .NET

BenchmarkDotNet 0.15.8, .NET 10.0.15 (10.0.15, 10.0.1526.53008)

| Benchmark | Metric | Value | Unit |
|---|---|---:|---|
| 'C++ engine via wrapper (1 P/Invoke per transfer)' | mean time per decision | 64.91 | ns |
| 'C++ engine via wrapper (1 P/Invoke per transfer)' | standard deviation | 2.418 | ns |
| 'C++ engine via wrapper (1 P/Invoke per transfer)' | allocated per decision | 0.000 | bytes |
| 'C++ engine raw P/Invoke (pre-built structs)' | mean time per decision | 29.48 | ns |
| 'C++ engine raw P/Invoke (pre-built structs)' | standard deviation | 0.911 | ns |
| 'C++ engine raw P/Invoke (pre-built structs)' | allocated per decision | 0.000 | bytes |
| 'C++ engine batch (1 P/Invoke per 4096 transfers)' | mean time per decision | 29.34 | ns |
| 'C++ engine batch (1 P/Invoke per 4096 transfers)' | standard deviation | 0.846 | ns |
| 'C++ engine batch (1 P/Invoke per 4096 transfers)' | allocated per decision | 0.000 | bytes |
| 'C# reference implementation' | mean time per decision | 5.583 | ns |
| 'C# reference implementation' | standard deviation | 0.238 | ns |
| 'C# reference implementation' | allocated per decision | 0.000 | bytes |

## Full stack

warm-up 15s (excluded), ledger consistent after run: yes

| Benchmark | Metric | Value | Unit |
|---|---|---:|---|
| 32 VUs, 60s | requests | 15,654 | requests |
| 32 VUs, 60s | throughput | 260.90 | requests/s |
| 32 VUs, 60s | latency p50 | 27.48 | ms |
| 32 VUs, 60s | latency p95 | 745.38 | ms |
| 32 VUs, 60s | latency p99 | 1,840 | ms |
| 32 VUs, 60s | latency max | 4,783 | ms |
| 32 VUs, 60s | completed (success rate) | 100.00 | % |
| 32 VUs, 60s | rejected by engine | 0.000 | % |
| 32 VUs, 60s | errors (failure rate) | 0.000 | % |
