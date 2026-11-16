using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using AegisBank.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;

namespace AegisBank.Infrastructure.Queries;

public sealed class AccountQueries(AegisDbContext db, TimeProvider clock) : IAccountQueries
{
    private static readonly string[] SortFields = ["accountNumber", "balance", "createdAt", "status"];

    public async Task<PagedResult<AccountDto>> ListAsync(Actor actor, AccountFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page ?? new PageRequest();
        var query = Scope(actor, db.Accounts.AsNoTracking());

        if (!(actor.IsStaff && filter.IncludeSystemAccounts)) query = query.Where(a => a.CustomerId != null);
        if (filter.CustomerId is { } customerId) query = query.Where(a => a.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = TransferMapping.ParseEnum<AccountStatus>(filter.Status, "status");
            query = query.Where(a => a.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = "%" + EscapeLike(filter.Search.Trim()) + "%";
            query = query.Where(a =>
                EF.Functions.ILike(a.AccountNumber, pattern, "\\") ||
                EF.Functions.ILike(a.Name, pattern, "\\") ||
                (a.Customer != null && EF.Functions.ILike(a.Customer.FullName, pattern, "\\")));
        }

        var sort = SortSpec.Parse(filter.Sort, SortFields, "accountNumber", false);
        query = (sort.Field, sort.Descending) switch
        {
            ("balance", false) => query.OrderBy(a => a.AvailableBalanceMinorUnits).ThenBy(a => a.Id),
            ("balance", true) => query.OrderByDescending(a => a.AvailableBalanceMinorUnits).ThenBy(a => a.Id),
            ("createdAt", false) => query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id),
            ("createdAt", true) => query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id),
            ("status", false) => query.OrderBy(a => a.Status).ThenBy(a => a.AccountNumber),
            ("status", true) => query.OrderByDescending(a => a.Status).ThenBy(a => a.AccountNumber),
            (_, true) => query.OrderByDescending(a => a.AccountNumber),
            _ => query.OrderBy(a => a.AccountNumber),
        };

        var total = await query.LongCountAsync(ct);
        var rows = await query.Skip(page.Skip).Take(page.SafePageSize).Include(a => a.Customer).ToListAsync(ct);
        return new PagedResult<AccountDto>(rows.Select(ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AccountDto> GetAsync(Actor actor, Guid id, CancellationToken ct = default) =>
        ToDto(await LoadAsync(actor, id, ct));

    public async Task<AccountBalanceDto> GetBalanceAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var a = await LoadAsync(actor, id, ct);
        return new AccountBalanceDto(a.Id, a.AccountNumber, a.Currency, a.AvailableBalanceMinorUnits,
            clock.GetUtcNow().UtcDateTime, a.Version);
    }

    public async Task<PagedResult<AccountTransactionDto>> GetTransactionsAsync(Actor actor, Guid id, PageRequest page, CancellationToken ct = default)
    {
        await LoadAsync(actor, id, ct);
        var query = db.LedgerLines.AsNoTracking().Where(l => l.AccountId == id);
        var total = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Select(l => new
            {
                l.Id,
                l.JournalEntryId,
                l.JournalEntry!.TransferId,
                l.JournalEntry.ReferenceNumber,
                l.JournalEntry.Transfer!.Kind,
                l.DebitMinorUnits,
                l.CreditMinorUnits,
                l.JournalEntry.Currency,
                Counterparty = l.JournalEntry.Lines.Where(o => o.Id != l.Id).Select(o => o.Account!.AccountNumber).FirstOrDefault(),
                l.CreatedAt,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new AccountTransactionDto(
            r.Id, r.JournalEntryId, r.TransferId, r.ReferenceNumber, r.Kind.ToString(),
            r.DebitMinorUnits > 0 ? "Debit" : "Credit",
            r.DebitMinorUnits > 0 ? r.DebitMinorUnits : r.CreditMinorUnits,
            r.Currency, r.Counterparty, r.CreatedAt)).ToList();
        return new PagedResult<AccountTransactionDto>(items, page.SafePage, page.SafePageSize, total);
    }

    private async Task<Account> LoadAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().Include(a => a.Customer).FirstOrDefaultAsync(a => a.Id == id, ct);
        // Accounts of other customers are reported as missing, not as forbidden.
        if (account is null || !actor.CanView(account)) throw AppException.NotFound("Account");
        return account;
    }

    private static IQueryable<Account> Scope(Actor actor, IQueryable<Account> query)
    {
        if (actor.IsStaff) return query;
        if (actor.Role == UserRole.Customer && actor.CustomerId is { } customerId)
            return query.Where(a => a.CustomerId == customerId);
        return query.Where(a => false);
    }

    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static AccountDto ToDto(Account a) => new(
        a.Id, a.AccountNumber, a.Name, a.Class.ToString(), a.NormalSide.ToString(),
        a.CustomerId, a.Customer?.CustomerNumber, a.Customer?.FullName,
        a.Currency, a.AvailableBalanceMinorUnits, a.Status.ToString(), a.CreatedAt, a.UpdatedAt, a.Version);
}

public sealed class TransferQueries(AegisDbContext db) : ITransferQueries
{
    private static readonly string[] SortFields = ["createdAt", "amount", "reference"];

