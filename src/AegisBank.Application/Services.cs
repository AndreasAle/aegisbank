using AegisBank.Domain;

namespace AegisBank.Application;

/// <summary>Coordinates every operation that moves money.</summary>
public interface ITransferService
{
    /// <summary>Customer-to-customer transfer initiated by the account owner or an administrator.</summary>
    Task<TransferOutcome> SubmitTransferAsync(Actor actor, SubmitTransfer request, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Funds an account from the vault cash account. Administrators only.</summary>
    Task<TransferOutcome> OpeningDepositAsync(Actor actor, Guid accountId, long amountMinorUnits, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Posts a compensating entry for a completed transfer. Administrators only.</summary>
    Task<TransferOutcome> ReverseAsync(Actor actor, Guid transferId, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Test seam for failure injection. The default implementation does nothing.
/// Stages are named constants on <see cref="TransferStages"/>.
/// </summary>
public interface ITransferFaultInjector
{
    Task OnStageAsync(string stage, CancellationToken ct);
}

public static class TransferStages
{
    public const string AfterIdempotencyClaim = "after-idempotency-claim";
    public const string AfterAccountsLocked = "after-accounts-locked";
    public const string AfterLedgerPersisted = "after-ledger-persisted";
    public const string BeforeCommit = "before-commit";
}

public interface IAccountQueries
{
    Task<PagedResult<AccountDto>> ListAsync(Actor actor, AccountFilter filter, CancellationToken ct = default);
    Task<AccountDto> GetAsync(Actor actor, Guid id, CancellationToken ct = default);
    Task<AccountBalanceDto> GetBalanceAsync(Actor actor, Guid id, CancellationToken ct = default);
    Task<PagedResult<AccountTransactionDto>> GetTransactionsAsync(Actor actor, Guid id, PageRequest page, CancellationToken ct = default);
}

public interface ITransferQueries
{
    Task<PagedResult<TransferDto>> ListAsync(Actor actor, TransferFilter filter, CancellationToken ct = default);
    Task<TransferDto> GetAsync(Actor actor, Guid id, CancellationToken ct = default);
}

public interface ILedgerQueries
{
    Task<PagedResult<JournalEntryDto>> ListJournalsAsync(JournalFilter filter, CancellationToken ct = default);
    Task<JournalEntryDto> GetJournalAsync(Guid id, CancellationToken ct = default);
    Task<JournalEntryDto?> GetJournalForTransferAsync(Guid transferId, CancellationToken ct = default);
}

public interface ILedgerVerifier
{
    Task<LedgerVerificationReport> VerifyAsync(CancellationToken ct = default);
}

public interface IAdminQueries
{
    Task<OverviewDto> GetOverviewAsync(int days = 14, CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> ListAuditLogsAsync(AuditLogFilter filter, CancellationToken ct = default);
    Task<SystemHealthDto> GetSystemHealthAsync(CancellationToken ct = default);
}

public interface IAccountAdminService
{
    Task<CustomerDto> CreateCustomerAsync(Actor actor, CreateCustomer request, CancellationToken ct = default);
    Task<AccountDto> OpenAccountAsync(Actor actor, OpenAccount request, CancellationToken ct = default);
    Task<AccountDto> SetStatusAsync(Actor actor, Guid accountId, AccountStatus status, CancellationToken ct = default);
}

public interface IAuthService
{
    /// <summary>Returns the user when the credentials are valid, otherwise null. Never says why.</summary>
    Task<AuthenticatedUser?> ValidateCredentialsAsync(string username, string password, string? correlationId, CancellationToken ct = default);
    Task<AuthenticatedUser?> FindActiveUserAsync(Guid userId, CancellationToken ct = default);
    Task<RefreshTokenGrant> IssueRefreshTokenAsync(AuthenticatedUser user, CancellationToken ct = default);
    /// <summary>Exchanges a refresh token for a new one. Reuse of a rotated token revokes the family.</summary>
    Task<RefreshTokenGrant?> RotateRefreshTokenAsync(string refreshToken, string? correlationId, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string refreshToken, Guid userId, string? correlationId, CancellationToken ct = default);
    Task RecordSignOutAsync(Guid userId, string channel, string? correlationId, CancellationToken ct = default);
}
