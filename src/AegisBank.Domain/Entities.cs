namespace AegisBank.Domain;

public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    /// <summary>Set for users with the Customer role; scopes account ownership.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// <summary>SHA-256 of the opaque token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>All tokens descending from one login share a family.</summary>
    public Guid FamilyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}

public sealed class Customer
{
    public Guid Id { get; set; }
    public string CustomerNumber { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public DateTime CreatedAt { get; set; }
    public List<Account> Accounts { get; set; } = [];
}

public sealed class Account
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Null only for system accounts (for example vault cash).</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public AccountClass Class { get; set; }
    public string Currency { get; set; } = Currencies.Idr.Code;
    /// <summary>
    /// Projection of the ledger, expressed on the account's normal side.
    /// The ledger lines are authoritative; this column is reconciled against them.
    /// </summary>
    public long AvailableBalanceMinorUnits { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Incremented on every balance change; optimistic concurrency token.</summary>
    public long Version { get; set; }

    public NormalSide NormalSide => Class == AccountClass.Asset ? NormalSide.Debit : NormalSide.Credit;
    public bool IsSystemAccount => CustomerId is null;
}

public sealed class Transfer
{
    public Guid Id { get; set; }
    public string ReferenceNumber { get; set; } = "";
    public TransferKind Kind { get; set; }
    /// <summary>The account that is debited.</summary>
    public Guid SourceAccountId { get; set; }
    public Account? SourceAccount { get; set; }
    /// <summary>The account that is credited.</summary>
    public Guid DestinationAccountId { get; set; }
    public Account? DestinationAccount { get; set; }
    public long AmountMinorUnits { get; set; }
    public string Currency { get; set; } = Currencies.Idr.Code;
    public TransferStatus Status { get; set; }
    /// <summary>Engine result name when the transfer was rejected.</summary>
    public string? FailureCode { get; set; }
    public Guid InitiatedByUserId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public Guid? ReversalOfTransferId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}

public sealed class JournalEntry
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public Transfer? Transfer { get; set; }
    public string ReferenceNumber { get; set; } = "";
    public string Currency { get; set; } = Currencies.Idr.Code;
    public string Description { get; set; } = "";
    /// <summary>Database transaction that posted the entry. Set by a trigger.</summary>
    public long PostingTxId { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<LedgerLine> Lines { get; set; } = [];
}

public sealed class LedgerLine
{
    public Guid Id { get; set; }
    public Guid JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public int LineNumber { get; set; }
    public long DebitMinorUnits { get; set; }
    public long CreditMinorUnits { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    /// <summary>JSON document.</summary>
    public string Metadata { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public Guid ActorId { get; set; }
    public string RequestFingerprint { get; set; } = "";
    public IdempotencyStatus Status { get; set; }
    public int? ResponseCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
