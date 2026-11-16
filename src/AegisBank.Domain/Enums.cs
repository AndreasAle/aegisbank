namespace AegisBank.Domain;

public enum UserRole
{
    /// <summary>Internal actor for system postings. Can never sign in.</summary>
    System = 0,
    Customer = 1,
    Auditor = 2,
    Admin = 3,
}

public enum CustomerStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>Numeric values match AEGIS_ACCOUNT_* in the native engine header.</summary>
public enum AccountStatus
{
    Active = 1,
    Frozen = 2,
    Closed = 3,
}

/// <summary>
/// Accounting class from the bank's point of view. It determines the normal
/// balance side: assets grow with debits, liabilities grow with credits.
/// </summary>
public enum AccountClass
{
    Asset = 1,
    Liability = 2,
}

/// <summary>Numeric values match AEGIS_NORMAL_* in the native engine header.</summary>
public enum NormalSide
{
    Debit = 1,
    Credit = 2,
}

public enum TransferKind
{
    /// <summary>Customer account to customer account.</summary>
    Internal = 1,
    /// <summary>Funds entering a customer account from the vault cash account.</summary>
    OpeningDeposit = 2,
    /// <summary>Compensating entry for an earlier completed transfer.</summary>
    Reversal = 3,
}

public enum TransferStatus
{
    Completed = 1,
    Rejected = 2,
}

public enum IdempotencyStatus
{
    InProgress = 1,
    Completed = 2,
}
