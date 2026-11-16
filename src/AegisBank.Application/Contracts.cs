namespace AegisBank.Application;

public sealed record AccountDto(
    Guid Id, string AccountNumber, string Name, string AccountClass, string NormalSide,
    Guid? CustomerId, string? CustomerNumber, string? CustomerName,
    string Currency, long AvailableBalanceMinorUnits, string Status,
    DateTime CreatedAt, DateTime UpdatedAt, long Version);

public sealed record AccountBalanceDto(
    Guid AccountId, string AccountNumber, string Currency, long AvailableBalanceMinorUnits, DateTime AsOf, long Version);

public sealed record AccountTransactionDto(
    Guid LedgerLineId, Guid JournalEntryId, Guid TransferId, string ReferenceNumber, string Kind,
    string Direction, long AmountMinorUnits, string Currency,
    string? CounterpartyAccountNumber, DateTime PostedAt);

public sealed record TransferDto(
    Guid Id, string ReferenceNumber, string Kind, string Status,
    Guid SourceAccountId, string SourceAccountNumber,
    Guid DestinationAccountId, string DestinationAccountNumber,
    long AmountMinorUnits, string Currency,
    string? FailureCode, string? FailureReason,
    Guid? JournalEntryId, Guid? ReversalOfTransferId,
    Guid InitiatedByUserId, DateTime CreatedAt, DateTime? CompletedAt);

public sealed record LedgerLineDto(
    Guid Id, int LineNumber, Guid AccountId, string AccountNumber, string AccountName,
    long DebitMinorUnits, long CreditMinorUnits);

public sealed record JournalEntryDto(
    Guid Id, Guid TransferId, string ReferenceNumber, string Currency, string Description, DateTime CreatedAt,
    long TotalDebitMinorUnits, long TotalCreditMinorUnits, bool IsBalanced, IReadOnlyList<LedgerLineDto> Lines);

public sealed record AuditLogDto(
    Guid Id, Guid? ActorId, string? ActorUsername, string Action, string EntityType, string? EntityId,
    string Metadata, string? CorrelationId, DateTime CreatedAt);

public sealed record DailyVolumeDto(DateOnly Day, long CompletedCount, long RejectedCount, long CompletedAmountMinorUnits);

public sealed record OverviewDto(
    long CustomerAccountCount, long ActiveAccountCount, long CustomerCount,
    long TotalCustomerBalanceMinorUnits, long VaultCashBalanceMinorUnits,
    long TransferCount, long CompletedTransferCount, long RejectedTransferCount,
    long CompletedAmountMinorUnits, string Currency,
    IReadOnlyList<DailyVolumeDto> DailyVolume, IReadOnlyList<TransferDto> RecentTransfers);

// --- Requests ---------------------------------------------------------------

public sealed record AccountFilter(string? Search = null, string? Status = null, Guid? CustomerId = null,
    bool IncludeSystemAccounts = false, string? Sort = null, PageRequest? Page = null);

public sealed record TransferFilter(string? Status = null, string? Kind = null, Guid? AccountId = null,
    string? Reference = null, DateTime? From = null, DateTime? To = null, string? Sort = null, PageRequest? Page = null);

public sealed record JournalFilter(Guid? AccountId = null, string? Reference = null,
    DateTime? From = null, DateTime? To = null, PageRequest? Page = null);

public sealed record AuditLogFilter(string? Action = null, string? EntityType = null, Guid? ActorId = null,
    DateTime? From = null, DateTime? To = null, PageRequest? Page = null);

public sealed record SubmitTransfer(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinorUnits, string Currency);

// --- Transfer outcome -------------------------------------------------------

/// <summary>
/// Outcome of a money-moving command. <see cref="StatusCode"/> is 201 for a
/// completed transfer and 422 for a recorded business rejection. A replay of
/// an earlier request returns the stored outcome with <see cref="Replayed"/> set.
/// </summary>
public sealed record TransferOutcome(int StatusCode, TransferDto Transfer, bool Replayed)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

// --- Verification -----------------------------------------------------------

public sealed record InvariantCheck(string Name, string Description, bool Passed, long Violations, IReadOnlyList<string> Samples);

public sealed record LedgerVerificationReport(
    bool IsConsistent, DateTime VerifiedAt, long JournalCount, long LedgerLineCount,
    long TotalDebitsMinorUnits, long TotalCreditsMinorUnits,
    long TotalAssetBalanceMinorUnits, long TotalLiabilityBalanceMinorUnits,
    IReadOnlyList<InvariantCheck> Checks);

// --- Auth -------------------------------------------------------------------

public sealed record AuthenticatedUser(Guid Id, string Username, string Email, string Role, Guid? CustomerId)
{
    public Actor ToActor(string? correlationId = null) =>
        new(Id, Username, Enum.Parse<Domain.UserRole>(Role), CustomerId, correlationId);
}

public sealed record RefreshTokenGrant(AuthenticatedUser User, string RefreshToken, DateTime RefreshTokenExpiresAt);

// --- Administration ---------------------------------------------------------

public sealed record CreateCustomer(string FullName, string Email);
public sealed record CustomerDto(Guid Id, string CustomerNumber, string FullName, string Email, string Status, DateTime CreatedAt);
public sealed record OpenAccount(Guid CustomerId, string Name, string Currency);

public sealed record ComponentHealth(string Name, bool Healthy, string Detail);

public sealed record SystemHealthDto(
    bool Healthy, DateTime CheckedAt, string InstanceId, string Environment,
    IReadOnlyList<ComponentHealth> Components,
    IReadOnlyList<TransferDto> RecentRejections,
    long PendingMigrations);
