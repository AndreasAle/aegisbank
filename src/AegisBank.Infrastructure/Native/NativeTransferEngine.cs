using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AegisBank.Application;

namespace AegisBank.Infrastructure.Native;

/// <summary>
/// Safe wrapper over the C ABI of the C++ engine. The wrapper owns all memory
/// passed to the engine (stack-allocated structs) and copies results into
/// managed value types before returning; no native pointer escapes.
/// </summary>
public sealed class NativeTransferEngine : ITransferEngine
{
    private static readonly Lazy<Exception?> LayoutCheck = new(VerifyLayout, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<int, string> ResultNames = new();

    public unsafe EngineDecision Evaluate(
        EngineAccount debitAccount, EngineAccount creditAccount, long amountMinorUnits, uint currencyNumeric)
    {
        EnsureUsable();

        AegisTransferRequest request = default;
        request.StructSize = (uint)sizeof(AegisTransferRequest);
        request.AbiVersion = NativeMethods.ExpectedAbiVersion;
        Fill(ref request.DebitAccount, debitAccount);
        Fill(ref request.CreditAccount, creditAccount);
        request.AmountMinor = amountMinorUnits;
        request.Currency = currencyNumeric;

        AegisTransferDecision decision = default;
        decision.StructSize = (uint)sizeof(AegisTransferDecision);

        var code = NativeMethods.aegis_evaluate_transfer(&request, &decision);

        if (code != decision.ResultCode || decision.AbiVersion != NativeMethods.ExpectedAbiVersion)
        {
            throw new EngineContractException(
                $"Engine returned an inconsistent decision (return {code}, decision {decision.ResultCode}, abi {decision.AbiVersion}).");
        }

        return new EngineDecision(
            Enum.IsDefined((EngineResult)code) ? (EngineResult)code : EngineResult.Internal,
            ResultName(code),
            decision.DebitAccountBalanceAfter,
            decision.CreditAccountBalanceAfter,
            ToInstruction(ref decision.DebitLine),
            ToInstruction(ref decision.CreditLine));
    }

    public EngineInfo GetInfo()
    {
        try
        {
            EnsureUsable();
            var version = Marshal.PtrToStringUTF8(NativeMethods.aegis_engine_version());
            return new EngineInfo(true, version, NativeMethods.aegis_abi_version(), null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or EngineContractException)
        {
            return new EngineInfo(false, null, 0, ex.GetType().Name);
        }
    }

    internal static string ResultName(int code) =>
        ResultNames.GetOrAdd(code, static c => Marshal.PtrToStringUTF8(NativeMethods.aegis_result_name(c)) ?? "UNKNOWN");

    private static void EnsureUsable()
    {
        if (LayoutCheck.Value is { } failure) throw failure;
    }

    /// <summary>
    /// Compares managed struct layouts and the ABI version with what the
    /// loaded library reports. A mismatch means the library and this
    /// assembly were built from different header versions.
    /// </summary>
    private static unsafe Exception? VerifyLayout()
    {
        try
        {
            var abi = NativeMethods.aegis_abi_version();
            if (abi != NativeMethods.ExpectedAbiVersion)
                return new EngineContractException($"Engine ABI version {abi} is not supported (expected {NativeMethods.ExpectedAbiVersion}).");

            (string Name, uint Managed, uint Native)[] probes =
            [
                ("aegis_account_snapshot", (uint)sizeof(AegisAccountSnapshot), NativeMethods.aegis_sizeof_account_snapshot()),
                ("aegis_transfer_request", (uint)sizeof(AegisTransferRequest), NativeMethods.aegis_sizeof_transfer_request()),
                ("aegis_ledger_instruction", (uint)sizeof(AegisLedgerInstruction), NativeMethods.aegis_sizeof_ledger_instruction()),
                ("aegis_transfer_decision", (uint)sizeof(AegisTransferDecision), NativeMethods.aegis_sizeof_transfer_decision()),
                ("offsetof(request.amount_minor)", (uint)Marshal.OffsetOf<AegisTransferRequest>(nameof(AegisTransferRequest.AmountMinor)), NativeMethods.aegis_offsetof_request_amount()),
                ("offsetof(decision.lines)", (uint)Marshal.OffsetOf<AegisTransferDecision>(nameof(AegisTransferDecision.DebitLine)), NativeMethods.aegis_offsetof_decision_lines()),
            ];
            foreach (var (name, managed, native) in probes)
            {
                if (managed != native)
                    return new EngineContractException($"Layout mismatch for {name}: managed {managed}, native {native}.");
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return ex;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Fill(ref AegisAccountSnapshot target, in EngineAccount source)
    {
        fixed (byte* id = target.AccountId)
        {
            source.Id.TryWriteBytes(new Span<byte>(id, 16));
        }
        target.BalanceMinor = source.BalanceMinorUnits;
        target.Currency = source.CurrencyNumeric;
        target.Status = (byte)source.Status;
        target.NormalSide = (byte)source.NormalSide;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe EngineLedgerInstruction ToInstruction(ref AegisLedgerInstruction line)
    {
        fixed (byte* id = line.AccountId)
        {
            return new EngineLedgerInstruction(new Guid(new ReadOnlySpan<byte>(id, 16)), line.DebitMinor, line.CreditMinor);
        }
    }
}

/// <summary>The native library violated the ABI contract. Never caused by user input.</summary>
public sealed class EngineContractException(string message) : Exception(message);
