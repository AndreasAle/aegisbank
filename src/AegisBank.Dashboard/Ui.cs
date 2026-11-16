using System.Globalization;
using System.Text.Json;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AegisBank.Dashboard;

/// <summary>Formatting helpers shared by the Razor pages.</summary>
public static class Ui
{
    public static string Money(long minorUnits, string currency = "IDR") => Currencies.Get(currency).Format(minorUnits);
    public static string Amount(long minorUnits, string currency = "IDR") => Currencies.Get(currency).FormatPlain(minorUnits);

    public static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public static string Time(DateTime? utc) => utc is { } value ? Time(value) : "—";
    public static string Day(DateOnly day) => day.ToString("dd MMM", CultureInfo.InvariantCulture);
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>CSS modifier for a status label.</summary>
    public static string StatusClass(string status) => status switch
    {
        "Completed" or "Active" or "Healthy" or "Balanced" => "ok",
        "Rejected" or "Closed" or "Unhealthy" or "Unbalanced" => "bad",
        "Frozen" or "Suspended" => "warn",
        _ => "muted",
    };

    public static string KindLabel(string kind) => kind switch
    {
        "OpeningDeposit" => "Opening deposit",
        "Internal" => "Internal transfer",
        _ => kind,
    };

    /// <summary>Compact rupiah for chart axes: 1.2 jt, 3.4 M.</summary>
    public static string Compact(long minorUnits)
    {
        var rupiah = minorUnits / 100m;
        return rupiah switch
        {
            >= 1_000_000_000_000m => (rupiah / 1_000_000_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + " T",
            >= 1_000_000_000m => (rupiah / 1_000_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + " M",
            >= 1_000_000m => (rupiah / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + " jt",
            >= 1_000m => (rupiah / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + " rb",
            _ => rupiah.ToString("0", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>Base class: exposes the signed-in actor and common flags to pages.</summary>
public abstract class ConsolePage : PageModel
{
    public Actor Actor => User.ToActor(HttpContext);
    public bool IsAdmin => User.IsInRole("Admin");

    /// <summary>Message carried across a redirect.</summary>
    [TempData] public string? Notice { get; set; }
    [TempData] public string? NoticeKind { get; set; }

    protected void Flash(string message, string kind = "ok")
    {
        Notice = message;
        NoticeKind = kind;
    }
}

public sealed record PagerModel(int Page, int TotalPages, long TotalCount, int PageSize, Func<int, string?> Url)
{
    public long First => TotalCount == 0 ? 0 : (long)(Page - 1) * PageSize + 1;
    public long Last => Math.Min(TotalCount, (long)Page * PageSize);
}

// ---------------------------------------------------------------- benchmark results

public sealed class BenchmarkOptions
{
    /// <summary>Path of the summary file written by benchmarks/summarize.py.</summary>
    public string? SummaryPath { get; set; }
}

public sealed record BenchmarkEntry(string Suite, string Name, string Metric, double Value, string Unit);

public sealed record BenchmarkSummary(DateTime? RecordedAt, string? Machine, string? Notes, IReadOnlyList<BenchmarkEntry> Entries);

/// <summary>
/// Reads recorded benchmark results. Returns null when nothing has been
/// recorded; the console then says so instead of showing numbers.
/// </summary>
public sealed class BenchmarkResultsReader(IOptions<BenchmarkOptions> options, ILogger<BenchmarkResultsReader> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BenchmarkSummary? Read()
    {
        var path = options.Value.SummaryPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var summary = JsonSerializer.Deserialize<BenchmarkSummary>(File.ReadAllText(path), Json);
            return summary is { Entries.Count: > 0 } ? summary : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Benchmark summary at {Path} could not be read", path);
            return null;
        }
    }
}