    public async Task<PagedResult<TransferDto>> ListAsync(Actor actor, TransferFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page ?? new PageRequest();
        var query = Scope(actor, db.Transfers.AsNoTracking());

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = TransferMapping.ParseEnum<TransferStatus>(filter.Status, "status");
            query = query.Where(t => t.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            var kind = TransferMapping.ParseEnum<TransferKind>(filter.Kind, "kind");
            query = query.Where(t => t.Kind == kind);
        }
        if (filter.AccountId is { } accountId)
            query = query.Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId);
        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var pattern = AccountQueries.EscapeLike(filter.Reference.Trim()) + "%";
            query = query.Where(t => EF.Functions.ILike(t.ReferenceNumber, pattern, "\\"));
        }
        if (filter.From is { } from) query = query.Where(t => t.CreatedAt >= from.ToUniversalTime());
        if (filter.To is { } to) query = query.Where(t => t.CreatedAt < to.ToUniversalTime());

        var sort = SortSpec.Parse(filter.Sort, SortFields, "createdAt", true);
        query = (sort.Field, sort.Descending) switch
        {
            ("amount", false) => query.OrderBy(t => t.AmountMinorUnits).ThenBy(t => t.Id),
            ("amount", true) => query.OrderByDescending(t => t.AmountMinorUnits).ThenBy(t => t.Id),
            ("reference", false) => query.OrderBy(t => t.ReferenceNumber),
            ("reference", true) => query.OrderByDescending(t => t.ReferenceNumber),
            (_, false) => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            _ => query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
        };

        var total = await query.LongCountAsync(ct);
        var rows = await TransferMapping.Project(query.Skip(page.Skip).Take(page.SafePageSize)).ToListAsync(ct);
        return new PagedResult<TransferDto>(rows.Select(TransferMapping.ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<TransferDto> GetAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var row = await TransferMapping.Project(Scope(actor, db.Transfers.AsNoTracking()).Where(t => t.Id == id))
            .FirstOrDefaultAsync(ct);
        return row is null ? throw AppException.NotFound("Transfer") : TransferMapping.ToDto(row);
    }

    /// <summary>Customers see transfers in which one of their accounts takes part.</summary>
    private static IQueryable<Transfer> Scope(Actor actor, IQueryable<Transfer> query)
    {
        if (actor.IsStaff) return query;
        if (actor.Role == UserRole.Customer && actor.CustomerId is { } customerId)
            return query.Where(t => t.SourceAccount!.CustomerId == customerId || t.DestinationAccount!.CustomerId == customerId);
        return query.Where(t => false);
    }
}

public sealed class LedgerQueries(AegisDbContext db) : ILedgerQueries
{
    public async Task<PagedResult<JournalEntryDto>> ListJournalsAsync(JournalFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page ?? new PageRequest();
        var query = db.JournalEntries.AsNoTracking();

        if (filter.AccountId is { } accountId) query = query.Where(j => j.Lines.Any(l => l.AccountId == accountId));
        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var pattern = AccountQueries.EscapeLike(filter.Reference.Trim()) + "%";
            query = query.Where(j => EF.Functions.ILike(j.ReferenceNumber, pattern, "\\"));
        }
        if (filter.From is { } from) query = query.Where(j => j.CreatedAt >= from.ToUniversalTime());
        if (filter.To is { } to) query = query.Where(j => j.CreatedAt < to.ToUniversalTime());

        var total = await query.LongCountAsync(ct);
        var journals = await query
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Include(j => j.Lines.OrderBy(l => l.LineNumber)).ThenInclude(l => l.Account)
            .AsSplitQuery()
            .ToListAsync(ct);
        return new PagedResult<JournalEntryDto>(journals.Select(ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<JournalEntryDto> GetJournalAsync(Guid id, CancellationToken ct = default)
    {
        var journal = await Detailed().FirstOrDefaultAsync(j => j.Id == id, ct);
        return journal is null ? throw AppException.NotFound("Journal entry") : ToDto(journal);
    }

    public async Task<JournalEntryDto?> GetJournalForTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        var journal = await Detailed().FirstOrDefaultAsync(j => j.TransferId == transferId, ct);
        return journal is null ? null : ToDto(journal);
    }

    private IQueryable<JournalEntry> Detailed() =>
        db.JournalEntries.AsNoTracking().Include(j => j.Lines.OrderBy(l => l.LineNumber)).ThenInclude(l => l.Account);

    private static JournalEntryDto ToDto(JournalEntry j)
    {
        var debit = j.Lines.Sum(l => l.DebitMinorUnits);
        var credit = j.Lines.Sum(l => l.CreditMinorUnits);
        return new JournalEntryDto(j.Id, j.TransferId, j.ReferenceNumber, j.Currency, j.Description, j.CreatedAt,
            debit, credit, debit == credit && j.Lines.Count >= 2,
            j.Lines.OrderBy(l => l.LineNumber).Select(l => new LedgerLineDto(
                l.Id, l.LineNumber, l.AccountId, l.Account?.AccountNumber ?? "", l.Account?.Name ?? "",
                l.DebitMinorUnits, l.CreditMinorUnits)).ToList());
    }
}
