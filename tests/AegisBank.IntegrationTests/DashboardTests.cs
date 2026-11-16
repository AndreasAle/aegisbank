extern alias dashboard;

using System.Net;
using System.Text.RegularExpressions;
using AegisBank.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AegisBank.IntegrationTests;

/// <summary>The Razor Pages console on its own database, seeded with the demo dataset.</summary>
public sealed class DashboardFixture : WebApplicationFactory<dashboard::Program>, IAsyncLifetime
{
    public const string AdminPassword = "Console-Admin-1!";
    public const string AuditorPassword = "Console-Auditor-1!";
    public const string CustomerPassword = "Console-Customer-1!";

    public TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync();
        _ = Server;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Database.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", Database.ConnectionString);
        builder.UseSetting("Security:RequireHttps", "false");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "1000");
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:AuditorPassword", AuditorPassword);
        builder.UseSetting("Seed:CustomerPassword", CustomerPassword);
    }

    public HttpClient NewBrowser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No antiforgery token found on {path}");
        return match.Groups[1].Value;
    }

    public async Task<HttpClient> SignedInAsync(string username, string password)
    {
        var client = NewBrowser();
        var response = await SignInAsync(client, username, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string username, string password)
    {
        var token = await TokenAsync(client, "/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }
}

public sealed class DashboardTests(DashboardFixture console) : IClassFixture<DashboardFixture>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/Accounts")]
    [InlineData("/Transfers")]
    [InlineData("/Transfers/New")]
    [InlineData("/Ledger")]
    [InlineData("/Monitoring")]
    [InlineData("/Audit")]
    public async Task PagesRedirectAnonymousVisitorsToSignIn(string path)
    {
        using var client = console.NewBrowser();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/", "Total customer balance")]
    [InlineData("/Accounts", "2900000011")]
    [InlineData("/Accounts?Q=wulandari", "Sari Wulandari")]
    [InlineData("/Transfers", "INSUFFICIENT_FUNDS")]
    [InlineData("/Transfers?Status=Completed&Kind=OpeningDeposit", "Opening deposit")]
    [InlineData("/Transfers/New", "Idempotency key")]
    [InlineData("/Ledger", "Ledger reconciled.")]
    [InlineData("/Monitoring", "Native transaction engine")]
    [InlineData("/Monitoring", "No benchmark results have been recorded")]
    [InlineData("/Audit", "transfer.completed")]
    [InlineData("/Accounts/New", "Opening deposit")]
    public async Task PagesRenderRealDataForAnAdministrator(string path, string expected)
    {
        using var client = await console.SignedInAsync("ops.admin", DashboardFixture.AdminPassword);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OverviewFiguresComeFromTheDatabase()
    {
        using var client = await console.SignedInAsync("ops.admin", DashboardFixture.AdminPassword);
        var html = await client.GetStringAsync("/");

        await using var db = console.Database.CreateContext();
        var total = await db.Accounts.Where(a => a.CustomerId != null).SumAsync(a => a.AvailableBalanceMinorUnits);
        Assert.Contains(Domain.Currencies.Idr.Format(total), html);
    }

    [Fact]
    public async Task WrongPasswordAndCustomerAccountsAreRefusedIdentically()
    {
        using var client = console.NewBrowser();
        var wrong = await DashboardFixture.SignInAsync(client, "ops.admin", "wrong-password");
        var customer = await DashboardFixture.SignInAsync(client, "ratna", DashboardFixture.CustomerPassword);

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
        Assert.Contains("incorrect", await wrong.Content.ReadAsStringAsync());
        Assert.Contains("incorrect", await customer.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task SessionCookieIsHttpOnlyAndSameSiteStrict()
    {
        using var client = console.NewBrowser();
        var response = await DashboardFixture.SignInAsync(client, "ops.admin", DashboardFixture.AdminPassword);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("aegis.console="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResponsesCarrySecurityHeaders()
    {
        using var client = console.NewBrowser();
        var response = await client.GetAsync("/Account/Login");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static Dictionary<string, string> TransferForm(Guid from, Guid to, string amount, string key, string? token)
    {
        var form = new Dictionary<string, string>
        {
            ["Input.SourceAccountId"] = from.ToString(),
            ["Input.DestinationAccountId"] = to.ToString(),
            ["Input.Amount"] = amount,
            ["Input.IdempotencyKey"] = key,
        };
        if (token is not null) form["__RequestVerificationToken"] = token;
        return form;
    }

    private async Task<(Guid From, Guid To)> TwoAccountsAsync()
    {
        await using var db = console.Database.CreateContext();
        var from = await db.Accounts.SingleAsync(a => a.AccountNumber == "2900000061");
        var to = await db.Accounts.SingleAsync(a => a.AccountNumber == "2900000051");
        return (from.Id, to.Id);
    }

    [Fact]
    public async Task TransferSimulatorPostsReplaysAndReportsConflicts()
    {
        using var client = await console.SignedInAsync("ops.admin", DashboardFixture.AdminPassword);
        var (from, to) = await TwoAccountsAsync();
        var before = await TestBank.BalanceAsync(console.Database, from);
        var token = await DashboardFixture.TokenAsync(client, "/Transfers/New");
        var key = "console-test-" + Guid.NewGuid().ToString("N");

        var first = await (await client.PostAsync("/Transfers/New", new FormUrlEncodedContent(TransferForm(from, to, "150000", key, token)))).Content.ReadAsStringAsync();
        Assert.Contains("First execution", first);
        Assert.Contains("Completed", first);
        Assert.Equal(before - 15_000_000, await TestBank.BalanceAsync(console.Database, from));

        var replay = await (await client.PostAsync("/Transfers/New", new FormUrlEncodedContent(TransferForm(from, to, "150000", key, token)))).Content.ReadAsStringAsync();
        Assert.Contains("Replayed", replay);
        Assert.Equal(before - 15_000_000, await TestBank.BalanceAsync(console.Database, from));

        var conflict = await (await client.PostAsync("/Transfers/New", new FormUrlEncodedContent(TransferForm(from, to, "150001", key, token)))).Content.ReadAsStringAsync();
        Assert.Contains("idempotency_key_conflict", conflict);
        Assert.Equal(before - 15_000_000, await TestBank.BalanceAsync(console.Database, from));

        var invalid = await (await client.PostAsync("/Transfers/New", new FormUrlEncodedContent(TransferForm(from, to, "12,5", "console-test-" + Guid.NewGuid().ToString("N"), token)))).Content.ReadAsStringAsync();
        Assert.Contains("Enter the amount in rupiah", invalid);

        var rejected = await (await client.PostAsync("/Transfers/New", new FormUrlEncodedContent(TransferForm(from, to, "999999999999", "console-test-" + Guid.NewGuid().ToString("N"), token)))).Content.ReadAsStringAsync();
        Assert.Contains("INSUFFICIENT_FUNDS", rejected);
    }

    [Fact]
    public async Task StateChangingRequestsWithoutAnAntiforgeryTokenAreRefused()
    {
        using var client = await console.SignedInAsync("ops.admin", DashboardFixture.AdminPassword);
        var (from, to) = await TwoAccountsAsync();
        var before = await TestBank.BalanceAsync(console.Database, from);

        var response = await client.PostAsync("/Transfers/New",
            new FormUrlEncodedContent(TransferForm(from, to, "1000", "console-csrf-" + Guid.NewGuid().ToString("N"), token: null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await TestBank.BalanceAsync(console.Database, from));
    }

    [Fact]
    public async Task AuditorCanReadButCannotTransferOrOpenAccounts()
    {
        using var client = await console.SignedInAsync("audit.viewer", DashboardFixture.AuditorPassword);
        var (from, to) = await TwoAccountsAsync();
        var before = await TestBank.BalanceAsync(console.Database, from);

        var page = await client.GetStringAsync("/Transfers/New");
        Assert.Contains("read-only", page);

        var token = await DashboardFixture.TokenAsync(client, "/Transfers/New");
        var post = await client.PostAsync("/Transfers/New",
            new FormUrlEncodedContent(TransferForm(from, to, "1000", "console-auditor-" + Guid.NewGuid().ToString("N"), token)));
        Assert.Contains(post.StatusCode, new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden });
        Assert.Equal(before, await TestBank.BalanceAsync(console.Database, from));

        var newAccount = await client.GetAsync("/Accounts/New");
        Assert.Equal(HttpStatusCode.Redirect, newAccount.StatusCode);
        Assert.Contains("AccessDenied", newAccount.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SignOutEndsTheSession()
    {
        using var client = await console.SignedInAsync("ops.admin", DashboardFixture.AdminPassword);
        var token = await DashboardFixture.TokenAsync(client, "/");
        var response = await client.PostAsync("/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Accounts")).StatusCode);
    }
}
