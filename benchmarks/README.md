# Benchmarks

| Path | What |
|---|---|
| `../src/AegisBank.Engine/bench/engine_bench.cpp` | Native CPU-only benchmark (built as `aegis_engine_bench`) |
| `AegisBank.Benchmarks/` | BenchmarkDotNet: C++ engine through P/Invoke vs. a C# reference implementation |
| `k6/transfers.js` | Full-stack load test against the running API |
| `summarize.py` | Merges whatever raw results exist into `results/summary.json`, `summary.csv`, `RESULTS.md` |
| `results/` | Raw outputs and summaries of the last recorded run |

`AegisBank.Benchmarks/ManagedReferenceEngine.cs` is a benchmark-only C#
implementation of the engine's rules. Nothing outside this folder references
it. The benchmark aborts if it disagrees with the native engine on any of
500,000 randomised requests.

## Running

```bash
# 1. Native engine (argument 2 = dataset size, default 1,000,000)
bash scripts/build-engine.sh
build/engine/aegis_engine_bench benchmarks/results/native-engine.json
build/engine/aegis_engine_bench benchmarks/results/native-engine-4096.json 4096

# 2. Engine through .NET (takes a few minutes)
dotnet run -c Release --project benchmarks/AegisBank.Benchmarks -- benchmarks/results/bdn

# 3. Full stack. The rate limiter must be off or the test measures the limiter.
RATE_LIMITING_ENABLED=false docker compose up --build -d
bash scripts/wait-healthy.sh
RATE_LIMITING_ENABLED=false docker compose --profile bench run --rm k6
docker compose up -d          # restores the rate limiter

# 4. Summary (also feeds the dashboard's System monitoring page)
python benchmarks/summarize.py --machine "CPU, memory, OS"
```

k6 parameters come from `.env`: `LOAD_VUS`, `LOAD_DURATION`, `LOAD_WARMUP`.

Method and interpretation: [../docs/benchmarks.md](../docs/benchmarks.md).
