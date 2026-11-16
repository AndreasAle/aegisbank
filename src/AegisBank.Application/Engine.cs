using AegisBank.Domain;

namespace AegisBank.Application;

/// <summary>Immutable view of an account handed to the transaction engine.</summary>
public readonly record struct EngineAccount(
    Guid Id, long BalanceMinorUnits, uint CurrencyNumeric, AccountStatus Status, NormalSide NormalSide)
{
    public static EngineAccount From(Account account) => new(
        account.Id,
        account.AvailableBalanceMinorUnits,
        Currencies.Get(account.Currency).NumericCode,
        account.Status,
        account.NormalSide);
}

public readonly record struct EngineLedgerInstruction(Guid AccountId, long DebitMinorUnits, long CreditMinorUnits);

/// <summary>Result codes of the native engine. Values mirror AEGIS_* in engine.h.</summary>
public enum EngineResult
{
    Ok = 0,
    NullArgument = 1,
    AbiMismatch = 2,
    AmountNotPositive = 10,
    SameAccount = 11,
    CurrencyMismatch = 12,
    DebitAccountState = 13,
    CreditAccountState = 14,
    InsufficientFunds = 15,
    BalanceOverflow = 16,
    InvalidAccountData = 17,
    Internal = 99,
}

public readonly record struct EngineDecision(
    EngineResult Result,
    string ResultName,
    long DebitAccountBalanceAfter,
    long CreditAccountBalanceAfter,
    EngineLedgerInstruction DebitLine,
    EngineLedgerInstruction CreditLine)
{
    public bool Approved => Result == EngineResult.Ok;

    /// <summary>True for outcomes caused by the request, not by a defect.</summary>
    public bool IsBusinessRejection => Result is >= EngineResult.AmountNotPositive and <= EngineResult.BalanceOverflow;
}

public sealed record EngineInfo(bool Available, string? Version, uint AbiVersion, string? Error);

/// <summary>
/// Financial decision logic. The production implementation calls the native
/// C++ engine; it holds no state and performs no I/O.
/// </summary>
public interface ITransferEngine
{
    EngineDecision Evaluate(EngineAccount debitAccount, EngineAccount creditAccount, long amountMinorUnits, uint currencyNumeric);
    EngineInfo GetInfo();
}

public static class EngineMessages
{
    /// <summary>Client-safe explanation of an engine rejection.</summary>
    public static string Describe(string? resultName) => resultName switch
    {
        "AMOUNT_NOT_POSITIVE" => "The amount must be greater than zero.",
        "SAME_ACCOUNT" => "Source and destination accounts must be different.",
        "CURRENCY_MISMATCH" => "The accounts and the transfer must use the same currency.",
        "DEBIT_ACCOUNT_NOT_ACTIVE" => "The source account is not active.",
        "CREDIT_ACCOUNT_NOT_ACTIVE" => "The destination account is not active.",
        "INSUFFICIENT_FUNDS" => "The source account does not have sufficient funds.",
        "BALANCE_OVERFLOW" => "The transfer would exceed the maximum representable balance.",
        "ORIGINAL_NOT_REVERSIBLE" => "The original transfer cannot be reversed.",
        _ => "The transfer was rejected.",
    };
}
