using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Transfers;

public sealed class IndexModel(ITransferQueries transfers) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Reference { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public PagedResult<TransferDto> Result { get; private set; } = null!;
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Result = await transfers.ListAsync(Actor,
                new TransferFilter(Status, Kind, Reference: Reference, Sort: Sort, Page: new PageRequest(P, 25)), ct);
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.Validation)
        {
            Error = ex.Message;
            Result = new PagedResult<TransferDto>([], 1, 25, 0);
        }
    }

    public string? PageUrl(int page) => Url.Page("/Transfers/Index", new { Reference, Status, Kind, Sort, P = page });

    public string? SortUrl(string field)
    {
        var current = Sort ?? "createdAt:desc";
        var next = current == field + ":desc" ? field + ":asc" : field + ":desc";
        return Url.Page("/Transfers/Index", new { Reference, Status, Kind, Sort = next });
    }

    public string SortMark(string field) => (Sort ?? "createdAt:desc") switch
    {
        var s when s == field + ":asc" => " ↑",
        var s when s == field + ":desc" => " ↓",
        _ => "",
    };
}
