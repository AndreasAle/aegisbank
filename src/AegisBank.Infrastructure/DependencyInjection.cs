using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Admin;
using AegisBank.Infrastructure.Auth;
using AegisBank.Infrastructure.Ledger;
using AegisBank.Infrastructure.Native;
using AegisBank.Infrastructure.Persistence;
using AegisBank.Infrastructure.Queries;
using AegisBank.Infrastructure.Seeding;
using AegisBank.Infrastructure.Transfers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisBank.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Database";

    public static IServiceCollection AddAegisInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Connection string 'Database' is not configured. Set the ConnectionStrings__Database environment variable.");

        // No automatic retrying execution strategy: transfers manage their own
        // transactions and retry the whole unit of work explicitly.
        services.AddDbContextFactory<AegisDbContext>(o => o.UseNpgsql(connectionString));

        services.Configure<TransferOptions>(configuration.GetSection("Transfers"));
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<SeedOptions>(configuration.GetSection("Seed"));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITransferEngine, NativeTransferEngine>();
        services.TryAddSingleton<ITransferFaultInjector, NoFaults>();
        services.AddSingleton<ITransferService, TransferCoordinator>();
        services.AddSingleton<ILedgerVerifier, LedgerVerifier>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<IAccountQueries, AccountQueries>();
        services.AddScoped<ITransferQueries, TransferQueries>();
        services.AddScoped<ILedgerQueries, LedgerQueries>();
        services.AddScoped<IAdminQueries, AdminQueries>();
        services.AddScoped<IAccountAdminService, AccountAdminService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<DemoSeeder>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<EngineHealthCheck>("native-engine", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Applies pending migrations when Database:MigrateOnStartup is true and
    /// runs the demo seed when Seed:Enabled is true. Concurrent instances are
    /// serialized by a PostgreSQL advisory lock.
    /// </summary>
    public static async Task InitializeAegisDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AegisBank.Startup");
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();

        if (configuration.GetValue("Database:MigrateOnStartup", false))
        {
            // EF Core takes its own database-wide migration lock, so several
            // instances starting at once apply each migration exactly once.
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Database migrations are up to date");
        }

        if (configuration.GetValue("Seed:Enabled", false))
            await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(ct);
    }
}

internal sealed class DatabaseHealthCheck(IDbContextFactory<AegisDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("PostgreSQL reachable")
                : HealthCheckResult.Unhealthy("PostgreSQL unreachable");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable");
        }
    }
}

internal sealed class EngineHealthCheck(ITransferEngine engine) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var info = engine.GetInfo();
        if (!info.Available) return Task.FromResult(HealthCheckResult.Unhealthy("Native engine library could not be loaded"));

        // Known-answer probe: 100 from an account holding 150 must leave 50.
        var a = new EngineAccount(Guid.Parse("00000000-0000-0000-0000-000000000001"), 150, 360, AccountStatus.Active, NormalSide.Credit);
        var b = new EngineAccount(Guid.Parse("00000000-0000-0000-0000-000000000002"), 0, 360, AccountStatus.Active, NormalSide.Credit);
        var decision = engine.Evaluate(a, b, 100, 360);
        return Task.FromResult(decision is { Approved: true, DebitAccountBalanceAfter: 50, CreditAccountBalanceAfter: 100 }
            ? HealthCheckResult.Healthy($"Engine {info.Version}, ABI {info.AbiVersion}")
            : HealthCheckResult.Unhealthy("Engine returned an unexpected answer to the self-test"));
    }
}
