using System.Reflection;
using System.Runtime.InteropServices;

namespace AegisBank.Infrastructure.Native;

// Managed mirrors of the structs in src/AegisBank.Engine/include/aegis/engine.h.
// All of them are blittable: they are passed by pointer without marshalling.
// Layouts are verified against the native library at start-up
// (NativeTransferEngine.VerifyLayout) and by unit tests.

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct AegisAccountSnapshot
{
    public fixed byte AccountId[16];
    public long BalanceMinor;
    public uint Currency;
    public byte Status;
    public byte NormalSide;
    public fixed byte Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct AegisTransferRequest
{
    public uint StructSize;
    public uint AbiVersion;
    public AegisAccountSnapshot DebitAccount;
    public AegisAccountSnapshot CreditAccount;
    public long AmountMinor;
    public uint Currency;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct AegisLedgerInstruction
{
    public fixed byte AccountId[16];
    public long DebitMinor;
    public long CreditMinor;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct AegisTransferDecision
{
    public uint StructSize;
    public uint AbiVersion;
    public int ResultCode;
    public uint Reserved;
    public long DebitAccountBalanceAfter;
    public long CreditAccountBalanceAfter;
    public AegisLedgerInstruction DebitLine;
    public AegisLedgerInstruction CreditLine;
}

internal static unsafe partial class NativeMethods
{
    public const string Library = "aegis_engine";
    public const uint ExpectedAbiVersion = 1;

    /// <summary>
    /// Optional override: full path of the engine library, or a directory
    /// containing it. Without it the default .NET probing rules apply
    /// (application directory first).
    /// </summary>
    public const string PathEnvironmentVariable = "AEGIS_ENGINE_PATH";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;

        var overridePath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var candidate = Directory.Exists(overridePath)
                ? Path.Combine(overridePath, PlatformFileName)
                : overridePath;
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }

        var local = Path.Combine(AppContext.BaseDirectory, PlatformFileName);
        if (File.Exists(local) && NativeLibrary.TryLoad(local, out var localHandle)) return localHandle;

        return IntPtr.Zero; // fall back to default probing
    }

    private static string PlatformFileName =>
        OperatingSystem.IsWindows() ? "aegis_engine.dll"
        : OperatingSystem.IsMacOS() ? "libaegis_engine.dylib"
        : "libaegis_engine.so";

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int aegis_evaluate_transfer(AegisTransferRequest* request, AegisTransferDecision* decision);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int aegis_evaluate_transfer_batch(AegisTransferRequest* requests, AegisTransferDecision* decisions, nuint count);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_abi_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr aegis_engine_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr aegis_result_name(int resultCode);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_sizeof_account_snapshot();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_sizeof_transfer_request();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_sizeof_ledger_instruction();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_sizeof_transfer_decision();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_offsetof_request_amount();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint aegis_offsetof_decision_lines();
}
