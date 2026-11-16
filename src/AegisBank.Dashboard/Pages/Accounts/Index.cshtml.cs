using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Accounts;

public sealed class IndexModel(IAccountQueries accounts) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public PagedResult<AccountDto> Result { get; private set; } = null!;
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Result = await accounts.ListAsync(Actor, new AccountFilter(Q, Status, Sort: Sort, Page: new PageRequest(P, 25)), ct);
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.Validation)
        {
            Error = ex.Message;
            Result = new PagedResult<AccountDto>([], 1, 25, 0);
        }
    }

    public string? PageUrl(int page) => Url.Page("/Accounts/Index", new { Q, Status, Sort, P = page });

    /// <summary>Link target for a sortable column header: toggles direction when already sorted by it.</summary>
    public string? SortUrl(string field)
    {
        var current = Sort ?? "accountNumber:asc";
        var next = current == field + ":asc" ? field + ":desc" : field + ":asc";
        return Url.Page("/Accounts/Index", new { Q, Status, Sort = next });
    }

    public string SortMark(string field) => (Sort ?? "accountNumber:asc") switch
    {
        var s when s == field + ":asc" => " ↑",
        var s when s == field + ":desc" => " ↓",
        _ => "",
    };
}
