using AegisBank.Application;

namespace AegisBank.Dashboard.Pages.Monitoring;

public sealed class IndexModel(IAdminQueries admin, BenchmarkResultsReader benchmarks) : ConsolePage
{
    public SystemHealthDto Health { get; private set; } = null!;
    public BenchmarkSummary? Benchmarks { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Health = await admin.GetSystemHealthAsync(ct);
        Benchmarks = benchmarks.Read();
    }
}
