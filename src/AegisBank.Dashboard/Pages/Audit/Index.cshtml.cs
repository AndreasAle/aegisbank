using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Audit;

public sealed class IndexModel(IAdminQueries admin) : ConsolePage
{
    public static readonly (string Value, string Label)[] Categories =
    [
        ("", "All events"),
        ("auth.", "Security: sign-in and tokens"),
        ("transfer.", "Transfers"),
        ("account.", "Account administration"),
        ("customer.", "Customer administration"),
    ];

    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public PagedResult<AuditLogDto> Result { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) =>
        Result = await admin.ListAuditLogsAsync(new AuditLogFilter(Action, Page: new PageRequest(P, 30)), ct);

    public string? PageUrl(int page) => Url.Page("/Audit/Index", new { Action, P = page });
}
