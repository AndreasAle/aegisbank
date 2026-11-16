using System.Runtime.InteropServices;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Native;

namespace AegisBank.UnitTests;

/// <summary>
/// Exercises the real native library through P/Invoke. These tests fail (they
/// are not skipped) when the library is missing, because a build without the
/// engine is not a valid build of the system.
/// </summary>
public sealed class NativeInteropTests
{
    private const uint Idr = 360;
    private readonly NativeTransferEngine _engine = new();

    private static EngineAccount Deposit(long balance, AccountStatus status = AccountStatus.Active, uint currency = Idr) =>
        new(Guid.NewGuid(), balance, currency, status, NormalSide.Credit);

    private static EngineAccount Vault(long balance) => new(Guid.NewGuid(), balance, Idr, AccountStatus.Active, NormalSide.Debit);

    [Fact]
    public void LibraryIsLoadedAndReportsTheExpectedAbi()
    {
        var info = _engine.GetInfo();
        Assert.True(info.Available, $"Native engine not available: {info.Error}");
        Assert.Equal(NativeMethods.ExpectedAbiVersion, info.AbiVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Fact]
    public unsafe void ManagedStructLayoutsMatchTheNativeHeader()
    {
        Assert.Equal(NativeMethods.aegis_sizeof_account_snapshot(), (uint)sizeof(AegisAccountSnapshot));
        Assert.Equal(NativeMethods.aegis_sizeof_transfer_request(), (uint)sizeof(AegisTransferRequest));
        Assert.Equal(NativeMethods.aegis_sizeof_ledger_instruction(), (uint)sizeof(AegisLedgerInstruction));
        Assert.Equal(NativeMethods.aegis_sizeof_transfer_decision(), (uint)sizeof(AegisTransferDecision));
        Assert.Equal(NativeMethods.aegis_offsetof_request_amount(),
            (uint)Marshal.OffsetOf<AegisTransferRequest>(nameof(AegisTransferRequest.AmountMinor)));
        Assert.Equal(NativeMethods.aegis_offsetof_decision_lines(),
            (uint)Marshal.OffsetOf<AegisTransferDecision>(nameof(AegisTransferDecision.DebitLine)));

        // The documented sizes, independent of what the library reports.
        Assert.Equal(32, sizeof(AegisAccountSnapshot));
        Assert.Equal(88, sizeof(AegisTransferRequest));
        Assert.Equal(32, sizeof(AegisLedgerInstruction));
        Assert.Equal(96, sizeof(AegisTransferDecision));
    }

    [Fact]
    public void ValidTransferReturnsBalancesAndBalancedInstructions()
    {
        var source = Deposit(10_000_000);
        var destination = Deposit(500);

        var d = _engine.Evaluate(source, destination, 8_000_000, Idr);

        Assert.True(d.Approved);
        Assert.Equal("OK", d.ResultName);
        Assert.Equal(2_000_000, d.DebitAccountBalanceAfter);
        Assert.Equal(8_000_500, d.CreditAccountBalanceAfter);
        // Account identifiers survive the round trip through 16 raw bytes.
        Assert.Equal(source.Id, d.DebitLine.AccountId);
        Assert.Equal(destination.Id, d.CreditLine.AccountId);
        Assert.Equal((8_000_000, 0), (d.DebitLine.DebitMinorUnits, d.DebitLine.CreditMinorUnits));
        Assert.Equal((0, 8_000_000), (d.CreditLine.DebitMinorUnits, d.CreditLine.CreditMinorUnits));
    }

    public static TheoryData<string, EngineResult, string> Rejections => new()
    {
        { "insufficient", EngineResult.InsufficientFunds, "INSUFFICIENT_FUNDS" },
        { "zero", EngineResult.AmountNotPositive, "AMOUNT_NOT_POSITIVE" },
        { "negative", EngineResult.AmountNotPositive, "AMOUNT_NOT_POSITIVE" },
        { "same", EngineResult.SameAccount, "SAME_ACCOUNT" },
        { "currency", EngineResult.CurrencyMismatch, "CURRENCY_MISMATCH" },
        { "closed-source", EngineResult.DebitAccountState, "DEBIT_ACCOUNT_NOT_ACTIVE" },
        { "frozen-destination", EngineResult.CreditAccountState, "CREDIT_ACCOUNT_NOT_ACTIVE" },
        { "overflow", EngineResult.BalanceOverflow, "BALANCE_OVERFLOW" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void RejectionsAreMappedToStructuredResults(string scenario, EngineResult expected, string expectedName)
    {
        var source = Deposit(1_000);
        var destination = Deposit(0);
        var d = scenario switch
        {
            "insufficient" => _engine.Evaluate(source, destination, 1_001, Idr),
            "zero" => _engine.Evaluate(source, destination, 0, Idr),
            "negative" => _engine.Evaluate(source, destination, -50, Idr),
            "same" => _engine.Evaluate(source, source, 10, Idr),
            "currency" => _engine.Evaluate(source, Deposit(0, currency: 840), 10, Idr),
            "closed-source" => _engine.Evaluate(Deposit(1_000, AccountStatus.Closed), destination, 10, Idr),
            "frozen-destination" => _engine.Evaluate(source, Deposit(0, AccountStatus.Frozen), 10, Idr),
            "overflow" => _engine.Evaluate(source, Deposit(long.MaxValue), 1, Idr),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.False(d.Approved);
        Assert.Equal(expected, d.Result);
        Assert.Equal(expectedName, d.ResultName);
        Assert.True(d.IsBusinessRejection);
        // A rejection never carries posting instructions.
        Assert.Equal(default, d.DebitLine);
        Assert.Equal(default, d.CreditLine);
    }

    [Fact]
    public void InvalidSnapshotIsAnEngineFaultNotABusinessRejection()
    {
        var d = _engine.Evaluate(Deposit(-1), Deposit(0), 10, Idr);
        Assert.Equal(EngineResult.InvalidAccountData, d.Result);
        Assert.False(d.IsBusinessRejection);
    }

    [Fact]
    public void OpeningDepositDebitsTheAssetAndCreditsTheLiability()
    {
        var d = _engine.Evaluate(Vault(1_000), Deposit(200), 300, Idr);
        Assert.True(d.Approved);
        Assert.Equal(1_300, d.DebitAccountBalanceAfter);
        Assert.Equal(500, d.CreditAccountBalanceAfter);
    }

    [Fact]
    public void RepeatedCallsAreDeterministic()
    {
        var source = Deposit(999_999);
        var destination = Deposit(1);
        var first = _engine.Evaluate(source, destination, 123_456, Idr);
        for (var i = 0; i < 5_000; i++) Assert.Equal(first, _engine.Evaluate(source, destination, 123_456, Idr));
    }

    [Fact]
    public void EngineIsSafeToCallFromManyThreads()
    {
        var failures = 0;
        Parallel.For(0, 64, worker =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 20_000; i++)
            {
                long balance = random.Next(0, 1_000_000), amount = random.Next(1, 1_200_000);
                var source = Deposit(balance);
                var destination = Deposit(0);
                var d = _engine.Evaluate(source, destination, amount, Idr);
                var ok = amount <= balance
                    ? d.Approved && d.DebitAccountBalanceAfter == balance - amount && d.CreditAccountBalanceAfter == amount
                      && d.DebitLine.AccountId == source.Id && d.CreditLine.AccountId == destination.Id
                    : d.Result == EngineResult.InsufficientFunds;
                if (!ok) Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);
    }

    [Fact]
    public unsafe void RawAbiRejectsNullAndMismatchedStructs()
    {
        AegisTransferRequest request = default;
        request.StructSize = (uint)sizeof(AegisTransferRequest);
        request.AbiVersion = NativeMethods.ExpectedAbiVersion;
        AegisTransferDecision decision = default;
        decision.StructSize = (uint)sizeof(AegisTransferDecision);

        Assert.Equal((int)EngineResult.NullArgument, NativeMethods.aegis_evaluate_transfer(&request, null));
        Assert.Equal((int)EngineResult.NullArgument, NativeMethods.aegis_evaluate_transfer(null, &decision));

        request.AbiVersion = 999;
        Assert.Equal((int)EngineResult.AbiMismatch, NativeMethods.aegis_evaluate_transfer(&request, &decision));

        decision.StructSize = 8;
        Assert.Equal((int)EngineResult.AbiMismatch, NativeMethods.aegis_evaluate_transfer(&request, &decision));
    }

    [Fact]
    public void ManagedResultCodesMirrorTheNativeNames()
    {
        (EngineResult Code, string Name)[] expected =
        [
            (EngineResult.Ok, "OK"), (EngineResult.NullArgument, "NULL_ARGUMENT"), (EngineResult.AbiMismatch, "ABI_MISMATCH"),
            (EngineResult.AmountNotPositive, "AMOUNT_NOT_POSITIVE"), (EngineResult.SameAccount, "SAME_ACCOUNT"),
            (EngineResult.CurrencyMismatch, "CURRENCY_MISMATCH"), (EngineResult.DebitAccountState, "DEBIT_ACCOUNT_NOT_ACTIVE"),
            (EngineResult.CreditAccountState, "CREDIT_ACCOUNT_NOT_ACTIVE"), (EngineResult.InsufficientFunds, "INSUFFICIENT_FUNDS"),
            (EngineResult.BalanceOverflow, "BALANCE_OVERFLOW"), (EngineResult.InvalidAccountData, "INVALID_ACCOUNT_DATA"),
            (EngineResult.Internal, "INTERNAL_ERROR"),
        ];
        Assert.Equal(Enum.GetValues<EngineResult>().Length, expected.Length);
        foreach (var (code, name) in expected) Assert.Equal(name, NativeTransferEngine.ResultName((int)code));
        // Every rejection name has a client-facing description.
        foreach (var (code, name) in expected.Where(e => e.Code is >= EngineResult.AmountNotPositive and <= EngineResult.BalanceOverflow))
            Assert.NotEqual("The transfer was rejected.", EngineMessages.Describe(name));
    }
}
