using AegisBank.Application;
using AegisBank.Infrastructure.Seeding;
using AegisBank.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AegisBank.IntegrationTests;

/// <summary>
/// The database must defend the ledger on its own, even against a client
/// that bypasses the application. These tests talk to PostgreSQL directly.
/// </summary>
public sealed class DatabaseIntegrityTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private ServiceProvider _services = null!;
    private Actor _admin = null!;
    private AccountDto _a = null!;
    private AccountDto _b = null!;
    private TransferDto _transfer = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _services = _database.BuildServices();
        _admin = await TestBank.CreateAdminAsync(_database);
        _a = await TestBank.OpenAccountAsync(_services, 1_000_000);
        _b = await TestBank.OpenAccountAsync(_services, 0);
        var outcome = await _services.GetRequiredService<ITransferService>()
            .SubmitTransferAsync(_admin, new SubmitTransfer(_a.Id, _b.Id, 250_000, "IDR"), "integrity-seed-transfer");
        _transfer = outcome.Transfer;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<PostgresException> AssertRejectedAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(connection, sql));
    }

    [Theory]
    [InlineData("UPDATE ledger_lines SET debit_minor_units = debit_minor_units + 1 WHERE debit_minor_units > 0")]
    [InlineData("UPDATE ledger_lines SET account_id = account_id")]
    [InlineData("DELETE FROM ledger_lines")]
    [InlineData("UPDATE journal_entries SET description = 'edited'")]
    [InlineData("DELETE FROM journal_entries")]
    [InlineData("UPDATE transfers SET amount_minor_units = 1")]
    [InlineData("UPDATE transfers SET status = 'Rejected', completed_at = NULL, failure_code = 'X'")]
    [InlineData("DELETE FROM transfers")]
    [InlineData("TRUNCATE ledger_lines")]
    [InlineData("TRUNCATE transfers CASCADE")]
    [InlineData("UPDATE audit_logs SET action = 'tampered'")]
    [InlineData("DELETE FROM audit_logs")]
    public async Task PostedRecordsCannotBeChangedOrRemoved(string sql)
    {
        var ex = await AssertRejectedAsync(sql);
        Assert.Contains("immutable", ex.MessageText);
    }

    [Fact]
    public async Task BalanceCannotBeDrivenNegative()
    {
        var ex = await AssertRejectedAsync($"UPDATE accounts SET available_balance_minor_units = -1 WHERE id = '{_a.Id}'");
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_accounts_balance_nonnegative", ex.ConstraintName);
    }

    [Theory]
    [InlineData(0, "ck_transfers_amount_positive")]
    [InlineData(-5, "ck_transfers_amount_positive")]
    public async Task TransferAmountMustBePositive(long amount, string constraint)
    {
        var ex = await AssertRejectedAsync($"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, failure_code, initiated_by_user_id, idempotency_key, request_fingerprint, created_at)
            VALUES (gen_random_uuid(), 'TRF-BAD-{amount}', 'Internal', '{_a.Id}', '{_b.Id}', {amount},
                    'IDR', 'Rejected', 'X', '{_admin.UserId}', 'bad-amount-{amount}', 'fp', now())
            """);
        Assert.Equal(constraint, ex.ConstraintName);
    }

    [Fact]
    public async Task DuplicateReferenceAccountNumberAndIdempotencyKeyAreRejected()
    {
        var duplicateKey = await AssertRejectedAsync($"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, failure_code, initiated_by_user_id, idempotency_key, request_fingerprint, created_at)
            VALUES (gen_random_uuid(), 'TRF-DUP-KEY', 'Internal', '{_a.Id}', '{_b.Id}', 10,
                    'IDR', 'Rejected', 'X', '{_admin.UserId}', 'integrity-seed-transfer', 'fp', now())
            """);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateKey.SqlState);

        var duplicateReference = await AssertRejectedAsync($"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, failure_code, initiated_by_user_id, idempotency_key, request_fingerprint, created_at)
            VALUES (gen_random_uuid(), '{_transfer.ReferenceNumber}', 'Internal', '{_a.Id}', '{_b.Id}', 10,
                    'IDR', 'Rejected', 'X', '{_admin.UserId}', 'another-key-0001', 'fp', now())
            """);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateReference.SqlState);

        var duplicateAccount = await AssertRejectedAsync($"""
            INSERT INTO accounts (id, account_number, name, customer_id, class, currency, available_balance_minor_units, status, created_at, updated_at, version)
            VALUES (gen_random_uuid(), '{_a.AccountNumber}', 'dup', NULL, 'Asset', 'IDR', 0, 'Active', now(), now(), 0)
            """);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateAccount.SqlState);
    }

    [Fact]
    public async Task UnbalancedJournalIsRejectedAtCommit()
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var transferId = Guid.NewGuid();
        var journalId = Guid.NewGuid();
        await ExecAsync(connection, $"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, initiated_by_user_id, idempotency_key, request_fingerprint, created_at, completed_at)
            VALUES ('{transferId}', 'TRF-UNBALANCED', 'Internal', '{_a.Id}', '{_b.Id}', 100,
                    'IDR', 'Completed', '{_admin.UserId}', 'unbalanced-key-01', 'fp', now(), now());
            INSERT INTO journal_entries (id, transfer_id, reference_number, currency, description, created_at)
            VALUES ('{journalId}', '{transferId}', 'JE-UNBALANCED', 'IDR', 'unbalanced', now());
            INSERT INTO ledger_lines (id, journal_entry_id, account_id, line_number, debit_minor_units, credit_minor_units, created_at)
            VALUES (gen_random_uuid(), '{journalId}', '{_a.Id}', 1, 100, 0, now()),
                   (gen_random_uuid(), '{journalId}', '{_b.Id}', 2, 0, 99, now());
            """, tx);

        // The statements are accepted; the deferred constraint trigger fires at commit.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Contains("not balanced", ex.MessageText);

        await using var db = _database.CreateContext();
        Assert.False(await db.Transfers.AnyAsync(t => t.Id == transferId));
        Assert.False(await db.JournalEntries.AnyAsync(j => j.Id == journalId));
    }

    [Fact]
    public async Task CompletedTransferWithoutJournalIsRejectedAtCommit()
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await ExecAsync(connection, $"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, initiated_by_user_id, idempotency_key, request_fingerprint, created_at, completed_at)
            VALUES (gen_random_uuid(), 'TRF-NOJOURNAL', 'Internal', '{_a.Id}', '{_b.Id}', 100,
                    'IDR', 'Completed', '{_admin.UserId}', 'no-journal-key-01', 'fp', now(), now())
            """, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Contains("no matching journal entry", ex.MessageText);
    }

    [Fact]
    public async Task LinesCannotBeAddedToAnAlreadyPostedJournal()
    {
        // Even a balanced pair is refused: a posted journal is frozen.
        var ex = await AssertRejectedAsync($"""
            INSERT INTO ledger_lines (id, journal_entry_id, account_id, line_number, debit_minor_units, credit_minor_units, created_at)
            VALUES (gen_random_uuid(), '{_transfer.JournalEntryId}', '{_a.Id}', 3, 500, 0, now()),
                   (gen_random_uuid(), '{_transfer.JournalEntryId}', '{_b.Id}', 4, 0, 500, now())
            """);
        Assert.Contains("already posted", ex.MessageText);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public async Task ALedgerLineIsEitherADebitOrACredit(long debit, long credit)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var transferId = Guid.NewGuid();
        var journalId = Guid.NewGuid();
        await ExecAsync(connection, $"""
            INSERT INTO transfers (id, reference_number, kind, source_account_id, destination_account_id, amount_minor_units,
                                   currency, status, initiated_by_user_id, idempotency_key, request_fingerprint, created_at, completed_at)
            VALUES ('{transferId}', 'TRF-{Guid.NewGuid().ToString("N")[..12]}', 'Internal', '{_a.Id}', '{_b.Id}', 100,
                    'IDR', 'Completed', '{_admin.UserId}', '{Guid.NewGuid():N}', 'fp', now(), now());
            INSERT INTO journal_entries (id, transfer_id, reference_number, currency, description, created_at)
            VALUES ('{journalId}', '{transferId}', 'JE-{Guid.NewGuid().ToString("N")[..12]}', 'IDR', 'bad line', now());
            """, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(connection, $"""
            INSERT INTO ledger_lines (id, journal_entry_id, account_id, line_number, debit_minor_units, credit_minor_units, created_at)
            VALUES (gen_random_uuid(), '{journalId}', '{_a.Id}', 1, {debit}, {credit}, now())
            """, tx));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task ReconciliationDetectsABalanceChangedBehindTheLedgersBack()
    {
        TestBank.AssertConsistent(await TestBank.VerifyAsync(_services));

        // Simulates a rogue direct update: allowed by the constraints, caught by reconciliation.
        await using (var connection = await OpenAsync())
            await ExecAsync(connection, $"UPDATE accounts SET available_balance_minor_units = available_balance_minor_units + 777 WHERE id = '{_b.Id}'");

        var report = await TestBank.VerifyAsync(_services);
        Assert.False(report.IsConsistent);
        var mismatch = Assert.Single(report.Checks, c => c.Name == "projections_reconcile");
        Assert.Equal(1, mismatch.Violations);
        Assert.Contains(_b.AccountNumber, mismatch.Samples[0]);
        Assert.False(report.Checks.Single(c => c.Name == "accounting_equation").Passed);
        // Journals themselves are still fine.
        Assert.True(report.Checks.Single(c => c.Name == "journals_balanced").Passed);
    }

    [Fact]
    public async Task OpeningFundsComeFromVaultCashThroughABalancedJournal()
    {
        await using var db = _database.CreateContext();
        var opening = await db.Transfers.AsNoTracking().Include(t => t.JournalEntry!.Lines)
            .SingleAsync(t => t.DestinationAccountId == _a.Id && t.Kind == Domain.TransferKind.OpeningDeposit);

        Assert.Equal(SystemIds.VaultCashAccountId, opening.SourceAccountId);
        var lines = opening.JournalEntry!.Lines.OrderBy(l => l.LineNumber).ToList();
        Assert.Equal((SystemIds.VaultCashAccountId, 1_000_000L, 0L), (lines[0].AccountId, lines[0].DebitMinorUnits, lines[0].CreditMinorUnits));
        Assert.Equal((_a.Id, 0L, 1_000_000L), (lines[1].AccountId, lines[1].DebitMinorUnits, lines[1].CreditMinorUnits));

        // Vault cash (asset) equals the sum of customer deposits (liabilities).
        var vault = await TestBank.BalanceAsync(_database, SystemIds.VaultCashAccountId);
        var deposits = await db.Accounts.Where(a => a.CustomerId != null).SumAsync(a => a.AvailableBalanceMinorUnits);
        Assert.Equal(deposits, vault);
    }

    [Fact]
    public async Task DemoSeedIsIdempotentAndRefusedInProduction()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Seed:Enabled"] = "true", ["Seed:AdminPassword"] = "Seed-Test-1!",
            ["Seed:AuditorPassword"] = "Seed-Test-2!", ["Seed:CustomerPassword"] = "Seed-Test-3!",
        };
        await using var services = _database.BuildServices(settings: settings);

        async Task<(int Users, int Accounts, int Transfers, long Total)> SnapshotAsync()
        {
            await using var db = _database.CreateContext();
            return (await db.Users.CountAsync(), await db.Accounts.CountAsync(), await db.Transfers.CountAsync(),
                await db.Accounts.Where(a => a.CustomerId != null).SumAsync(a => a.AvailableBalanceMinorUnits));
        }

        await using (var scope = services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync());
        var first = await SnapshotAsync();
        await using (var scope = services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync());
        Assert.Equal(first, await SnapshotAsync());
        TestBank.AssertConsistent(await TestBank.VerifyAsync(services));

        // Same configuration, Production environment: nothing happens.
        await using var production = _database.BuildServices(
            s => s.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new ProductionEnvironment()), settings);
        await using (var scope = production.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync());
    }

    private sealed class ProductionEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AegisBank.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
