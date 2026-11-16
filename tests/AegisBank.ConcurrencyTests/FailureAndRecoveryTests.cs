using AegisBank.Application;
using AegisBank.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AegisBank.ConcurrencyTests;

/// <summary>Failure injection, rollback, retry and restart behaviour of the transfer unit of work.</summary>
public sealed class FailureAndRecoveryTests : IAsyncLifetime
{
    private const long Rp = TestBank.Rupiah;

    private TestDatabase _database = null!;
    private Actor _admin = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _admin = await TestBank.CreateAdminAsync(_database);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    /// <summary>Scripted fault injector: runs the configured action when the given stage is reached.</summary>
    private sealed class ScriptedFaults : ITransferFaultInjector
    {
        public string? Stage { get; set; }
        public Func<Exception>? Fault { get; set; }
        public int RemainingFaults { get; set; }
        public int Hits;

        public Task OnStageAsync(string stage, CancellationToken ct)
        {
            if (stage == Stage && Fault is not null && RemainingFaults > 0)
            {
                RemainingFaults--;
                Interlocked.Increment(ref Hits);
                throw Fault();
            }
            return Task.CompletedTask;
        }
    }

    private ServiceProvider BuildWithFaults(ScriptedFaults faults) =>
        _database.BuildServices(s => s.AddSingleton<ITransferFaultInjector>(faults));

