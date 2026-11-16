using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AegisBank.Infrastructure.Seeding;

public sealed class SeedOptions
{
    /// <summary>Must be true for the seeder to do anything. Ignored (with an error) in Production.</summary>
    public bool Enabled { get; set; }
    public string? AdminPassword { get; set; }
    public string? AuditorPassword { get; set; }
    public string? CustomerPassword { get; set; }
}

/// <summary>
/// Creates a fictional demo dataset. Every person and account is invented.
/// Balances are created exclusively through opening-deposit postings, so the
/// seeded data satisfies the same ledger invariants as live data. The seeder
/// is idempotent: running it again changes nothing.
/// </summary>
public sealed class DemoSeeder(
    AegisDbContext db,
    ITransferService transfers,
    IPasswordHasher<User> hasher,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<DemoSeeder> logger)
{
    private sealed record DemoCustomer(string Number, string Name, string? Login, (string Number, string Name, long OpeningMajor)[] Accounts);

    private static readonly DemoCustomer[] Customers =
    [
        new("CIF900000001", "Ratna Kusumawardani", "ratna", [("2900000011", "Tabungan Harian", 25_000_000), ("2900000012", "Dana Pendidikan", 60_000_000)]),
        new("CIF900000002", "Bima Prasetyo", "bima", [("2900000021", "Tabungan Harian", 12_500_000)]),
        new("CIF900000003", "Sari Wulandari", "sari", [("2900000031", "Tabungan Harian", 8_750_000), ("2900000032", "Usaha Katering", 41_000_000)]),
        new("CIF900000004", "Yoga Hendrawan", null, [("2900000041", "Tabungan Harian", 3_200_000)]),
        new("CIF900000005", "Made Suartini", null, [("2900000051", "Tabungan Harian", 17_400_000)]),
        new("CIF900000006", "Fajar Nugroho", null, [("2900000061", "Operasional Toko", 95_000_000), ("2900000062", "Cadangan", 0)]),
    ];

    // (from, to, amount in rupiah). Includes two that the engine will reject.
    private static readonly (string From, string To, long Major)[] DemoTransfers =
    [
        ("2900000011", "2900000021", 1_250_000),
        ("2900000061", "2900000062", 20_000_000),
        ("2900000032", "2900000031", 3_500_000),
        ("2900000021", "2900000051", 400_000),
        ("2900000041", "2900000011", 5_000_000),   // insufficient funds
        ("2900000012", "2900000011", 2_000_000),
        ("2900000051", "2900000041", 750_000),
        ("2900000061", "2900000032", 8_250_000),
        ("2900000031", "2900000061", 99_000_000),  // insufficient funds
        ("2900000011", "2900000051", 310_000),
    ];

    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("Demo seed is disabled (Seed:Enabled=false)");
            return false;
        }
        if (environment.IsProduction())
        {
            logger.LogError("Demo seed is enabled but the environment is Production; refusing to seed");
            return false;
        }
        if (string.IsNullOrWhiteSpace(o.AdminPassword) || string.IsNullOrWhiteSpace(o.AuditorPassword) || string.IsNullOrWhiteSpace(o.CustomerPassword))
            throw new InvalidOperationException("Seed:AdminPassword, Seed:AuditorPassword and Seed:CustomerPassword must be configured to seed demo users.");

        // One seeder at a time across all instances sharing the database.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74651201)", ct);

        var now = clock.GetUtcNow().UtcDateTime;
        await EnsureUserAsync("ops.admin", "ops.admin@aegisbank.example", UserRole.Admin, null, o.AdminPassword!, now, ct);
        await EnsureUserAsync("audit.viewer", "audit.viewer@aegisbank.example", UserRole.Auditor, null, o.AuditorPassword!, now, ct);

        foreach (var demo in Customers)
        {
            var customer = await db.Customers.Include(c => c.Accounts).FirstOrDefaultAsync(c => c.CustomerNumber == demo.Number, ct);
            if (customer is null)
            {
                customer = new Customer
                {
                    Id = Guid.CreateVersion7(), CustomerNumber = demo.Number, FullName = demo.Name,
                    Email = $"{demo.Name.Split(' ')[0].ToLowerInvariant()}@customers.aegisbank.example", CreatedAt = now,
                };
                db.Customers.Add(customer);
            }
            foreach (var (number, name, _) in demo.Accounts)
            {
                if (customer.Accounts.Any(a => a.AccountNumber == number)) continue;
                customer.Accounts.Add(new Account
                {
                    Id = Guid.CreateVersion7(), AccountNumber = number, Name = name, Class = AccountClass.Liability,
                    Currency = Currencies.Idr.Code, Status = AccountStatus.Active, CreatedAt = now, UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);
            if (demo.Login is not null)
                await EnsureUserAsync(demo.Login, $"{demo.Login}@customers.aegisbank.example", UserRole.Customer, customer.Id, o.CustomerPassword!, now, ct);
        }
        await tx.CommitAsync(ct);

        // Money movement goes through the coordinator, like any other posting.
        var system = SystemIds.SystemActor("demo-seed");
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CustomerId != null).ToDictionaryAsync(a => a.AccountNumber, ct);
        var scale = (long)Math.Pow(10, Currencies.Idr.MinorUnitExponent);

        foreach (var (number, _, openingMajor) in Customers.SelectMany(c => c.Accounts))
        {
            if (openingMajor == 0) continue;
            await transfers.OpeningDepositAsync(system, accounts[number].Id, openingMajor * scale, $"seed-opening-{number}", ct);
        }

        var admin = await db.Users.AsNoTracking().FirstAsync(u => u.Username == "ops.admin", ct);
        var adminActor = new Actor(admin.Id, admin.Username, admin.Role, null, "demo-seed");
        for (var i = 0; i < DemoTransfers.Length; i++)
        {
            var (from, to, major) = DemoTransfers[i];
            await transfers.SubmitTransferAsync(adminActor,
                new SubmitTransfer(accounts[from].Id, accounts[to].Id, major * scale, Currencies.Idr.Code),
                $"seed-demo-transfer-{i + 1:D2}", ct);
        }

        logger.LogInformation("Demo dataset is in place ({Customers} customers, {Accounts} accounts)", Customers.Length, accounts.Count);
        return true;
    }

    private async Task EnsureUserAsync(string username, string email, UserRole role, Guid? customerId, string password, DateTime now, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Username == username, ct)) return;
        var user = new User
        {
            Id = Guid.CreateVersion7(), Username = username, Email = email, Role = role, CustomerId = customerId,
            IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }
}
