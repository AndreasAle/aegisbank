using AegisBank.Application;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Transfers;

public sealed class DetailsModel(ITransferQueries transfers, ILedgerQueries ledger, ITransferService service) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public TransferDto Transfer { get; private set; } = null!;
    public JournalEntryDto? Journal { get; private set; }
    public string ReversalKey { get; private set; } = "";
    public bool CanReverse => IsAdmin && Transfer is { Status: "Completed" } && Transfer.Kind != "Reversal";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Transfer = await transfers.GetAsync(Actor, Id, ct);
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return NotFound();
        }
        Journal = await ledger.GetJournalForTransferAsync(Id, ct);
        ReversalKey = "console-reversal-" + Guid.CreateVersion7().ToString("N");
        return Page();
    }

    public async Task<IActionResult> OnPostReverseAsync(string reversalKey, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            var outcome = await service.ReverseAsync(Actor, Id, reversalKey, ct);
            if (outcome.Succeeded)
            {
                Flash($"Reversal {outcome.Transfer.ReferenceNumber} posted. The original entry is unchanged.");
                return RedirectToPage(new { id = outcome.Transfer.Id });
            }
            Flash($"Reversal rejected: {outcome.Transfer.FailureReason}", "bad");
        }
        catch (AppException ex)
        {
            Flash(ex.Message, "bad");
        }
        return RedirectToPage(new { Id });
    }
}
