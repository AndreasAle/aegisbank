using AegisBank.Application;

namespace AegisBank.Dashboard.Pages;

public sealed class IndexModel(IAdminQueries admin) : ConsolePage
{
    public OverviewDto Overview { get; private set; } = null!;
    public ChartModel Chart { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Overview = await admin.GetOverviewAsync(14, ct);
        Chart = ChartModel.From(Overview.DailyVolume);
    }

    /// <summary>Geometry of the daily transfer-count chart, computed on the server and drawn as plain SVG.</summary>
    public sealed record ChartModel(int Width, int Height, int PlotLeft, int PlotTop, int PlotWidth, int PlotHeight,
        IReadOnlyList<ChartModel.Bar> Bars, IReadOnlyList<ChartModel.Tick> Ticks)
    {
        public sealed record Bar(double X, double Width, double CompletedY, double CompletedHeight, double RejectedY, double RejectedHeight,
            string Label, string Tooltip, bool ShowLabel);
        public sealed record Tick(double Y, string Label);

        public static ChartModel From(IReadOnlyList<DailyVolumeDto> days)
        {
            const int width = 720, height = 220, left = 38, top = 10, right = 8, bottom = 26;
            var plotWidth = width - left - right;
            var plotHeight = height - top - bottom;

            var max = Math.Max(1, days.Count == 0 ? 1 : days.Max(d => d.CompletedCount + d.RejectedCount));
            var step = NiceStep(max);
            var top5 = (long)Math.Ceiling(max / (double)step) * step;

            var slot = plotWidth / (double)Math.Max(1, days.Count);
            var barWidth = Math.Max(4, slot * 0.62);
            var labelEvery = days.Count > 10 ? 2 : 1;

            var bars = days.Select((d, i) =>
            {
                var completedHeight = d.CompletedCount / (double)top5 * plotHeight;
                var rejectedHeight = d.RejectedCount / (double)top5 * plotHeight;
                var x = left + i * slot + (slot - barWidth) / 2;
                var completedY = top + plotHeight - completedHeight;
                return new Bar(x, barWidth, completedY, completedHeight, completedY - rejectedHeight, rejectedHeight,
                    Ui.Day(d.Day),
                    $"{Ui.Day(d.Day)}: {d.CompletedCount} completed, {d.RejectedCount} rejected, {Ui.Money(d.CompletedAmountMinorUnits)}",
                    i % labelEvery == 0 || i == days.Count - 1);
            }).ToList();

            var ticks = new List<Tick>();
            for (long v = 0; v <= top5; v += step)
                ticks.Add(new Tick(top + plotHeight - v / (double)top5 * plotHeight, Ui.Count(v)));

            return new ChartModel(width, height, left, top, plotWidth, plotHeight, bars, ticks);
        }

        private static long NiceStep(long max)
        {
            var raw = Math.Max(1, max / 4.0);
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var normalized = raw / magnitude;
            var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
            return Math.Max(1, (long)(nice * magnitude));
        }
    }
}
