using System.ComponentModel.DataAnnotations;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Accounts;

[Authorize(Policy = Policies.Admin)]
public sealed class NewModel(IAccountAdminService admin, ITransferService transfers) : ConsolePage
{
    public sealed class InputModel
    {
        [Required, StringLength(120, MinimumLength = 2), Display(Name = "Customer name")]
        public string CustomerName { get; set; } = "";

        [Required, EmailAddress, StringLength(254), Display(Name = "Customer email")]
        public string CustomerEmail { get; set; } = "";

        [Required, StringLength(120, MinimumLength = 2), Display(Name = "Account name")]
        public string AccountName { get; set; } = "Tabungan Harian";

        [Display(Name = "Opening deposit")]
        public string? OpeningDeposit { get; set; }

        /// <summary>Generated with the form; makes the opening deposit idempotent across double submits.</summary>
        [Required] public string RequestKey { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public void OnGet() => Input.RequestKey = "console-open-" + Guid.CreateVersion7().ToString("N");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        long deposit = 0;
        if (!string.IsNullOrWhiteSpace(Input.OpeningDeposit) &&
            (!Currencies.Idr.TryParseMajor(Input.OpeningDeposit, out deposit) || deposit < 0))
        {
            ModelState.AddModelError("Input.OpeningDeposit", "Enter the amount in rupiah, for example 500000.");
        }
        if (!ModelState.IsValid) return Page();

        try
        {
            var customer = await admin.CreateCustomerAsync(Actor, new CreateCustomer(Input.CustomerName, Input.CustomerEmail), ct);
            var account = await admin.OpenAccountAsync(Actor, new OpenAccount(customer.Id, Input.AccountName, Currencies.Idr.Code), ct);
            if (deposit > 0)
            {
                var outcome = await transfers.OpeningDepositAsync(Actor, account.Id, deposit, Input.RequestKey, ct);
                if (!outcome.Succeeded)
                {
                    Flash($"Account {account.AccountNumber} was opened, but the opening deposit was rejected: {outcome.Transfer.FailureReason}", "warn");
                    return RedirectToPage("/Accounts/Details", new { id = account.Id });
                }
            }
            Flash($"Account {account.AccountNumber} opened for {customer.FullName}.");
            return RedirectToPage("/Accounts/Details", new { id = account.Id });
        }
        catch (AppException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
