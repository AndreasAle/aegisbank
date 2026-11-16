using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure;
using AegisBank.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AegisBank.Tests.Shared;

/// <summary>
/// A real PostgreSQL database created for one test class and dropped
/// afterwards. The server is taken from AEGIS_TEST_DB (a connection string
/// without a database name); migrations are applied exactly as in production.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "AEGIS_TEST_DB";

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)
        ?? "Host=localhost;Port=5432;Username=aegis;Password=aegis-dev-only";

    public string Name { get; }
    public string ConnectionString { get; }

    private TestDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }

    public static async Task<TestDatabase> CreateAsync(int maxPoolSize = 100)
    {
        var name = "aegis_test_" + Guid.NewGuid().ToString("N")[..16];
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres", Pooling = false }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = name,
            MaxPoolSize = maxPoolSize,
            Timeout = 30,
            CommandTimeout = 60,
            IncludeErrorDetail = true,
        }.ConnectionString;

        var database = new TestDatabase(name, connectionString);
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        return database;
    }

    public AegisDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Builds an independent service container, i.e. one simulated application instance.</summary>
    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:Database"] = ConnectionString };
        foreach (var (key, value) in settings ?? []) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        configure?.Invoke(services);
        services.AddAegisInfrastructure(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AegisBank.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>Helpers that build test data through the same services production uses.</summary>
public static class TestBank
{
    public const long Rupiah = 100; // minor units per rupiah

    public static Actor System => SystemIds.SystemActor("test");

    public static async Task<Guid> CreateCustomerAsync(IServiceProvider services, string name = "Test Customer")
    {
        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAccountAdminService>();
        var customer = await admin.CreateCustomerAsync(System, new CreateCustomer(name, $"{Guid.NewGuid():N}@test.example"));
        return customer.Id;
    }

    /// <summary>Opens an account and funds it with an opening deposit through the ledger.</summary>
    public static async Task<AccountDto> OpenAccountAsync(IServiceProvider services, long balanceMinorUnits, Guid? customerId = null)
    {
        customerId ??= await CreateCustomerAsync(services);
        AccountDto account;
        await using (var scope = services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAccountAdminService>();
            account = await admin.OpenAccountAsync(System, new OpenAccount(customerId.Value, "Test account", "IDR"));
        }
        if (balanceMinorUnits > 0)
        {
            var outcome = await services.GetRequiredService<ITransferService>()
                .OpeningDepositAsync(System, account.Id, balanceMinorUnits, "open-" + account.Id.ToString("N"));
            if (!outcome.Succeeded) throw new InvalidOperationException("Opening deposit failed: " + outcome.Transfer.FailureCode);
        }
        return account;
    }

    /// <summary>An administrator actor backed by a real user row (transfers reference the initiating user).</summary>
    public static async Task<Actor> CreateAdminAsync(TestDatabase database, string username = "test.admin")
    {
        await using var db = database.CreateContext();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.CreateVersion7(), Username = username + "." + Guid.NewGuid().ToString("N")[..6],
            Email = $"{Guid.NewGuid():N}@test.example", PasswordHash = "!", Role = UserRole.Admin,
            IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, user.Username, UserRole.Admin, null, "test");
    }

    public static async Task<long> BalanceAsync(TestDatabase database, Guid accountId)
    {
        await using var db = database.CreateContext();
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.AvailableBalanceMinorUnits).SingleAsync();
    }

    public static async Task<LedgerVerificationReport> VerifyAsync(IServiceProvider services)
    {
        var report = await services.GetRequiredService<ILedgerVerifier>().VerifyAsync();
        return report;
    }

    public static void AssertConsistent(LedgerVerificationReport report)
    {
        var failed = report.Checks.Where(c => !c.Passed)
            .Select(c => $"{c.Name} ({c.Violations}): {string.Join("; ", c.Samples)}").ToList();
        if (failed.Count > 0) throw new Xunit.Sdk.XunitException("Ledger invariants violated:\n" + string.Join("\n", failed));
        if (!report.IsConsistent) throw new Xunit.Sdk.XunitException("Report is marked inconsistent.");
    }
}