    [Theory]
    [InlineData(TransferStages.AfterIdempotencyClaim)]
    [InlineData(TransferStages.AfterAccountsLocked)]
    [InlineData(TransferStages.AfterLedgerPersisted)]
    [InlineData(TransferStages.BeforeCommit)]
    public async Task FailureAtAnyStage_RollsBackEverything_AndTheKeyRemainsUsable(string stage)
    {
        var faults = new ScriptedFaults { Stage = stage, Fault = () => new IOException("injected crash") };
        await using var services = BuildWithFaults(faults);
        var transfers = services.GetRequiredService<ITransferService>();
        var source = await TestBank.OpenAccountAsync(services, 100_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(services, 0);
        faults.RemainingFaults = 1; // armed only now: the opening deposit above must not trip it
        var request = new SubmitTransfer(source.Id, destination.Id, 40_000 * Rp, "IDR");
        var key = "fault-" + stage;

        await Assert.ThrowsAsync<IOException>(() => transfers.SubmitTransferAsync(_admin, request, key));
        Assert.Equal(1, faults.Hits);

        // Nothing of the failed attempt is visible: no transfer, no journal, no claim, no balance change.
        await using (var db = _database.CreateContext())
        {
            Assert.Equal(0, await db.Transfers.CountAsync(t => t.IdempotencyKey == key));
            Assert.Equal(0, await db.IdempotencyRecords.CountAsync(r => r.IdempotencyKey == key));
            Assert.Equal(0, await db.JournalEntries.CountAsync(j => j.Transfer!.IdempotencyKey == key));
            Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.Action == "transfer.completed" && a.ActorId == _admin.UserId));
        }
        Assert.Equal(100_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(0, await TestBank.BalanceAsync(_database, destination.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(services));

        // The client retries with the same key and the transfer is applied exactly once.
        var retried = await transfers.SubmitTransferAsync(_admin, request, key);
        Assert.Equal(201, retried.StatusCode);
        Assert.False(retried.Replayed);
        Assert.Equal(60_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(40_000 * Rp, await TestBank.BalanceAsync(_database, destination.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(services));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public async Task TransientDatabaseFailure_IsRetriedAndAppliedExactlyOnce(string sqlState)
    {
        var faults = new ScriptedFaults
        {
            Stage = TransferStages.AfterLedgerPersisted,
            Fault = () => new PostgresException("injected", "ERROR", "ERROR", sqlState),
        };
        await using var services = BuildWithFaults(faults);
        var source = await TestBank.OpenAccountAsync(services, 100_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(services, 0);
        faults.RemainingFaults = 2;

        var outcome = await services.GetRequiredService<ITransferService>().SubmitTransferAsync(_admin,
            new SubmitTransfer(source.Id, destination.Id, 10_000 * Rp, "IDR"), "transient-" + sqlState);

        Assert.Equal(201, outcome.StatusCode);
        Assert.Equal(2, faults.Hits);
        Assert.Equal(90_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(10_000 * Rp, await TestBank.BalanceAsync(_database, destination.Id));
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Transfers.CountAsync(t => t.IdempotencyKey == "transient-" + sqlState));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(services));
    }

    [Fact]
    public async Task PersistentTransientFailure_GivesUpAfterBoundedAttempts_WithoutSideEffects()
    {
        var faults = new ScriptedFaults
        {
            Stage = TransferStages.BeforeCommit,
            Fault = () => new PostgresException("injected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected),
        };
        await using var services = _database.BuildServices(
            s => s.AddSingleton<ITransferFaultInjector>(faults),
            new Dictionary<string, string?> { ["Transfers:MaxAttempts"] = "3", ["Transfers:BaseRetryDelayMilliseconds"] = "1" });
        var source = await TestBank.OpenAccountAsync(services, 100_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(services, 0);
        faults.RemainingFaults = int.MaxValue;

        var ex = await Assert.ThrowsAsync<AppException>(() => services.GetRequiredService<ITransferService>()
            .SubmitTransferAsync(_admin, new SubmitTransfer(source.Id, destination.Id, 10_000 * Rp, "IDR"), "always-deadlock"));

        Assert.Equal(ErrorKind.Unavailable, ex.Kind);
        Assert.Equal(3, faults.Hits);
        Assert.Equal(100_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.Transfers.CountAsync(t => t.IdempotencyKey == "always-deadlock"));
    }

    [Fact]
    public async Task CancellationDuringTheUnitOfWork_LeavesNoTrace()
    {
        using var cts = new CancellationTokenSource();
        var faults = new ScriptedFaults
        {
            Stage = TransferStages.AfterLedgerPersisted,
            Fault = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); },
        };
        await using var services = BuildWithFaults(faults);
        var source = await TestBank.OpenAccountAsync(services, 100_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(services, 0);
        faults.RemainingFaults = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.GetRequiredService<ITransferService>()
            .SubmitTransferAsync(_admin, new SubmitTransfer(source.Id, destination.Id, 10_000 * Rp, "IDR"), "cancelled-key", cts.Token));

        Assert.Equal(100_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync(r => r.IdempotencyKey == "cancelled-key"));
    }

    [Fact]
    public async Task AfterRestart_ACommittedRequestIsReplayedNotReapplied()
    {
        var first = _database.BuildServices();
        var source = await TestBank.OpenAccountAsync(first, 100_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(first, 0);
        var request = new SubmitTransfer(source.Id, destination.Id, 30_000 * Rp, "IDR");

        var original = await first.GetRequiredService<ITransferService>().SubmitTransferAsync(_admin, request, "restart-key");
        Assert.Equal(201, original.StatusCode);

        // "Restart": the instance is disposed and all pooled connections are closed.
        await first.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var second = _database.BuildServices();
        var replay = await second.GetRequiredService<ITransferService>().SubmitTransferAsync(_admin, request, "restart-key");

        Assert.True(replay.Replayed);
        Assert.Equal(original.StatusCode, replay.StatusCode);
        Assert.Equal(original.Transfer, replay.Transfer);
        Assert.Equal(70_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(second));
    }

    [Fact]
    public async Task RejectedTransfer_IsRecordedAndReplayed_WithoutTouchingBalances()
    {
        await using var services = _database.BuildServices();
        var transfers = services.GetRequiredService<ITransferService>();
        var source = await TestBank.OpenAccountAsync(services, 10_000 * Rp);
        var destination = await TestBank.OpenAccountAsync(services, 0);
        var request = new SubmitTransfer(source.Id, destination.Id, 10_001 * Rp, "IDR");

        var rejected = await transfers.SubmitTransferAsync(_admin, request, "rejected-key");
        var replay = await transfers.SubmitTransferAsync(_admin, request, "rejected-key");

        Assert.Equal(422, rejected.StatusCode);
        Assert.Equal("Rejected", rejected.Transfer.Status);
        Assert.Equal("INSUFFICIENT_FUNDS", rejected.Transfer.FailureCode);
        Assert.True(replay.Replayed);
        Assert.Equal(rejected.Transfer, replay.Transfer);
        Assert.Equal(10_000 * Rp, await TestBank.BalanceAsync(_database, source.Id));
        Assert.Equal(0, await TestBank.BalanceAsync(_database, destination.Id));
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Transfers.CountAsync(t => t.IdempotencyKey == "rejected-key"));
        TestBank.AssertConsistent(await TestBank.VerifyAsync(services));
    }
}
