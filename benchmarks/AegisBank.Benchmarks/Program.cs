using AegisBank.Application;
using AegisBank.Benchmarks;
using AegisBank.Domain;
using AegisBank.Infrastructure.Native;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release --project benchmarks/AegisBank.Benchmarks -- [artifactsDir]
//
// CPU-only comparison of the native C++ engine (through P/Invoke) with a
// logically equivalent C# implementation. No database is involved.

var artifacts = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts");

var mismatches = EquivalenceCheck.Run(cases: 500_000);
if (mismatches != 0)
{
    Console.Error.WriteLine($"Native and managed engines disagree on {mismatches} cases; benchmark aborted.");
    return 2;
}
Console.WriteLine("Equivalence check passed: native and managed engines agree on 500,000 randomised cases.");

var config = ManualConfig.CreateEmpty()
    // An explicit id: BenchmarkDotNet uses the job id in generated project paths.
    .AddJob(new Job("EngineJob") { Run = { WarmupCount = 6, IterationCount = 15, LaunchCount = 1 } })
    .AddLogger(ConsoleLogger.Default)
    .AddColumnProvider(DefaultColumnProviders.Instance)
    .AddExporter(JsonExporter.Full, MarkdownExporter.GitHub, CsvExporter.Default)
    .WithArtifactsPath(artifacts);

var summary = BenchmarkRunner.Run<EngineBenchmarks>(config);
return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;

namespace AegisBank.Benchmarks
{
    public static class Dataset
    {
        public const int Size = 4096;
        public const uint Idr = 360;

        /// <summary>Seeded mix: mostly approvals and insufficient-funds, a few status/currency rejections.</summary>
        public static (EngineAccount Debit, EngineAccount Credit, long Amount)[] Create(int size = Size, int seed = 20261116)
        {
            var random = new Random(seed);
            var items = new (EngineAccount, EngineAccount, long)[size];
            for (var i = 0; i < size; i++)
            {
                var dice = random.Next(100);
                var debit = new EngineAccount(NewId(random), random.NextInt64(0, 50_000_000), Idr, AccountStatus.Active, NormalSide.Credit);
                var credit = new EngineAccount(NewId(random), random.NextInt64(0, 50_000_000),
                    dice == 2 ? 840u : Idr, dice == 0 ? AccountStatus.Closed : AccountStatus.Active, NormalSide.Credit);
                var amount = dice == 1 ? 0 : random.NextInt64(1, 60_000_000);
                items[i] = (debit, credit, amount);
            }
            return items;
        }

