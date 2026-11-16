using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using AegisBank.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace AegisBank.Infrastructure.Queries;

public sealed class AdminQueries(AegisDbContext db, ITransferEngine engine, IHostEnvironment environment, TimeProvider clock) : IAdminQueries
{
    public static readonly string InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<OverviewDto> GetOverviewAsync(int days = 14, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var customerAccounts = db.Accounts.AsNoTracking().Where(a => a.CustomerId != null);

        var accountCount = await customerAccounts.LongCountAsync(ct);
        var activeCount = await customerAccounts.LongCountAsync(a => a.Status == AccountStatus.Active, ct);
        var customerCount = await db.Customers.LongCountAsync(ct);
        var totalBalance = await customerAccounts.SumAsync(a => (long?)a.AvailableBalanceMinorUnits, ct) ?? 0;
        var vault = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == SystemIds.VaultCashAccountId)
            .Select(a => (long?)a.AvailableBalanceMinorUnits).FirstOrDefaultAsync(ct) ?? 0;

        var byStatus = await db.Transfers.AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount(), Amount = g.Sum(t => t.AmountMinorUnits) })
            .ToListAsync(ct);
        var completed = byStatus.FirstOrDefault(s => s.Status == TransferStatus.Completed);
        var rejected = byStatus.FirstOrDefault(s => s.Status == TransferStatus.Rejected);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var firstDay = today.AddDays(-(days - 1));
        var since = firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var daily = await db.Transfers.AsNoTracking()
            .Where(t => t.CreatedAt >= since)
            .GroupBy(t => t.CreatedAt.Date)
            .Select(g => new
            {
                Day = g.Key,
                Completed = g.LongCount(t => t.Status == TransferStatus.Completed),
                Rejected = g.LongCount(t => t.Status == TransferStatus.Rejected),
                Amount = g.Sum(t => t.Status == TransferStatus.Completed ? t.AmountMinorUnits : 0),
            })
            .ToListAsync(ct);
        var byDay = daily.ToDictionary(d => DateOnly.FromDateTime(d.Day));
        var volume = Enumerable.Range(0, days).Select(i =>
        {
            var day = firstDay.AddDays(i);
            return byDay.TryGetValue(day, out var d)
                ? new DailyVolumeDto(day, d.Completed, d.Rejected, d.Amount)
                : new DailyVolumeDto(day, 0, 0, 0);
        }).ToList();

        var recent = await TransferMapping.Project(db.Transfers.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(8)).ToListAsync(ct);

        return new OverviewDto(accountCount, activeCount, customerCount, totalBalance, vault,
            (completed?.Count ?? 0) + (rejected?.Count ?? 0), completed?.Count ?? 0, rejected?.Count ?? 0,
            completed?.Amount ?? 0, Currencies.Idr.Code, volume, recent.Select(TransferMapping.ToDto).ToList());
    }

    public async Task<PagedResult<AuditLogDto>> ListAuditLogsAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page ?? new PageRequest();
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            var prefix = AccountQueries.EscapeLike(filter.Action.Trim()) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Action, prefix, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) query = query.Where(a => a.EntityType == filter.EntityType);
        if (filter.ActorId is { } actorId) query = query.Where(a => a.ActorId == actorId);
        if (filter.From is { } from) query = query.Where(a => a.CreatedAt >= from.ToUniversalTime());
        if (filter.To is { } to) query = query.Where(a => a.CreatedAt < to.ToUniversalTime());

        var total = await query.LongCountAsync(ct);
        var rows = await (
            from a in query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip(page.Skip).Take(page.SafePageSize)
            join u in db.Users.AsNoTracking() on a.ActorId equals (Guid?)u.Id into users
            from u in users.DefaultIfEmpty()
            orderby a.CreatedAt descending, a.Id descending
            select new AuditLogDto(a.Id, a.ActorId, u != null ? u.Username : null, a.Action, a.EntityType, a.EntityId,
                a.Metadata, a.CorrelationId, a.CreatedAt)).ToListAsync(ct);
        return new PagedResult<AuditLogDto>(rows, page.SafePage, page.SafePageSize, total);
    }

    public async Task<SystemHealthDto> GetSystemHealthAsync(CancellationToken ct = default)
    {
        var components = new List<ComponentHealth>();
        long pending = 0;
        IReadOnlyList<TransferDto> rejections = [];

        var databaseUp = false;
        try
        {
            var version = await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"").FirstAsync(ct);
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
            databaseUp = true;
            components.Add(new ComponentHealth("PostgreSQL", true, $"Connected, server {version}"));
            components.Add(new ComponentHealth("Schema migrations", pending == 0,
                pending == 0 ? "Up to date" : $"{pending} pending"));
        }
        catch (Exception)
        {
            components.Add(new ComponentHealth("PostgreSQL", false, "Connection failed"));
        }

        var info = engine.GetInfo();
        components.Add(info.Available
            ? new ComponentHealth("Native transaction engine", true, $"Loaded, version {info.Version}, ABI {info.AbiVersion}")
            : new ComponentHealth("Native transaction engine", false, $"Unavailable ({info.Error})"));

        if (databaseUp)
        {
            var rows = await TransferMapping.Project(db.Transfers.AsNoTracking()
                .Where(t => t.Status == TransferStatus.Rejected)
                .OrderByDescending(t => t.CreatedAt).Take(10)).ToListAsync(ct);
            rejections = rows.Select(TransferMapping.ToDto).ToList();
        }

        return new SystemHealthDto(components.All(c => c.Healthy), clock.GetUtcNow().UtcDateTime,
            InstanceId, environment.EnvironmentName, components, rejections, pending);
    }
}
