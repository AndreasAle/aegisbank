using System.ComponentModel.DataAnnotations;
using AegisBank.Application;
using AegisBank.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Transfers;

public sealed class NewModel(IAccountQueries accounts, ITransferService transfers) : ConsolePage
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Choose a source account.")] public Guid? SourceAccountId { get; set; }
        [Required(ErrorMessage = "Choose a destination account.")] public Guid? DestinationAccountId { get; set; }
        [Required(ErrorMessage = "Enter an amount.")] public string? Amount { get; set; }
        [Required, StringLength(IdempotencyKeys.MaxLength, MinimumLength = IdempotencyKeys.MinLength)]
        public string IdempotencyKey { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public IReadOnlyList<AccountDto> Accounts { get; private set; } = [];
    public TransferOutcome? Outcome { get; private set; }
    /// <summary>Error that prevented the request from being processed at all (nothing was recorded).</summary>
    public string? RequestError { get; private set; }
    public string? RequestErrorCode { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Input.IdempotencyKey = NewKey();
        await LoadAccountsAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Auditors may open the page but cannot move funds.
        if (!IsAdmin) return Forbid();
        await LoadAccountsAsync(ct);

        long amount = 0;
        if (Input.Amount is not null && !Currencies.Idr.TryParseMajor(Input.Amount, out amount))
            ModelState.AddModelError("Input.Amount", "Enter the amount in rupiah using digits, for example 150000 or 150000.50.");
        if (!ModelState.IsValid) return Page();

        try
        {
            Outcome = await transfers.SubmitTransferAsync(Actor,
                new SubmitTransfer(Input.SourceAccountId!.Value, Input.DestinationAccountId!.Value, amount, Currencies.Idr.Code),
                Input.IdempotencyKey.Trim(), ct);
            // Balances in the selectors should reflect the posting.
            await LoadAccountsAsync(ct);
        }
        catch (AppException ex)
        {
            RequestError = ex.Message;
            RequestErrorCode = ex.Code;
        }
        // The key is intentionally kept in the form: submitting again shows the replay behaviour.
        return Page();
    }

    private async Task LoadAccountsAsync(CancellationToken ct) =>
        Accounts = (await accounts.ListAsync(Actor, new AccountFilter(Page: new PageRequest(1, PageRequest.MaxPageSize)), ct)).Items;

    private static string NewKey() => "console-" + Guid.CreateVersion7().ToString("N");
}
