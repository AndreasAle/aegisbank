using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisBank.Api.Controllers;
using AegisBank.Application;
using AegisBank.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AegisBank.IntegrationTests;

/// <summary>
/// The real API host (Program.cs, full middleware pipeline) on a private
/// PostgreSQL database, with the fictional demo dataset seeded.
/// </summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "Test-Admin-Pass-1!";
    public const string AuditorPassword = "Test-Auditor-Pass-1!";
    public const string CustomerPassword = "Test-Customer-Pass-1!";
    public const string Ratna1 = "2900000011";
    public const string Ratna2 = "2900000012";
    public const string Bima1 = "2900000021";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public TestDatabase Database { get; private set; } = null!;

    protected virtual Dictionary<string, string> Settings => new()
    {
        ["ConnectionStrings:Database"] = Database.ConnectionString,
        ["Database:MigrateOnStartup"] = "false",
        ["Jwt:SigningKey"] = "integration-test-signing-key-0123456789abcdef",
        ["Security:RequireHttps"] = "false",
        ["OpenApi:Enabled"] = "true",
        ["RateLimiting:Enabled"] = "false",
        ["Auth:MaxFailedLogins"] = "3",
        ["Seed:Enabled"] = "true",
        ["Seed:AdminPassword"] = AdminPassword,
        ["Seed:AuditorPassword"] = AuditorPassword,
        ["Seed:CustomerPassword"] = CustomerPassword,
    };

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync();
        _ = Server; // start the host (runs the seed)
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Database.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
    }

    // ------------------------------------------------------------ helpers

    public async Task<TokenResponse> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    public async Task<HttpClient> ClientForAsync(string username, string password)
    {
        var tokens = await LoginAsync(username, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public Task<HttpClient> AdminAsync() => ClientForAsync("ops.admin", AdminPassword);
    public Task<HttpClient> AuditorAsync() => ClientForAsync("audit.viewer", AuditorPassword);
    public Task<HttpClient> RatnaAsync() => ClientForAsync("ratna", CustomerPassword);
    public Task<HttpClient> BimaAsync() => ClientForAsync("bima", CustomerPassword);

    public static async Task<AccountDto> AccountByNumberAsync(HttpClient client, string accountNumber)
    {
        var page = await client.GetFromJsonAsync<PagedResult<AccountDto>>($"/api/accounts?search={accountNumber}", Json);
        return Assert.Single(page!.Items, a => a.AccountNumber == accountNumber);
    }

    public static async Task<long> BalanceAsync(HttpClient client, Guid accountId) =>
        (await client.GetFromJsonAsync<AccountBalanceDto>($"/api/accounts/{accountId}/balance", Json))!.AvailableBalanceMinorUnits;

    /// <summary>Creates a customer and a funded account through the admin API.</summary>
    public static async Task<AccountDto> NewFundedAccountAsync(HttpClient admin, long balanceMinorUnits)
    {
        var customerResponse = await admin.PostAsJsonAsync("/api/admin/customers",
            new { fullName = "Integration Customer", email = $"{Guid.NewGuid():N}@test.example" });
        Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
        var customer = (await customerResponse.Content.ReadFromJsonAsync<CustomerDto>(Json))!;

        var accountResponse = await admin.PostAsJsonAsync("/api/admin/accounts",
            new { customerId = customer.Id, name = "Integration account", currency = "IDR" });
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        var account = (await accountResponse.Content.ReadFromJsonAsync<AccountDto>(Json))!;

        if (balanceMinorUnits > 0)
        {
            using var deposit = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/accounts/{account.Id}/deposits")
            {
                Content = JsonContent.Create(new { amountMinorUnits = balanceMinorUnits }),
            };
            deposit.Headers.Add("Idempotency-Key", "deposit-" + Guid.NewGuid().ToString("N"));
            var depositResponse = await admin.SendAsync(deposit);
            Assert.Equal(HttpStatusCode.Created, depositResponse.StatusCode);
        }
        return account;
    }

    public static async Task<HttpResponseMessage> TransferAsync(HttpClient client, Guid from, Guid to, long amount,
        string? key, string currency = "IDR")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new { sourceAccountId = from, destinationAccountId = to, amountMinorUnits = amount, currency }),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    public static string NewKey() => "it-" + Guid.NewGuid().ToString("N");

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
