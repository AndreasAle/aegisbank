using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Accounts;

public sealed class DetailsModel(IAccountQueries accounts, IAccountAdminService admin, ITransferService transfers) : ConsolePage
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public AccountDto Account { get; private set; } = null!;
    public PagedResult<AccountTransactionDto> Postings { get; private set; } = null!;
    /// <summary>Key for the deposit form. Generated with the page so that a double submit is one deposit.</summary>
    public string DepositKey { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Account = await accounts.GetAsync(Actor, Id, ct);
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return NotFound();
        }
        Postings = await accounts.GetTransactionsAsync(Actor, Id, new PageRequest(P, 20), ct);
        DepositKey = "console-deposit-" + Guid.CreateVersion7().ToString("N");
        return Page();
    }

    public string? PageUrl(int page) => Url.Page("/Accounts/Details", new { Id, P = page });

    public async Task<IActionResult> OnPostStatusAsync(AccountStatus status, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            var updated = await admin.SetStatusAsync(Actor, Id, status, ct);
            Flash($"Account {updated.AccountNumber} is now {updated.Status}.");
        }
        catch (AppException ex)
        {
            Flash(ex.Message, "bad");
        }
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostDepositAsync(string amount, string depositKey, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (!Currencies.Idr.TryParseMajor(amount, out var minor) || minor <= 0)
        {
            Flash("Enter the amount in rupiah as a positive number, for example 250000.", "bad");
            return RedirectToPage(new { Id });
        }
        try
        {
            var outcome = await transfers.OpeningDepositAsync(Actor, Id, minor, depositKey, ct);
            if (outcome.Succeeded)
                Flash($"Deposit {outcome.Transfer.ReferenceNumber} posted: {Ui.Money(minor)}." + (outcome.Replayed ? " (already posted earlier; no new entry)" : ""));
            else
                Flash($"Deposit rejected: {outcome.Transfer.FailureReason}", "bad");
        }
        catch (AppException ex)
        {
            Flash(ex.Message, "bad");
        }
        return RedirectToPage(new { Id });
    }
}
