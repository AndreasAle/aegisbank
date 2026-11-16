using System.Net.Mail;
using System.Security.Cryptography;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using AegisBank.Infrastructure.Queries;
using AegisBank.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;

namespace AegisBank.Infrastructure.Admin;

public sealed class AccountAdminService(AegisDbContext db, TimeProvider clock) : IAccountAdminService
{
    public async Task<CustomerDto> CreateCustomerAsync(Actor actor, CreateCustomer request, CancellationToken ct = default)
    {
        RequireAdmin(actor);
        var name = (request.FullName ?? "").Trim();
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 120) throw AppException.Validation("invalid_name", "Full name must be 2-120 characters.");
        if (email.Length > 254 || !MailAddress.TryCreate(email, out _)) throw AppException.Validation("invalid_email", "Email address is not valid.");

        var now = clock.GetUtcNow().UtcDateTime;
        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            CustomerNumber = "CIF" + RandomDigits(9),
            FullName = name,
            Email = email,
            CreatedAt = now,
        };
        db.Customers.Add(customer);
        db.AuditLogs.Add(Audit.Entry(actor, "customer.created", "Customer", customer.Id.ToString(),
            new { customerNumber = customer.CustomerNumber }, clock));
        await db.SaveChangesAsync(ct);
        return new CustomerDto(customer.Id, customer.CustomerNumber, customer.FullName, customer.Email, customer.Status.ToString(), customer.CreatedAt);
    }

    public async Task<AccountDto> OpenAccountAsync(Actor actor, OpenAccount request, CancellationToken ct = default)
    {
        RequireAdmin(actor);
        var name = (request.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) throw AppException.Validation("invalid_name", "Account name must be 2-120 characters.");
        if (!Currencies.TryGet(request.Currency?.Trim().ToUpperInvariant(), out var currency))
            throw AppException.Validation("unsupported_currency", "Only IDR is supported.");

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == request.CustomerId, ct)
            ?? throw AppException.NotFound("Customer");
        if (customer.Status != CustomerStatus.Active)
            throw AppException.Conflict("customer_not_active", "The customer is not active.");

        var now = clock.GetUtcNow().UtcDateTime;
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            // Customer account numbers start with 2; system accounts start with 1.
            AccountNumber = "2" + RandomDigits(9),
            Name = name,
            CustomerId = customer.Id,
            Customer = customer,
            Class = AccountClass.Liability,
            Currency = currency.Code,
            AvailableBalanceMinorUnits = 0, // funds only ever arrive through the ledger
            Status = AccountStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        db.AuditLogs.Add(Audit.Entry(actor, "account.opened", "Account", account.Id.ToString(),
            new { accountNumber = account.AccountNumber, customerNumber = customer.CustomerNumber }, clock));
        await db.SaveChangesAsync(ct);
        return AccountQueries.ToDto(account);
    }

    public async Task<AccountDto> SetStatusAsync(Actor actor, Guid accountId, AccountStatus status, CancellationToken ct = default)
    {
        RequireAdmin(actor);
        if (!Enum.IsDefined(status)) throw AppException.Validation("invalid_status", "Unknown account status.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same row lock as transfers take, so a status change never interleaves with a posting.
        var account = (await db.Accounts
            .FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {accountId} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();
        if (account is null || account.IsSystemAccount) throw AppException.NotFound("Account");

        if (account.Status == AccountStatus.Closed && status != AccountStatus.Closed)
            throw AppException.Conflict("account_closed", "A closed account cannot be reopened.");
        if (status == AccountStatus.Closed && account.AvailableBalanceMinorUnits != 0)
            throw AppException.Conflict("balance_not_zero", "An account can only be closed with a zero balance.");

        var previous = account.Status;
        if (previous != status)
        {
            account.Status = status;
            account.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            account.Version++;
            db.AuditLogs.Add(Audit.Entry(actor, "account.status_changed", "Account", account.Id.ToString(),
                new { accountNumber = account.AccountNumber, from = previous.ToString(), to = status.ToString() }, clock));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        await db.Entry(account).Reference(a => a.Customer).LoadAsync(ct);
        return AccountQueries.ToDto(account);
    }

    private static void RequireAdmin(Actor actor)
    {
        if (actor.Role is not (UserRole.Admin or UserRole.System)) throw AppException.Forbidden();
    }

    private static string RandomDigits(int count)
    {
        Span<char> digits = stackalloc char[count];
        for (var i = 0; i < count; i++) digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(digits);
    }
}
