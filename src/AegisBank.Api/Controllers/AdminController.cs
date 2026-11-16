using System.ComponentModel.DataAnnotations;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Api.Controllers;

public sealed record CreateCustomerRequest(
    [Required, StringLength(120, MinimumLength = 2)] string FullName,
    [Required, EmailAddress, StringLength(254)] string Email);

public sealed record OpenAccountRequest(
    [Required] Guid? CustomerId,
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    [Required, RegularExpression("^[A-Za-z]{3}$")] string Currency);

public sealed record DepositRequest([Required, Range(1, long.MaxValue)] long? AmountMinorUnits);

public sealed record SetAccountStatusRequest([Required] AccountStatus? Status);

/// <summary>Read endpoints are open to Admin and Auditor; every mutation requires Admin.</summary>
[ApiController]
[Authorize(Policy = Policies.Staff)]
[Route("api/admin")]
[Produces("application/json")]
public sealed class AdminController(
    IAdminQueries admin, ITransferQueries transferQueries, ITransferService transfers, IAccountAdminService accounts) : ControllerBase
{
    /// <summary>Headline figures, daily transfer volume and the most recent transfers.</summary>
    [HttpGet("overview")]
    public Task<OverviewDto> Overview([FromQuery, Range(1, 90)] int days = 14, CancellationToken ct = default) =>
        admin.GetOverviewAsync(days, ct);

    /// <summary>All transfers, including opening deposits and reversals.</summary>
    [HttpGet("transactions")]
    public Task<PagedResult<TransferDto>> Transactions([FromQuery] PagingQuery paging, [FromQuery] string? status, [FromQuery] string? kind,
        [FromQuery] Guid? accountId, [FromQuery, StringLength(32)] string? reference, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? sort, CancellationToken ct) =>
        transferQueries.ListAsync(User.ToActor(HttpContext),
            new TransferFilter(status, kind, accountId, reference, from, to, sort, paging.ToRequest()), ct);

    [HttpGet("audit-logs")]
    public Task<PagedResult<AuditLogDto>> AuditLogs([FromQuery] PagingQuery paging, [FromQuery, StringLength(64)] string? action,
        [FromQuery, StringLength(64)] string? entityType, [FromQuery] Guid? actorId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        CancellationToken ct) =>
        admin.ListAuditLogsAsync(new AuditLogFilter(action, entityType, actorId, from, to, paging.ToRequest()), ct);

    [HttpGet("system-health")]
    public Task<SystemHealthDto> SystemHealth(CancellationToken ct) => admin.GetSystemHealthAsync(ct);

    [HttpPost("customers")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateCustomer(CreateCustomerRequest request, CancellationToken ct)
    {
        var customer = await accounts.CreateCustomerAsync(User.ToActor(HttpContext), new CreateCustomer(request.FullName, request.Email), ct);
        return StatusCode(StatusCodes.Status201Created, customer);
    }

    /// <summary>Opens a customer account with a zero balance.</summary>
    [HttpPost("accounts")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<AccountDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> OpenAccount(OpenAccountRequest request, CancellationToken ct)
    {
        var account = await accounts.OpenAccountAsync(User.ToActor(HttpContext),
            new OpenAccount(request.CustomerId!.Value, request.Name, request.Currency), ct);
        return StatusCode(StatusCodes.Status201Created, account);
    }

    /// <summary>Funds an account from vault cash through a balanced opening journal entry.</summary>
    [HttpPost("accounts/{id:guid}/deposits")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<TransferDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Deposit(Guid id, [FromHeader(Name = TransfersController.IdempotencyHeader)] string? idempotencyKey,
        DepositRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw AppException.Validation("idempotency_key_required", $"The {TransfersController.IdempotencyHeader} header is required.");
        var outcome = await transfers.OpeningDepositAsync(User.ToActor(HttpContext), id, request.AmountMinorUnits!.Value, idempotencyKey, ct);
        return this.ToActionResult(outcome);
    }

    [HttpPut("accounts/{id:guid}/status")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    public Task<AccountDto> SetStatus(Guid id, SetAccountStatusRequest request, CancellationToken ct) =>
        accounts.SetStatusAsync(User.ToActor(HttpContext), id, request.Status!.Value, ct);

    /// <summary>Posts a compensating journal entry for a completed transfer. The original is never edited.</summary>
    [HttpPost("transfers/{id:guid}/reversal")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<TransferDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Reverse(Guid id, [FromHeader(Name = TransfersController.IdempotencyHeader)] string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw AppException.Validation("idempotency_key_required", $"The {TransfersController.IdempotencyHeader} header is required.");
        var outcome = await transfers.ReverseAsync(User.ToActor(HttpContext), id, idempotencyKey, ct);
        return this.ToActionResult(outcome);
    }
}
