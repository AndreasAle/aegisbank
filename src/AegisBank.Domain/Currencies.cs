using System.Globalization;

namespace AegisBank.Domain;

/// <summary>ISO 4217 currency. Amounts are always stored as integer minor units.</summary>
public sealed record Currency(string Code, uint NumericCode, int MinorUnitExponent, string Symbol)
{
    private static readonly NumberFormatInfo IdFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
    };

    /// <summary>Formats minor units for display, e.g. 10000000 -> "Rp 100.000,00".</summary>
    public string Format(long minorUnits)
    {
        var divisor = (decimal)Math.Pow(10, MinorUnitExponent);
        var major = minorUnits / divisor;
        return $"{Symbol} {major.ToString("N" + MinorUnitExponent, IdFormat)}";
    }

    /// <summary>Formats without the currency symbol.</summary>
    public string FormatPlain(long minorUnits)
    {
        var divisor = (decimal)Math.Pow(10, MinorUnitExponent);
        return (minorUnits / divisor).ToString("N" + MinorUnitExponent, IdFormat);
    }

    /// <summary>Parses a decimal major-unit string ("1500000" or "1500000.50") into minor units.</summary>
    public bool TryParseMajor(string? text, out long minorUnits)
    {
        minorUnits = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var major))
            return false;
        var scaled = major * (decimal)Math.Pow(10, MinorUnitExponent);
        if (scaled != decimal.Truncate(scaled)) return false;
        if (scaled < 0 || scaled > long.MaxValue) return false;
        minorUnits = (long)scaled;
        return true;
    }
}

public static class Currencies
{
    /// <summary>Indonesian rupiah. ISO 4217 defines two minor-unit digits (sen).</summary>
    public static readonly Currency Idr = new("IDR", 360, 2, "Rp");

    private static readonly Dictionary<string, Currency> ByCode =
        new(StringComparer.Ordinal) { [Idr.Code] = Idr };

    public static bool TryGet(string? code, out Currency currency)
    {
        currency = Idr;
        return code is not null && ByCode.TryGetValue(code, out currency!);
    }

    public static Currency Get(string code) =>
        TryGet(code, out var c) ? c : throw new ArgumentException($"Unsupported currency '{code}'.", nameof(code));
}
