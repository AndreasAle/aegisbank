using System.ComponentModel.DataAnnotations;
using AegisBank.Api.Security;
using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Api.Controllers;

/// <summary>Common paging parameters.</summary>
public sealed class PagingQuery
{
    /// <summary>1-based page number.</summary>
    [Range(1, 1_000_000)] public int Page { get; set; } = 1;
    /// <summary>Items per page (1-200).</summary>
    [Range(1, PageRequest.MaxPageSize)] public int PageSize { get; set; } = 25;
    public PageRequest ToRequest() => new(Page, PageSize);
}

[ApiController]
[Authorize]
[Route("api/accounts")]
[Produces("application/json")]
public sealed class AccountsController(IAccountQueries accounts) : ControllerBase
{
    /// <summary>Lists accounts visible to the caller. Customers see only their own accounts.</summary>
    /// <param name="search">Matches account number, account name or customer name.</param>
    /// <param name="status">Active, Frozen or Closed.</param>
    /// <param name="sort">accountNumber, balance, createdAt or status, optionally followed by :asc or :desc.</param>
    [HttpGet]
    public Task<PagedResult<AccountDto>> List([FromQuery] PagingQuery paging, [FromQuery, StringLength(100)] string? search,
        [FromQuery] string? status, [FromQuery] Guid? customerId, [FromQuery] string? sort, [FromQuery] bool includeSystem = false,
        CancellationToken ct = default) =>
        accounts.ListAsync(User.ToActor(HttpContext),
            new AccountFilter(search, status, customerId, includeSystem, sort, paging.ToRequest()), ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AccountDto> Get(Guid id, CancellationToken ct) => accounts.GetAsync(User.ToActor(HttpContext), id, ct);

    [HttpGet("{id:guid}/balance")]
    [ProducesResponseType<AccountBalanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AccountBalanceDto> Balance(Guid id, CancellationToken ct) => accounts.GetBalanceAsync(User.ToActor(HttpContext), id, ct);

    /// <summary>Ledger postings on the account, newest first.</summary>
    [HttpGet("{id:guid}/transactions")]
    [ProducesResponseType<PagedResult<AccountTransactionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PagedResult<AccountTransactionDto>> Transactions(Guid id, [FromQuery] PagingQuery paging, CancellationToken ct) =>
        accounts.GetTransactionsAsync(User.ToActor(HttpContext), id, paging.ToRequest(), ct);
}

public sealed record CreateTransferRequest(
    [Required] Guid? SourceAccountId,
    [Required] Guid? DestinationAccountId,
    [Required, Range(1, long.MaxValue)] long? AmountMinorUnits,
    [Required, RegularExpression("^[A-Za-z]{3}$")] string Currency);

[ApiController]
[Authorize]
[Route("api/transfers")]
[Produces("application/json")]
public sealed class TransfersController(ITransferService transfers, ITransferQueries queries) : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>Submits an internal transfer.</summary>
    /// <remarks>
    /// The Idempotency-Key header is required. Repeating a request with the same key and the same
    /// body returns the original result (header Idempotent-Replayed: true). Reusing a key with a
    /// different body returns 409.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<TransferDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        CreateTransferRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw AppException.Validation("idempotency_key_required", $"The {IdempotencyHeader} header is required.");

        var outcome = await transfers.SubmitTransferAsync(User.ToActor(HttpContext),
            new SubmitTransfer(request.SourceAccountId!.Value, request.DestinationAccountId!.Value,
                request.AmountMinorUnits!.Value, request.Currency),
            idempotencyKey, ct);
        return this.ToActionResult(outcome);
    }

    /// <summary>Lists transfers visible to the caller, newest first by default.</summary>
    /// <param name="sort">createdAt, amount or reference, optionally followed by :asc or :desc.</param>
    [HttpGet]
    public Task<PagedResult<TransferDto>> List([FromQuery] PagingQuery paging, [FromQuery] string? status, [FromQuery] string? kind,
        [FromQuery] Guid? accountId, [FromQuery, StringLength(32)] string? reference, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? sort, CancellationToken ct) =>
        queries.ListAsync(User.ToActor(HttpContext),
            new TransferFilter(status, kind, accountId, reference, from, to, sort, paging.ToRequest()), ct);

    [HttpGet("{id:guid}", Name = "GetTransfer")]
    [ProducesResponseType<TransferDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<TransferDto> Get(Guid id, CancellationToken ct) => queries.GetAsync(User.ToActor(HttpContext), id, ct);
}

internal static class TransferResults
{
    /// <summary>201 with the transfer for a completed posting, 422 Problem Details for a recorded rejection.</summary>
    public static IActionResult ToActionResult(this ControllerBase controller, TransferOutcome outcome)
    {
        controller.Response.Headers[TransfersController.ReplayedHeader] = outcome.Replayed ? "true" : "false";
        if (outcome.Succeeded)
            return controller.CreatedAtRoute("GetTransfer", new { id = outcome.Transfer.Id }, outcome.Transfer);

        var problem = Problems.Create(outcome.StatusCode, (outcome.Transfer.FailureCode ?? "REJECTED").ToLowerInvariant(),
            outcome.Transfer.FailureReason ?? "The transfer was rejected.");
        problem.Title = "Transfer rejected";
        problem.Extensions["transfer"] = outcome.Transfer;
        return Problems.Result(controller.HttpContext, problem);
    }
}

[ApiController]
[Authorize(Policy = Policies.Staff)]
[Route("api/ledger")]
[Produces("application/json")]
public sealed class LedgerController(ILedgerQueries ledger, ILedgerVerifier verifier) : ControllerBase
{
    /// <summary>Lists journal entries with their debit and credit lines.</summary>
    [HttpGet]
    public Task<PagedResult<JournalEntryDto>> List([FromQuery] PagingQuery paging, [FromQuery] Guid? accountId,
        [FromQuery, StringLength(32)] string? reference, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        ledger.ListJournalsAsync(new JournalFilter(accountId, reference, from, to, paging.ToRequest()), ct);

    /// <summary>
    /// Runs every ledger invariant check against one consistent snapshot. Responds 200 when the
    /// ledger is consistent and 409 with the same report when any check fails.
    /// </summary>
    [HttpGet("verify")]
    [ProducesResponseType<LedgerVerificationReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<LedgerVerificationReport>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        var report = await verifier.VerifyAsync(ct);
        return report.IsConsistent ? Ok(report) : Conflict(report);
    }

    [HttpGet("{journalId:guid}")]
    [ProducesResponseType<JournalEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<JournalEntryDto> Get(Guid journalId, CancellationToken ct) => ledger.GetJournalAsync(journalId, ct);
}
