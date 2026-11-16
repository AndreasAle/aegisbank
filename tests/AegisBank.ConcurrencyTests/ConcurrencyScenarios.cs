using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AegisBank.ConcurrencyTests;

/// <summary>
/// The three scenarios required by the specification, plus lock-ordering and
/// contention cases. Everything runs against real PostgreSQL through the
/// production transfer coordinator and the native engine.
/// </summary>
public sealed class ConcurrencyScenarios : IAsyncLifetime
{
    private const long Rp = TestBank.Rupiah;

    private TestDatabase _database = null!;
    private ServiceProvider _instanceA = null!;
    private ServiceProvider _instanceB = null!;
    private Actor _admin = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        // Two service containers on one database stand in for two application instances.
        _instanceA = _database.BuildServices();
        _instanceB = _database.BuildServices();
        _admin = await TestBank.CreateAdminAsync(_database);
    }

    public async Task DisposeAsync()
    {
        await _instanceA.DisposeAsync();
        await _instanceB.DisposeAsync();
        await _database.DisposeAsync();
    }

    private ITransferService Transfers(int i) =>
        (i % 2 == 0 ? _instanceA : _instanceB).GetRequiredService<ITransferService>();

    /// <summary>Starts all operations at the same instant.</summary>
    private static async Task<T[]> RunTogetherAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await operation(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    // ------------------------------------------------------------------ Scenario A

    [Fact]
    public async Task ScenarioA_TwoConcurrentTransfersOf80kFrom100k_ExactlyOneSucceeds()
    {
        // Repeated so that both interleavings (and both instances winning) are exercised.
        for (var round = 0; round < 25; round++)
        {
            var source = await TestBank.OpenAccountAsync(_instanceA, 100_000 * Rp);
            var destination1 = await TestBank.OpenAccountAsync(_instanceA, 0);
            var destination2 = await TestBank.OpenAccountAsync(_instanceA, 0);
            var destinations = new[] { destination1.Id, destination2.Id };

            var outcomes = await RunTogetherAsync(2, i => Transfers(i).SubmitTransferAsync(_admin,
                new SubmitTransfer(source.Id, destinations[i], 80_000 * Rp, "IDR"), $"scenario-a-{round}-{i}"));

            Assert.Equal(1, outcomes.Count(o => o.StatusCode == 201));
            var failed = Assert.Single(outcomes, o => o.StatusCode == 422);
            Assert.Equal("INSUFFICIENT_FUNDS", failed.Transfer.FailureCode);
            Assert.Null(failed.Transfer.JournalEntryId);

            Assert.Equal(20_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
            var received = await TestBank.BalanceAsync(_database, destination1.Id) + await TestBank.BalanceAsync(_database, destination2.Id);
            Assert.Equal(80_000 * Rp, received);
        }

        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceA));
    }

    // ------------------------------------------------------------------ Scenario B

    [Fact]
    public async Task ScenarioB_HundredIdenticalRequestsWithOneKey_CommitOneTransfer()
    {
        var source = await TestBank.OpenAccountAsync(_instanceA, 1_000_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(_instanceA, 0);
        var request = new SubmitTransfer(source.Id, destination.Id, 25_000 * Rp, "IDR");

        var outcomes = await RunTogetherAsync(100, i => Transfers(i).SubmitTransferAsync(_admin, request, "scenario-b-single-key"));

        // Every caller received the same committed result.
        Assert.All(outcomes, o => Assert.Equal(201, o.StatusCode));
        Assert.Single(outcomes.Select(o => o.Transfer.Id).Distinct());
        Assert.Single(outcomes.Select(o => o.Transfer.ReferenceNumber).Distinct());
        Assert.Single(outcomes.Select(o => o.Transfer.JournalEntryId).Distinct());
        Assert.Equal(1, outcomes.Count(o => !o.Replayed));
        Assert.Equal(99, outcomes.Count(o => o.Replayed));

        // Exactly one financial effect.
        Assert.Equal(975_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(25_000 * Rp, await TestBank.BalanceAsync(_database, destination.Id));

        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Transfers.CountAsync(t => t.IdempotencyKey == "scenario-b-single-key"));
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync(r => r.IdempotencyKey == "scenario-b-single-key"));
        var transferId = outcomes[0].Transfer.Id;
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.TransferId == transferId));
        Assert.Equal(2, await db.LedgerLines.CountAsync(l => l.JournalEntry!.TransferId == transferId));

        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceB));
    }

    [Fact]
    public async Task ScenarioB_SameKeyWithDifferentPayloadConcurrently_OneCommitsOthersConflict()
    {
        var source = await TestBank.OpenAccountAsync(_instanceA, 1_000_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(_instanceA, 0);

        // 20 callers share one key but each asks for a different amount.
        var results = await RunTogetherAsync(20, async i =>
        {
            try
            {
                var outcome = await Transfers(i).SubmitTransferAsync(_admin,
                    new SubmitTransfer(source.Id, destination.Id, (i + 1) * 1_000 * Rp, "IDR"), "scenario-b-conflict-key");
                return (Outcome: (TransferOutcome?)outcome, Error: (AppException?)null);
            }
            catch (AppException ex)
            {
                return (Outcome: null, Error: ex);
            }
        });

        var committed = Assert.Single(results, r => r.Outcome is not null).Outcome!;
        Assert.All(results.Where(r => r.Error is not null), r =>
        {
            Assert.Equal(ErrorKind.Conflict, r.Error!.Kind);
            Assert.Equal("idempotency_key_conflict", r.Error.Code);
        });
        Assert.Equal(1_000_000 * Rp - committed.Transfer.AmountMinorUnits, await TestBank.BalanceAsync(_database, source.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceA));
    }

    // ------------------------------------------------------------------ Scenario C

    [Fact]
    public async Task ScenarioC_ThousandValidConcurrentTransfersAcrossAccounts_PreserveAllInvariants()
    {
        const int accountCount = 20;
        const int transferCount = 1_000;
        const long opening = 10_000_000 * Rp;

        var accounts = new List<AccountDto>();
        for (var i = 0; i < accountCount; i++) accounts.Add(await TestBank.OpenAccountAsync(_instanceA, opening));

        // Deterministic plan. Every transfer is valid whatever the execution order:
        // the worst case outflow of one account (1000 x 5,000) is below its opening balance.
        var random = new Random(20260116);
        var plan = Enumerable.Range(0, transferCount).Select(i =>
        {
            var from = random.Next(accountCount);
            var to = (from + 1 + random.Next(accountCount - 1)) % accountCount;
            return (Index: i, From: from, To: to, Amount: random.Next(1, 5_001) * Rp);
        }).ToArray();

        var expected = accounts.ToDictionary(a => a.Id, _ => opening);
        foreach (var p in plan)
        {
            expected[accounts[p.From].Id] -= p.Amount;
            expected[accounts[p.To].Id] += p.Amount;
        }

        var outcomes = new TransferOutcome[transferCount];
        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (p, ct) =>
        {
            outcomes[p.Index] = await Transfers(p.Index).SubmitTransferAsync(_admin,
                new SubmitTransfer(accounts[p.From].Id, accounts[p.To].Id, p.Amount, "IDR"), $"scenario-c-{p.Index:D4}", ct);
        });

        Assert.All(outcomes, o => Assert.Equal(201, o.StatusCode));
        Assert.All(outcomes, o => Assert.False(o.Replayed));
        Assert.Equal(transferCount, outcomes.Select(o => o.Transfer.Id).Distinct().Count());

        await using var db = _database.CreateContext();
        var balances = await db.Accounts.AsNoTracking().Where(a => a.CustomerId != null)
            .ToDictionaryAsync(a => a.Id, a => a.AvailableBalanceMinorUnits);

        // Each balance equals the sequentially computed expectation: no lost update, no double posting.
        foreach (var account in accounts) Assert.Equal(expected[account.Id], balances[account.Id]);
        Assert.All(balances.Values, b => Assert.True(b >= 0));
        // Internal transfers neither create nor destroy funds.
        Assert.Equal(accountCount * opening, balances.Values.Sum());

        Assert.Equal(transferCount, await db.Transfers.CountAsync(t => t.Kind == TransferKind.Internal));
        Assert.Equal(transferCount, await db.JournalEntries.CountAsync(j => j.Transfer!.Kind == TransferKind.Internal));

        var report = await TestBank.VerifyAsync(_instanceA);
        TestBank.AssertConsistent(report);
        Assert.Equal(report.TotalDebitsMinorUnits, report.TotalCreditsMinorUnits);
        Assert.Equal(report.TotalAssetBalanceMinorUnits, report.TotalLiabilityBalanceMinorUnits);
    }

    // ------------------------------------------------------------------ additional cases

    [Fact]
    public async Task OpposingTransfersBetweenTwoAccounts_DoNotDeadlock()
    {
        var a = await TestBank.OpenAccountAsync(_instanceA, 1_000_000 * Rp);
        var b = await TestBank.OpenAccountAsync(_instanceA, 1_000_000 * Rp);

        // A->B and B->A at the same time is the classic deadlock shape.
        var outcomes = await RunTogetherAsync(400, i => Transfers(i).SubmitTransferAsync(_admin,
            i % 2 == 0 ? new SubmitTransfer(a.Id, b.Id, 100 * Rp, "IDR") : new SubmitTransfer(b.Id, a.Id, 100 * Rp, "IDR"),
            $"opposing-{i:D3}"));

        Assert.All(outcomes, o => Assert.Equal(201, o.StatusCode));
        Assert.Equal(1_000_000 * Rp, await TestBank.BalanceAsync(_database, a.Id));
        Assert.Equal(1_000_000 * Rp, await TestBank.BalanceAsync(_database, b.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceA));
    }

    [Fact]
    public async Task ContendedTransfersWithScarceFunds_NeverOverdrawAndConserveFunds()
    {
        const int accountCount = 6;
        const long opening = 50_000 * Rp;
        var accounts = new List<AccountDto>();
        for (var i = 0; i < accountCount; i++) accounts.Add(await TestBank.OpenAccountAsync(_instanceA, opening));

        // Amounts are large relative to balances, so many requests must be rejected.
        var random = new Random(77);
        var plan = Enumerable.Range(0, 600).Select(i =>
        {
            var from = random.Next(accountCount);
            var to = (from + 1 + random.Next(accountCount - 1)) % accountCount;
            return (Index: i, From: from, To: to, Amount: random.Next(5_000, 40_001) * Rp);
        }).ToArray();

        var outcomes = new TransferOutcome[plan.Length];
        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 48 }, async (p, ct) =>
        {
            outcomes[p.Index] = await Transfers(p.Index).SubmitTransferAsync(_admin,
                new SubmitTransfer(accounts[p.From].Id, accounts[p.To].Id, p.Amount, "IDR"), $"scarce-{p.Index:D4}", ct);
        });

        Assert.All(outcomes, o => Assert.Contains(o.StatusCode, new[] { 201, 422 }));
        Assert.Contains(outcomes, o => o.StatusCode == 201);
        Assert.Contains(outcomes, o => o.StatusCode == 422);
        Assert.All(outcomes.Where(o => o.StatusCode == 422), o => Assert.Equal("INSUFFICIENT_FUNDS", o.Transfer.FailureCode));

        await using var db = _database.CreateContext();
        var ids = accounts.Select(a => a.Id).ToList();
        var balances = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => a.AvailableBalanceMinorUnits).ToListAsync();
        Assert.All(balances, b => Assert.True(b >= 0));
        Assert.Equal(accountCount * opening, balances.Sum());

        // Only completed transfers moved money.
        var completedIds = outcomes.Where(o => o.StatusCode == 201).Select(o => o.Transfer.Id).ToHashSet();
        var keys = plan.Select(p => $"scarce-{p.Index:D4}").ToList();
        var journalTransferIds = await db.JournalEntries.Where(j => keys.Contains(j.Transfer!.IdempotencyKey)).Select(j => j.TransferId).ToListAsync();
        Assert.Equal(completedIds.Count, journalTransferIds.Count);
        Assert.True(completedIds.SetEquals(journalTransferIds));

        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceB));
    }

    [Fact]
    public async Task ConcurrentReversalsOfOneTransfer_PostExactlyOneReversal()
    {
        var source = await TestBank.OpenAccountAsync(_instanceA, 500_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(_instanceA, 0);
        var original = await Transfers(0).SubmitTransferAsync(_admin,
            new SubmitTransfer(source.Id, destination.Id, 200_000 * Rp, "IDR"), "reversal-original");
        Assert.Equal(201, original.StatusCode);

        // Ten operators reverse the same transfer, each with their own key.
        var results = await RunTogetherAsync(10, async i =>
        {
            try { return (await Transfers(i).ReverseAsync(_admin, original.Transfer.Id, $"reversal-attempt-{i}")).StatusCode; }
            catch (AppException ex) when (ex.Kind == ErrorKind.Conflict) { return 409; }
        });

        Assert.Equal(1, results.Count(r => r == 201));
        Assert.Equal(9, results.Count(r => r is 409 or 422));
        Assert.Equal(500_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(0, await TestBank.BalanceAsync(_database, destination.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceA));
    }

    [Fact]
    public async Task VerificationRunsConsistentlyWhileTransfersAreCommitting()
    {
        var accounts = new List<AccountDto>();
        for (var i = 0; i < 8; i++) accounts.Add(await TestBank.OpenAccountAsync(_instanceA, 1_000_000 * Rp));

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var random = new Random(5);
            await Parallel.ForEachAsync(Enumerable.Range(0, 400), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
            {
                int from, to;
                lock (random) { from = random.Next(8); to = (from + 1 + random.Next(7)) % 8; }
                await Transfers(i).SubmitTransferAsync(_admin,
                    new SubmitTransfer(accounts[from].Id, accounts[to].Id, 1_000 * Rp, "IDR"), $"verify-live-{i:D3}", ct);
            });
        });

        // The verifier uses one snapshot, so it must never observe a half-applied transfer.
        var reports = 0;
        while (!writer.IsCompleted)
        {
            TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceB));
            reports++;
        }
        await writer;
        Assert.True(reports > 0);
        TestBank.AssertConsistent(await TestBank.VerifyAsync(_instanceB));
    }
}