        private static Guid NewId(Random random)
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }
    }

    public static class EquivalenceCheck
    {
        public static int Run(int cases)
        {
            var native = new NativeTransferEngine();
            var managed = new ManagedReferenceEngine();
            var mismatches = 0;
            foreach (var (debit, credit, amount) in Dataset.Create(cases, seed: 7))
            {
                if (native.Evaluate(debit, credit, amount, Dataset.Idr) != managed.Evaluate(debit, credit, amount, Dataset.Idr))
                    mismatches++;
            }
            // Edge cases that random data rarely hits.
            var a = new EngineAccount(Guid.NewGuid(), long.MaxValue, Dataset.Idr, AccountStatus.Active, NormalSide.Credit);
            var b = new EngineAccount(Guid.NewGuid(), 10, Dataset.Idr, AccountStatus.Active, NormalSide.Credit);
            var vault = new EngineAccount(Guid.NewGuid(), long.MaxValue, Dataset.Idr, AccountStatus.Active, NormalSide.Debit);
            (EngineAccount, EngineAccount, long)[] edges =
            [
                (b, a, 1), (a, b, long.MaxValue), (a, a, 5), (b, a, long.MinValue), (vault, b, 1), (b, vault, 5), (b, vault, 50),
                (b with { BalanceMinorUnits = -1 }, a, 1), (b with { Status = AccountStatus.Frozen }, a, 1),
            ];
            foreach (var (debit, credit, amount) in edges)
            {
                if (native.Evaluate(debit, credit, amount, Dataset.Idr) != managed.Evaluate(debit, credit, amount, Dataset.Idr))
                    mismatches++;
            }
            return mismatches;
        }
    }

    [MemoryDiagnoser]
    public unsafe class EngineBenchmarks
    {
        private (EngineAccount Debit, EngineAccount Credit, long Amount)[] _data = [];
        private AegisTransferRequest[] _requests = [];
        private AegisTransferDecision[] _decisions = [];
        private readonly NativeTransferEngine _native = new();
        private readonly ManagedReferenceEngine _managed = new();

        [GlobalSetup]
        public void Setup()
        {
            _data = Dataset.Create();
            _requests = new AegisTransferRequest[_data.Length];
            _decisions = new AegisTransferDecision[_data.Length];
            for (var i = 0; i < _data.Length; i++)
            {
                ref var r = ref _requests[i];
                r.StructSize = (uint)sizeof(AegisTransferRequest);
                r.AbiVersion = NativeMethods.ExpectedAbiVersion;
                Fill(ref r.DebitAccount, _data[i].Debit);
                Fill(ref r.CreditAccount, _data[i].Credit);
                r.AmountMinor = _data[i].Amount;
                r.Currency = Dataset.Idr;
            }
            if (!_native.GetInfo().Available) throw new InvalidOperationException("Native engine is not available.");
        }

        private static void Fill(ref AegisAccountSnapshot target, EngineAccount source)
        {
            fixed (byte* id = target.AccountId) source.Id.TryWriteBytes(new Span<byte>(id, 16));
            target.BalanceMinor = source.BalanceMinorUnits;
            target.Currency = source.CurrencyNumeric;
            target.Status = (byte)source.Status;
            target.NormalSide = (byte)source.NormalSide;
        }

        /// <summary>Production path: managed wrapper, one P/Invoke per decision.</summary>
        [Benchmark(Description = "C++ engine via wrapper (1 P/Invoke per transfer)", OperationsPerInvoke = Dataset.Size)]
        public long NativeWrapper()
        {
            long acc = 0;
            foreach (var (debit, credit, amount) in _data)
                acc += _native.Evaluate(debit, credit, amount, Dataset.Idr).DebitAccountBalanceAfter;
            return acc;
        }

        /// <summary>Raw P/Invoke with pre-built structs: isolates the cost of crossing the boundary.</summary>
        [Benchmark(Description = "C++ engine raw P/Invoke (pre-built structs)", OperationsPerInvoke = Dataset.Size)]
        public long NativeRaw()
        {
            long acc = 0;
            AegisTransferDecision decision = default;
            fixed (AegisTransferRequest* requests = _requests)
            {
                for (var i = 0; i < _requests.Length; i++)
                {
                    decision.StructSize = (uint)sizeof(AegisTransferDecision);
                    NativeMethods.aegis_evaluate_transfer(requests + i, &decision);
                    acc += decision.DebitAccountBalanceAfter;
                }
            }
            return acc;
        }

        /// <summary>One boundary crossing for the whole batch.</summary>
        [Benchmark(Description = "C++ engine batch (1 P/Invoke per 4096 transfers)", OperationsPerInvoke = Dataset.Size)]
        public long NativeBatch()
        {
            long acc = 0;
            fixed (AegisTransferRequest* requests = _requests)
            fixed (AegisTransferDecision* decisions = _decisions)
            {
                for (var i = 0; i < _decisions.Length; i++) decisions[i].StructSize = (uint)sizeof(AegisTransferDecision);
                NativeMethods.aegis_evaluate_transfer_batch(requests, decisions, (nuint)_requests.Length);
                for (var i = 0; i < _decisions.Length; i++) acc += decisions[i].DebitAccountBalanceAfter;
            }
            return acc;
        }

        [Benchmark(Baseline = true, Description = "C# reference implementation", OperationsPerInvoke = Dataset.Size)]
        public long Managed()
        {
            long acc = 0;
            foreach (var (debit, credit, amount) in _data)
                acc += _managed.Evaluate(debit, credit, amount, Dataset.Idr).DebitAccountBalanceAfter;
            return acc;
        }
    }
}
