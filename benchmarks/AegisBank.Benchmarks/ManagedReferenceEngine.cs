using AegisBank.Application;
using AegisBank.Domain;

namespace AegisBank.Benchmarks;

/// <summary>
/// BENCHMARK ONLY. A C# implementation of the same rules as the C++ engine,
/// used as the comparison baseline. It is not referenced by the API, the
/// dashboard or the transfer coordinator; the production path always calls
/// the native library. Program.cs verifies that both implementations agree
/// before any measurement is taken.
/// </summary>
public sealed class ManagedReferenceEngine : ITransferEngine
{
    public EngineDecision Evaluate(EngineAccount debit, EngineAccount credit, long amount, uint currency)
    {
        if (amount <= 0) return Reject(EngineResult.AmountNotPositive, "AMOUNT_NOT_POSITIVE");
        if (!Valid(debit) || !Valid(credit)) return Reject(EngineResult.InvalidAccountData, "INVALID_ACCOUNT_DATA");
        if (debit.Id == credit.Id) return Reject(EngineResult.SameAccount, "SAME_ACCOUNT");
        if (debit.CurrencyNumeric != currency || credit.CurrencyNumeric != currency)
            return Reject(EngineResult.CurrencyMismatch, "CURRENCY_MISMATCH");
        if (debit.Status != AccountStatus.Active) return Reject(EngineResult.DebitAccountState, "DEBIT_ACCOUNT_NOT_ACTIVE");
        if (credit.Status != AccountStatus.Active) return Reject(EngineResult.CreditAccountState, "CREDIT_ACCOUNT_NOT_ACTIVE");

        var debitResult = Apply(debit, NormalSide.Debit, amount, out var debitAfter);
        if (debitResult != EngineResult.Ok) return Reject(debitResult, Name(debitResult));
        var creditResult = Apply(credit, NormalSide.Credit, amount, out var creditAfter);
        if (creditResult != EngineResult.Ok) return Reject(creditResult, Name(creditResult));

        return new EngineDecision(EngineResult.Ok, "OK", debitAfter, creditAfter,
            new EngineLedgerInstruction(debit.Id, amount, 0),
            new EngineLedgerInstruction(credit.Id, 0, amount));
    }

    public EngineInfo GetInfo() => new(true, "managed-reference", 1, null);

    private static bool Valid(in EngineAccount a) =>
        a.Status is AccountStatus.Active or AccountStatus.Frozen or AccountStatus.Closed &&
        a.NormalSide is NormalSide.Debit or NormalSide.Credit &&
        a.BalanceMinorUnits >= 0 && a.CurrencyNumeric != 0;

    private static EngineResult Apply(in EngineAccount account, NormalSide postingSide, long amount, out long balance)
    {
        balance = 0;
        if (account.NormalSide == postingSide)
        {
            // Checked addition without exceptions.
            if (account.BalanceMinorUnits > long.MaxValue - amount) return EngineResult.BalanceOverflow;
            balance = account.BalanceMinorUnits + amount;
            return EngineResult.Ok;
        }
        var difference = account.BalanceMinorUnits - amount; // both operands are non-negative here: cannot overflow
        if (difference < 0) return EngineResult.InsufficientFunds;
        balance = difference;
        return EngineResult.Ok;
    }

    private static string Name(EngineResult result) => result switch
    {
        EngineResult.InsufficientFunds => "INSUFFICIENT_FUNDS",
        EngineResult.BalanceOverflow => "BALANCE_OVERFLOW",
        _ => "INTERNAL_ERROR",
    };

    private static EngineDecision Reject(EngineResult result, string name) => new(result, name, 0, 0, default, default);
}
