using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Ledger;

public sealed class IndexModel(ILedgerQueries ledger, ILedgerVerifier verifier) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public string? Reference { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public PagedResult<JournalEntryDto> Journals { get; private set; } = null!;
    public LedgerVerificationReport Report { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Journals = await ledger.ListJournalsAsync(new JournalFilter(Reference: Reference, Page: new PageRequest(P, 15)), ct);
        // Reconciliation is computed from the database on every load; nothing is cached.
        Report = await verifier.VerifyAsync(ct);
    }

    public string? PageUrl(int page) => Url.Page("/Ledger/Index", new { Reference, P = page });
}
