using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisBank.Api.Security;

public static class HealthResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes check names, states and fixed descriptions. Exception details are never included.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            }),
        }, Json));
    }
}
