using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AegisBank.Infrastructure.Transfers;

public sealed class TransferOptions
{
    /// <summary>Total attempts for one unit of work when the database reports a deadlock or serialization failure.</summary>
    public int MaxAttempts { get; set; } = 4;
    public int BaseRetryDelayMilliseconds { get; set; } = 20;
    public int IdempotencyRetentionHours { get; set; } = 24;
}

/// <summary>
/// Runs every money-moving command as one PostgreSQL transaction:
/// claim idempotency key -> lock accounts in id order -> ask the native engine
/// -> persist transfer, journal, ledger lines, balance projections and the
/// stored response -> commit. Nothing is visible to other sessions before the
/// commit, and nothing survives a failure before it.
/// </summary>
public sealed class TransferCoordinator(
    IDbContextFactory<AegisDbContext> contextFactory,
    ITransferEngine engine,
    ITransferFaultInjector faults,
    IOptions<TransferOptions> options,
    TimeProvider clock,
    ILogger<TransferCoordinator> logger) : ITransferService
{
    private static readonly JsonSerializerOptions StoredResponseJson = new(JsonSerializerDefaults.Web);
    private readonly TransferOptions _options = options.Value;

    private sealed record PostingCommand(
        Actor Actor, TransferKind Kind, Guid DebitAccountId, Guid CreditAccountId,
        long AmountMinorUnits, string Currency, string IdempotencyKey, string Fingerprint, Guid? ReversalOf);

    // ---------------------------------------------------------------- entry points

    public async Task<TransferOutcome> SubmitTransferAsync(Actor actor, SubmitTransfer request, string idempotencyKey, CancellationToken ct = default)
    {
        ValidateShape(idempotencyKey, request.SourceAccountId, request.DestinationAccountId, request.AmountMinorUnits, request.Currency);

        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            var accounts = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == request.SourceAccountId || a.Id == request.DestinationAccountId)
                .ToListAsync(ct);
            var source = accounts.FirstOrDefault(a => a.Id == request.SourceAccountId);
            var destination = accounts.FirstOrDefault(a => a.Id == request.DestinationAccountId);

            // A caller who cannot see the source account learns nothing about it.
            if (source is null || (!actor.CanView(source) && !actor.CanDebit(source)))
                throw AppException.NotFound("Source account");
            if (!actor.CanDebit(source))
            {
                db.AuditLogs.Add(Audit.Entry(actor, "transfer.denied", "Account", source.Id.ToString(),
                    new { reason = "not_account_owner" }, clock));
                await db.SaveChangesAsync(ct);
                throw AppException.Forbidden("You are not allowed to transfer from this account.");
            }
            if (destination is null || destination.IsSystemAccount)
                throw AppException.NotFound("Destination account");
        }

        var currency = request.Currency.Trim().ToUpperInvariant();
        var fingerprint = RequestFingerprint.Compute(TransferKind.Internal, actor.UserId,
            request.SourceAccountId, request.DestinationAccountId, request.AmountMinorUnits, currency);

        return await ExecuteWithRetryAsync(new PostingCommand(actor, TransferKind.Internal,
            request.SourceAccountId, request.DestinationAccountId, request.AmountMinorUnits,
            currency, idempotencyKey, fingerprint, null), ct);
    }

    public async Task<TransferOutcome> OpeningDepositAsync(Actor actor, Guid accountId, long amountMinorUnits, string idempotencyKey, CancellationToken ct = default)
    {
        if (actor.Role is not (UserRole.Admin or UserRole.System)) throw AppException.Forbidden();

        string currency;
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null || account.IsSystemAccount) throw AppException.NotFound("Account");
            currency = account.Currency;
        }
        ValidateShape(idempotencyKey, SystemIds.VaultCashAccountId, accountId, amountMinorUnits, currency);

        var fingerprint = RequestFingerprint.Compute(TransferKind.OpeningDeposit, actor.UserId,
            SystemIds.VaultCashAccountId, accountId, amountMinorUnits, currency);

        // Debit vault cash (asset grows), credit the customer account (liability grows).
        return await ExecuteWithRetryAsync(new PostingCommand(actor, TransferKind.OpeningDeposit,
            SystemIds.VaultCashAccountId, accountId, amountMinorUnits, currency, idempotencyKey, fingerprint, null), ct);
    }

    public async Task<TransferOutcome> ReverseAsync(Actor actor, Guid transferId, string idempotencyKey, CancellationToken ct = default)
    {
        if (!actor.IsAdmin) throw AppException.Forbidden();
        if (!IdempotencyKeys.IsValid(idempotencyKey))
            throw AppException.Validation("invalid_idempotency_key", "A valid Idempotency-Key is required.");

        Transfer original;
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            original = await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transferId, ct)
                ?? throw AppException.NotFound("Transfer");
        }
        if (original.Status != TransferStatus.Completed)
            throw AppException.Conflict("not_reversible", "Only completed transfers can be reversed.");
        if (original.Kind == TransferKind.Reversal)
            throw AppException.Conflict("not_reversible", "A reversal cannot itself be reversed.");

        var fingerprint = RequestFingerprint.Compute(TransferKind.Reversal, actor.UserId,
            original.DestinationAccountId, original.SourceAccountId, original.AmountMinorUnits, original.Currency, original.Id);

        // The reversal debits what was credited and credits what was debited.
        return await ExecuteWithRetryAsync(new PostingCommand(actor, TransferKind.Reversal,
            original.DestinationAccountId, original.SourceAccountId, original.AmountMinorUnits,
            original.Currency, idempotencyKey, fingerprint, original.Id), ct);
    }

    private static void ValidateShape(string idempotencyKey, Guid source, Guid destination, long amount, string? currency)
    {
        if (!IdempotencyKeys.IsValid(idempotencyKey))
            throw AppException.Validation("invalid_idempotency_key",
                $"Idempotency-Key must be {IdempotencyKeys.MinLength}-{IdempotencyKeys.MaxLength} printable ASCII characters.");
        if (source == Guid.Empty || destination == Guid.Empty)
            throw AppException.Validation("invalid_account", "Source and destination accounts are required.");
        if (source == destination)
            throw AppException.Validation("same_account", "Source and destination accounts must be different.");
        if (amount <= 0)
            throw AppException.Validation("invalid_amount", "Amount must be a positive number of minor units.");
        if (!Currencies.TryGet(currency?.Trim().ToUpperInvariant(), out _))
            throw AppException.Validation("unsupported_currency", "Only IDR is supported.");
    }

    // ---------------------------------------------------------------- unit of work

    private async Task<TransferOutcome> ExecuteWithRetryAsync(PostingCommand command, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(command, ct);
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                // The whole transaction was rolled back, so repeating it from
                // the start is safe: no partial effect exists.
                if (attempt >= _options.MaxAttempts)
                {
                    logger.LogError(ex, "Transfer unit of work gave up after {Attempts} attempts", attempt);
                    throw new AppException(ErrorKind.Unavailable, "transfer_contention",
                        "The transfer could not be processed due to contention. Retry with the same Idempotency-Key.");
                }
                var delay = TimeSpan.FromMilliseconds(
                    _options.BaseRetryDelayMilliseconds * (1 << (attempt - 1)) + Random.Shared.Next(0, _options.BaseRetryDelayMilliseconds));
                logger.LogWarning("Retrying transfer unit of work after {Reason} (attempt {Attempt}, delay {DelayMs} ms)",
                    DescribeRetryReason(ex), attempt, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (FindPostgres(ex) is { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                       && pg.ConstraintName == "ux_transfers_single_completed_reversal")
            {
                throw AppException.Conflict("already_reversed", "The transfer has already been reversed.");
            }
        }
    }

    private async Task<TransferOutcome> ExecuteOnceAsync(PostingCommand command, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // One top-level transaction, no savepoints: the posting triggers compare txid_current().
        db.Database.AutoSavepointsEnabled = false;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // PostgreSQL stores microseconds. Truncating here keeps the stored idempotent
        // response identical to what a later read of the same transfer returns.
        var ticks = clock.GetUtcNow().UtcDateTime.Ticks;
        var now = new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
        var actorId = command.Actor.UserId;

        // --- Step 1: claim the idempotency key -------------------------------
        // The unique index on (actor_id, idempotency_key) makes this atomic. A
        // concurrent transaction holding the same key blocks us here until it
        // commits (we then see its record) or rolls back (we then own the key).
        var recordId = Guid.CreateVersion7();
        var expiresAt = now.AddHours(_options.IdempotencyRetentionHours);
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_records (id, idempotency_key, actor_id, request_fingerprint, status, created_at, expires_at)
            VALUES ({recordId}, {command.IdempotencyKey}, {actorId}, {command.Fingerprint}, 'InProgress', {now}, {expiresAt})
            ON CONFLICT (actor_id, idempotency_key) DO NOTHING
            """, ct);

        if (claimed == 0)
        {
            var existing = (await db.IdempotencyRecords
                .FromSqlInterpolated($"SELECT * FROM idempotency_records WHERE actor_id = {actorId} AND idempotency_key = {command.IdempotencyKey} FOR SHARE")
                .AsNoTracking().ToListAsync(ct)).FirstOrDefault();

            if (existing is null) throw new IdempotencyRaceException();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(existing.RequestFingerprint),
                    System.Text.Encoding.ASCII.GetBytes(command.Fingerprint)))
            {
                throw AppException.Conflict("idempotency_key_conflict",
                    "This Idempotency-Key was already used with a different request.");
            }
            if (existing.Status != IdempotencyStatus.Completed || existing.ResponseBody is null || existing.ResponseCode is null)
                throw new IdempotencyRaceException();

            var stored = JsonSerializer.Deserialize<TransferDto>(existing.ResponseBody, StoredResponseJson)
                ?? throw new InvalidOperationException("Stored idempotent response is unreadable.");
            return new TransferOutcome(existing.ResponseCode.Value, stored, Replayed: true);
        }

        await faults.OnStageAsync(TransferStages.AfterIdempotencyClaim, ct);

        // --- Step 2: lock both accounts, always in ascending id order --------
        // Two transfers touching the same pair of accounts therefore request
        // their row locks in the same order and cannot deadlock each other.
        var first = command.DebitAccountId.CompareTo(command.CreditAccountId) < 0 ? command.DebitAccountId : command.CreditAccountId;
        var second = first == command.DebitAccountId ? command.CreditAccountId : command.DebitAccountId;
        var lockedFirst = await LockAccountAsync(db, first, ct);
        var lockedSecond = await LockAccountAsync(db, second, ct);
        var debit = lockedFirst.Id == command.DebitAccountId ? lockedFirst : lockedSecond;
        var credit = lockedFirst.Id == command.CreditAccountId ? lockedFirst : lockedSecond;

        await faults.OnStageAsync(TransferStages.AfterAccountsLocked, ct);

        // --- Step 3: decision by the native engine ---------------------------
        // Balances were read under the row locks, so they are the committed
        // values and cannot change before we commit.
        var currencyNumeric = Currencies.Get(command.Currency).NumericCode;
        var decision = engine.Evaluate(EngineAccount.From(debit), EngineAccount.From(credit), command.AmountMinorUnits, currencyNumeric);

        if (!decision.Approved && !decision.IsBusinessRejection)
            throw new InvalidOperationException($"Engine failure: {decision.ResultName}.");
        if (decision.Approved) VerifyDecision(decision, debit, credit, command.AmountMinorUnits);

        // --- Step 4: persist --------------------------------------------------
        var suffix = NewReferenceSuffix(now);
        var transfer = new Transfer
        {
            Id = Guid.CreateVersion7(),
            ReferenceNumber = "TRF-" + suffix,
            Kind = command.Kind,
            SourceAccountId = debit.Id,
            DestinationAccountId = credit.Id,
            AmountMinorUnits = command.AmountMinorUnits,
            Currency = command.Currency,
            Status = decision.Approved ? TransferStatus.Completed : TransferStatus.Rejected,
            FailureCode = decision.Approved ? null : decision.ResultName,
            InitiatedByUserId = actorId,
            IdempotencyKey = command.IdempotencyKey,
            RequestFingerprint = command.Fingerprint,
            ReversalOfTransferId = command.ReversalOf,
            CreatedAt = now,
            CompletedAt = decision.Approved ? now : null,
        };
        db.Transfers.Add(transfer);

        Guid? journalId = null;
        if (decision.Approved)
        {
            var journal = new JournalEntry
            {
                Id = Guid.CreateVersion7(),
                TransferId = transfer.Id,
                ReferenceNumber = "JE-" + suffix,
                Currency = command.Currency,
                Description = Describe(command.Kind, debit, credit),
                CreatedAt = now,
                Lines =
                [
                    new LedgerLine
                    {
                        Id = Guid.CreateVersion7(), AccountId = decision.DebitLine.AccountId, LineNumber = 1,
                        DebitMinorUnits = decision.DebitLine.DebitMinorUnits, CreditMinorUnits = 0, CreatedAt = now,
                    },
                    new LedgerLine
                    {
                        Id = Guid.CreateVersion7(), AccountId = decision.CreditLine.AccountId, LineNumber = 2,
                        DebitMinorUnits = 0, CreditMinorUnits = decision.CreditLine.CreditMinorUnits, CreatedAt = now,
                    },
                ],
            };
            db.JournalEntries.Add(journal);
            journalId = journal.Id;

            debit.AvailableBalanceMinorUnits = decision.DebitAccountBalanceAfter;
            debit.Version++;
            debit.UpdatedAt = now;
            credit.AvailableBalanceMinorUnits = decision.CreditAccountBalanceAfter;
            credit.Version++;
            credit.UpdatedAt = now;
        }

        db.AuditLogs.Add(Audit.Entry(command.Actor,
            decision.Approved ? "transfer.completed" : "transfer.rejected", "Transfer", transfer.Id.ToString(),
            new
            {
                reference = transfer.ReferenceNumber,
                kind = transfer.Kind.ToString(),
                amountMinorUnits = transfer.AmountMinorUnits,
                currency = transfer.Currency,
                sourceAccount = debit.AccountNumber,
                destinationAccount = credit.AccountNumber,
                engineResult = decision.ResultName,
            }, clock));

        await db.SaveChangesAsync(ct);
        await faults.OnStageAsync(TransferStages.AfterLedgerPersisted, ct);

        // --- Step 5: store the response next to the claim --------------------
        var dto = TransferMapping.ToDto(transfer, debit.AccountNumber, credit.AccountNumber, journalId);
        var statusCode = decision.Approved ? 201 : 422;
        var body = JsonSerializer.Serialize(dto, StoredResponseJson);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE idempotency_records
               SET status = 'Completed', response_code = {statusCode}, response_body = {body}
             WHERE id = {recordId}
            """, ct);
        if (updated != 1) throw new InvalidOperationException("Idempotency record disappeared during the transaction.");

        await faults.OnStageAsync(TransferStages.BeforeCommit, ct);

        // --- Step 6: commit. Deferred constraint triggers run here. ----------
        await tx.CommitAsync(ct);

        if (decision.Approved)
            logger.LogInformation("Transfer {Reference} completed: {Amount} {Currency} ({Kind})",
                transfer.ReferenceNumber, transfer.AmountMinorUnits, transfer.Currency, transfer.Kind);
        else
            logger.LogInformation("Transfer {Reference} rejected by engine: {Reason}", transfer.ReferenceNumber, decision.ResultName);

        return new TransferOutcome(statusCode, dto, Replayed: false);
    }

    private static async Task<Account> LockAccountAsync(AegisDbContext db, Guid id, CancellationToken ct)
    {
        var rows = await db.Accounts
            .FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {id} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : throw AppException.NotFound("Account");
    }

    /// <summary>
    /// Independent cross-check of an approved decision. A failure here means a
    /// defect in the engine or the interop layer; the transaction is aborted.
    /// </summary>
    private static void VerifyDecision(EngineDecision decision, Account debit, Account credit, long amount)
    {
        static long Expected(Account account, NormalSide postingSide, long amount) =>
            account.NormalSide == postingSide
                ? checked(account.AvailableBalanceMinorUnits + amount)
                : checked(account.AvailableBalanceMinorUnits - amount);

        var ok =
            decision.DebitLine.AccountId == debit.Id &&
            decision.CreditLine.AccountId == credit.Id &&
            decision.DebitLine.DebitMinorUnits == amount && decision.DebitLine.CreditMinorUnits == 0 &&
            decision.CreditLine.CreditMinorUnits == amount && decision.CreditLine.DebitMinorUnits == 0 &&
            decision.DebitAccountBalanceAfter >= 0 && decision.CreditAccountBalanceAfter >= 0 &&
            decision.DebitAccountBalanceAfter == Expected(debit, NormalSide.Debit, amount) &&
            decision.CreditAccountBalanceAfter == Expected(credit, NormalSide.Credit, amount);

        if (!ok) throw new InvalidOperationException("Engine decision failed verification; transaction aborted.");
    }

    private static string Describe(TransferKind kind, Account debit, Account credit) => kind switch
    {
        TransferKind.OpeningDeposit => $"Opening deposit to {credit.AccountNumber}",
        TransferKind.Reversal => $"Reversal: {debit.AccountNumber} to {credit.AccountNumber}",
        _ => $"Transfer {debit.AccountNumber} to {credit.AccountNumber}",
    };

    private static string NewReferenceSuffix(DateTime now)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[random[i] & 31];
        return $"{now:yyyyMMdd}-{new string(chars)}";
    }

    // ---------------------------------------------------------------- retry classification

    private sealed class IdempotencyRaceException : Exception;

    private static bool IsRetryable(Exception ex) =>
        ex is IdempotencyRaceException or DbUpdateConcurrencyException ||
        FindPostgres(ex) is { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    private static string DescribeRetryReason(Exception ex) => ex switch
    {
        IdempotencyRaceException => "idempotency race",
        DbUpdateConcurrencyException => "stale account version",
        _ => FindPostgres(ex)?.SqlState == PostgresErrorCodes.DeadlockDetected ? "deadlock" : "serialization failure",
    };

    private static PostgresException? FindPostgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException pg) return pg;
        }
        return null;
    }
}

internal sealed class NoFaults : ITransferFaultInjector
{
    public Task OnStageAsync(string stage, CancellationToken ct) => Task.CompletedTask;
}
