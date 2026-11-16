using System.Data;
using System.Data.Common;
using AegisBank.Application;
using AegisBank.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisBank.Infrastructure.Ledger;

/// <summary>
/// Verifies the financial invariants directly in SQL. All checks run in one
/// REPEATABLE READ, read-only transaction so that they observe a single
/// snapshot even while transfers are being committed.
/// </summary>
public sealed class LedgerVerifier(IDbContextFactory<AegisDbContext> contextFactory, TimeProvider clock) : ILedgerVerifier
{
    private sealed record Check(string Name, string Description, string ViolationsSql);

    // Each query returns one text column describing a violating row.
    private static readonly Check[] Checks =
    [
        new("journals_balanced",
            "Every journal entry has at least two lines and equal debit and credit totals.",
            """
            SELECT je.reference_number
              FROM journal_entries je
              LEFT JOIN ledger_lines l ON l.journal_entry_id = je.id
             GROUP BY je.id, je.reference_number
            HAVING COUNT(l.id) < 2
                OR COALESCE(SUM(l.debit_minor_units), 0) <> COALESCE(SUM(l.credit_minor_units), 0)
            """),
        new("projections_reconcile",
            "Each stored account balance equals the balance derived from its ledger lines.",
            """
            SELECT a.account_number || ' stored=' || a.available_balance_minor_units || ' ledger=' ||
                   (CASE WHEN a.class = 'Asset' THEN COALESCE(s.d, 0) - COALESCE(s.c, 0)
                         ELSE COALESCE(s.c, 0) - COALESCE(s.d, 0) END)
              FROM accounts a
              LEFT JOIN (SELECT account_id, SUM(debit_minor_units) AS d, SUM(credit_minor_units) AS c
                           FROM ledger_lines GROUP BY account_id) s ON s.account_id = a.id
             WHERE a.available_balance_minor_units <>
                   (CASE WHEN a.class = 'Asset' THEN COALESCE(s.d, 0) - COALESCE(s.c, 0)
                         ELSE COALESCE(s.c, 0) - COALESCE(s.d, 0) END)
            """),
        new("no_negative_balances",
            "No account has a negative balance.",
            "SELECT account_number FROM accounts WHERE available_balance_minor_units < 0"),
        new("completed_transfers_posted",
            "Every completed transfer has exactly one journal entry for the transfer amount on its two accounts.",
            """
            SELECT t.reference_number
              FROM transfers t
             WHERE t.status = 'Completed'
               AND NOT EXISTS (
                   SELECT 1 FROM journal_entries je
                    WHERE je.transfer_id = t.id
                      AND (SELECT COALESCE(SUM(l.debit_minor_units), 0) FROM ledger_lines l
                            WHERE l.journal_entry_id = je.id AND l.account_id = t.source_account_id) = t.amount_minor_units
                      AND (SELECT COALESCE(SUM(l.credit_minor_units), 0) FROM ledger_lines l
                            WHERE l.journal_entry_id = je.id AND l.account_id = t.destination_account_id) = t.amount_minor_units)
            """),
        new("rejected_transfers_not_posted",
            "No rejected transfer has a journal entry.",
            """
            SELECT t.reference_number FROM transfers t
              JOIN journal_entries je ON je.transfer_id = t.id
             WHERE t.status <> 'Completed'
            """),
        new("ledger_currency_consistent",
            "Ledger lines only touch accounts in the journal's currency.",
            """
            SELECT je.reference_number FROM ledger_lines l
              JOIN journal_entries je ON je.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
             WHERE a.currency <> je.currency
            """),
        new("accounting_equation",
            "Total asset balances equal total liability balances (per currency).",
            """
            SELECT currency || ' assets=' || assets || ' liabilities=' || liabilities FROM (
                SELECT currency,
                       COALESCE(SUM(available_balance_minor_units) FILTER (WHERE class = 'Asset'), 0) AS assets,
                       COALESCE(SUM(available_balance_minor_units) FILTER (WHERE class = 'Liability'), 0) AS liabilities
                  FROM accounts GROUP BY currency) x
             WHERE assets <> liabilities
            """),
        new("idempotency_exactly_once",
            "Every transfer is backed by exactly one completed idempotency record of the same actor and key.",
            """
            SELECT t.reference_number
              FROM transfers t
              LEFT JOIN idempotency_records r
                ON r.actor_id = t.initiated_by_user_id AND r.idempotency_key = t.idempotency_key
             WHERE r.id IS NULL OR r.status <> 'Completed' OR r.request_fingerprint <> t.request_fingerprint
            """),
        new("no_orphan_idempotency_claims",
            "No idempotency claim is left in progress or without a transfer.",
            """
            SELECT r.idempotency_key
              FROM idempotency_records r
              LEFT JOIN transfers t
                ON t.initiated_by_user_id = r.actor_id AND t.idempotency_key = r.idempotency_key
             WHERE r.status <> 'Completed' OR t.id IS NULL
            """),
        new("single_reversal",
            "No transfer has more than one completed reversal.",
            """
            SELECT reversal_of_transfer_id::text FROM transfers
             WHERE reversal_of_transfer_id IS NOT NULL AND status = 'Completed'
             GROUP BY reversal_of_transfer_id HAVING COUNT(*) > 1
            """),
    ];

    public async Task<LedgerVerificationReport> VerifyAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await ExecuteAsync(connection, tx, "SET TRANSACTION READ ONLY", ct);

        var results = new List<InvariantCheck>(Checks.Length);
        foreach (var check in Checks)
        {
            var count = Convert.ToInt64(await ScalarAsync(connection, tx, $"SELECT COUNT(*) FROM ({check.ViolationsSql}) v", ct));
            var samples = new List<string>();
            if (count > 0)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT * FROM ({check.ViolationsSql}) v LIMIT 5";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) samples.Add(reader.IsDBNull(0) ? "(null)" : reader.GetString(0));
            }
            results.Add(new InvariantCheck(check.Name, check.Description, count == 0, count, samples));
        }

        var journals = Convert.ToInt64(await ScalarAsync(connection, tx, "SELECT COUNT(*) FROM journal_entries", ct));
        var lines = Convert.ToInt64(await ScalarAsync(connection, tx, "SELECT COUNT(*) FROM ledger_lines", ct));
        var debits = ToInt64(await ScalarAsync(connection, tx, "SELECT COALESCE(SUM(debit_minor_units), 0) FROM ledger_lines", ct));
        var credits = ToInt64(await ScalarAsync(connection, tx, "SELECT COALESCE(SUM(credit_minor_units), 0) FROM ledger_lines", ct));
        var assets = ToInt64(await ScalarAsync(connection, tx, "SELECT COALESCE(SUM(available_balance_minor_units), 0) FROM accounts WHERE class = 'Asset'", ct));
        var liabilities = ToInt64(await ScalarAsync(connection, tx, "SELECT COALESCE(SUM(available_balance_minor_units), 0) FROM accounts WHERE class = 'Liability'", ct));

        results.Add(new InvariantCheck("ledger_totals_equal", "The sum of all debits equals the sum of all credits.",
            debits == credits, debits == credits ? 0 : 1,
            debits == credits ? [] : [$"debits={debits} credits={credits}"]));

        await tx.CommitAsync(ct);

        return new LedgerVerificationReport(results.All(r => r.Passed), clock.GetUtcNow().UtcDateTime,
            journals, lines, debits, credits, assets, liabilities, results);
    }

    private static long ToInt64(object? value) => value is null or DBNull ? 0 : decimal.ToInt64(Convert.ToDecimal(value));

    private static async Task<object?> ScalarAsync(DbConnection connection, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
