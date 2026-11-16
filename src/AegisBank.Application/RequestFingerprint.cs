using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AegisBank.Domain;

namespace AegisBank.Application;

/// <summary>
/// Canonical fingerprint of a money-moving request. Two requests with the
/// same idempotency key are "the same request" only when their fingerprints
/// match. The canonical form is independent of JSON formatting, property
/// order and GUID casing.
/// </summary>
public static class RequestFingerprint
{
    public const string Version = "v1";

    public static string Compute(
        TransferKind kind, Guid actorId, Guid sourceAccountId, Guid destinationAccountId,
        long amountMinorUnits, string currency, Guid? reversalOfTransferId = null)
    {
        var canonical = string.Join('|',
            Version,
            ((int)kind).ToString(CultureInfo.InvariantCulture),
            actorId.ToString("N"),
            sourceAccountId.ToString("N"),
            destinationAccountId.ToString("N"),
            amountMinorUnits.ToString(CultureInfo.InvariantCulture),
            currency.Trim().ToUpperInvariant(),
            reversalOfTransferId?.ToString("N") ?? "-");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class IdempotencyKeys
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>Keys are 8-128 printable ASCII characters without spaces.</summary>
    public static bool IsValid(string? key)
    {
        if (key is null || key.Length < MinLength || key.Length > MaxLength) return false;
        foreach (var c in key)
        {
            if (c <= 0x20 || c >= 0x7F) return false;
        }
        return true;
    }
}
