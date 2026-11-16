using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Ledger;

public sealed class JournalModel(ILedgerQueries ledger) : ConsolePage
{
    public JournalEntryDto Journal { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Journal = await ledger.GetJournalAsync(id, ct);
            return Page();
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return NotFound();
        }
    }
}
