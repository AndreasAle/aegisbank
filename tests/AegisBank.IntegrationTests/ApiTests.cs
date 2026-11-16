using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AegisBank.Api.Controllers;
using AegisBank.Application;

namespace AegisBank.IntegrationTests;

public sealed class AuthenticationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task HealthEndpointsAreAnonymousAndReady()
    {
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "native-engine");
    }

    [Fact]
    public async Task OpenApiDocumentIsServed()
    {
        using var client = api.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/transfers", out _));
    }

    [Fact]
    public async Task LoginReturnsTokensAndMeDescribesTheUser()
    {
        var tokens = await api.LoginAsync("ops.admin", ApiFixture.AdminPassword);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.InRange(tokens.ExpiresInSeconds, 1, 15 * 60);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));

        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me", ApiFixture.Json);
        Assert.Equal("ops.admin", me!.Username);
        Assert.Equal("Admin", me.Role);
    }

    [Theory]
    [InlineData("ops.admin", "wrong-password")]
    [InlineData("no.such.user", "whatever-password")]
    [InlineData("system", "!")]
    public async Task FailedLoginsAllLookTheSame(string username, string password)
    {
        using var client = api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ApiFixture.ProblemAsync(response);
        Assert.Equal("invalid_credentials", problem.GetProperty("code").GetString());
        Assert.Equal("Invalid credentials.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RepeatedFailuresLockTheAccount()
    {
        using var client = api.CreateClient();
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/api/auth/login", new { username = "sari", password = "bad-" + i });

        // The correct password is now refused as well, with the same generic answer.
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "sari", password = ApiFixture.CustomerPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefreshRotatesTheTokenAndReuseRevokesTheFamily()
    {
        var first = await api.LoginAsync("bima", ApiFixture.CustomerPassword);
        using var client = api.CreateClient();

        var rotated = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<TokenResponse>(ApiFixture.Json))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Presenting the already-used token is treated as theft...
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        // ...and the token that descended from it stops working too.
        var descendant = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesTheRefreshToken()
    {
        var tokens = await api.LoginAsync("ratna", ApiFixture.CustomerPassword);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Theory]
    [InlineData("/api/accounts")]
    [InlineData("/api/transfers")]
    [InlineData("/api/ledger")]
    [InlineData("/api/admin/overview")]
    [InlineData("/api/auth/me")]
    public async Task ProtectedEndpointsRequireAToken(string path)
    {
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ErrorsCarryACorrelationIdAndNoInternals()
    {
        using var client = await api.AdminAsync();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "it-correlation-0001");
        var response = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("it-correlation-0001", response.Headers.GetValues("X-Correlation-ID").Single());
        var raw = await response.Content.ReadAsStringAsync();
        var problem = await ApiFixture.ProblemAsync(response);
        Assert.Equal("it-correlation-0001", problem.GetProperty("correlationId").GetString());
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("Exception", raw);
        Assert.DoesNotContain("Npgsql", raw);
        Assert.DoesNotContain("   at ", raw);
    }
}

public sealed class AuthorizationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task CustomerSeesOnlyOwnAccounts()
    {
        using var ratna = await api.RatnaAsync();
        var page = await ratna.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?pageSize=200&includeSystem=true", ApiFixture.Json);

        Assert.Equal(2, page!.TotalCount);
        Assert.All(page.Items, a => Assert.Equal("Ratna Kusumawardani", a.CustomerName));
    }

    [Fact]
    public async Task CustomerCannotReadAnotherCustomersAccountOrItsHistory()
    {
        using var admin = await api.AdminAsync();
        var bimaAccount = await ApiFixture.AccountByNumberAsync(admin, ApiFixture.Bima1);
        using var ratna = await api.RatnaAsync();

        // Reported as missing, so the response does not confirm that the account exists.
        Assert.Equal(HttpStatusCode.NotFound, (await ratna.GetAsync($"/api/accounts/{bimaAccount.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ratna.GetAsync($"/api/accounts/{bimaAccount.Id}/balance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ratna.GetAsync($"/api/accounts/{bimaAccount.Id}/transactions")).StatusCode);
    }

    [Fact]
    public async Task CustomerCannotMoveMoneyOutOfAnotherCustomersAccount()
    {
        using var admin = await api.AdminAsync();
        var bimaAccount = await ApiFixture.AccountByNumberAsync(admin, ApiFixture.Bima1);
        var before = bimaAccount.AvailableBalanceMinorUnits;
        using var ratna = await api.RatnaAsync();
        var ratnaAccount = await ApiFixture.AccountByNumberAsync(ratna, ApiFixture.Ratna1);

        var response = await ApiFixture.TransferAsync(ratna, bimaAccount.Id, ratnaAccount.Id, 1_000, ApiFixture.NewKey());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await ApiFixture.BalanceAsync(admin, bimaAccount.Id));
    }

    [Fact]
    public async Task CustomerSeesOnlyTransfersInvolvingOwnAccounts()
    {
        using var admin = await api.AdminAsync();
        var foreign = await ApiFixture.NewFundedAccountAsync(admin, 50_000);
        var foreign2 = await ApiFixture.NewFundedAccountAsync(admin, 0);
        var created = await ApiFixture.TransferAsync(admin, foreign.Id, foreign2.Id, 1_000, ApiFixture.NewKey());
        var transfer = (await created.Content.ReadFromJsonAsync<TransferDto>(ApiFixture.Json))!;

        using var ratna = await api.RatnaAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ratna.GetAsync($"/api/transfers/{transfer.Id}")).StatusCode);
        var mine = await ratna.GetFromJsonAsync<PagedResult<TransferDto>>("/api/transfers?pageSize=200", ApiFixture.Json);
        Assert.NotEmpty(mine!.Items);
        Assert.All(mine.Items, t => Assert.True(
            t.SourceAccountNumber is ApiFixture.Ratna1 or ApiFixture.Ratna2 ||
            t.DestinationAccountNumber is ApiFixture.Ratna1 or ApiFixture.Ratna2));
    }

    [Theory]
    [InlineData("/api/admin/overview")]
    [InlineData("/api/admin/transactions")]
    [InlineData("/api/admin/audit-logs")]
    [InlineData("/api/admin/system-health")]
    [InlineData("/api/ledger")]
    [InlineData("/api/ledger/verify")]
    public async Task StaffEndpointsAreForbiddenToCustomersAndOpenToAuditors(string path)
    {
        using var ratna = await api.RatnaAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await ratna.GetAsync(path)).StatusCode);

        using var auditor = await api.AuditorAsync();
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AuditorIsReadOnly()
    {
        using var auditor = await api.AuditorAsync();
        var a = await ApiFixture.AccountByNumberAsync(auditor, ApiFixture.Ratna1);
        var b = await ApiFixture.AccountByNumberAsync(auditor, ApiFixture.Bima1);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await auditor.PostAsJsonAsync("/api/admin/customers", new { fullName = "Nope Nope", email = "nope@test.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await auditor.PutAsJsonAsync($"/api/admin/accounts/{a.Id}/status", new { status = "Frozen" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ApiFixture.TransferAsync(auditor, a.Id, b.Id, 1_000, ApiFixture.NewKey())).StatusCode);
        Assert.Equal(a.AvailableBalanceMinorUnits, await ApiFixture.BalanceAsync(auditor, a.Id));
    }

    [Fact]
    public async Task DeniedAndFailedAttemptsAreAudited()
    {
        using var client = api.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { username = "ops.admin", password = "definitely-wrong" });

        using var admin = await api.AdminAsync();
        var logs = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>("/api/admin/audit-logs?action=auth.login.failed", ApiFixture.Json);
        Assert.NotEmpty(logs!.Items);
        Assert.All(logs.Items, l => Assert.DoesNotContain("definitely-wrong", l.Metadata));
    }
}

public sealed class TransferApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task CustomerTransferIsPostedAsABalancedJournal()
    {
        using var ratna = await api.RatnaAsync();
        using var admin = await api.AdminAsync();
        var source = await ApiFixture.AccountByNumberAsync(ratna, ApiFixture.Ratna2);
        var destination = await ApiFixture.AccountByNumberAsync(admin, ApiFixture.Bima1);

        var response = await ApiFixture.TransferAsync(ratna, source.Id, destination.Id, 125_000, ApiFixture.NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues("Idempotent-Replayed").Single());
        var transfer = (await response.Content.ReadFromJsonAsync<TransferDto>(ApiFixture.Json))!;
        Assert.Equal("Completed", transfer.Status);
        Assert.StartsWith("TRF-", transfer.ReferenceNumber);
        Assert.EndsWith($"/api/transfers/{transfer.Id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(source.AvailableBalanceMinorUnits - 125_000, await ApiFixture.BalanceAsync(admin, source.Id));
        Assert.Equal(destination.AvailableBalanceMinorUnits + 125_000, await ApiFixture.BalanceAsync(admin, destination.Id));

        var journal = await admin.GetFromJsonAsync<JournalEntryDto>($"/api/ledger/{transfer.JournalEntryId}", ApiFixture.Json);
        Assert.True(journal!.IsBalanced);
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal((125_000, 0), (journal.Lines[0].DebitMinorUnits, journal.Lines[0].CreditMinorUnits));
        Assert.Equal(source.AccountNumber, journal.Lines[0].AccountNumber);
        Assert.Equal((0, 125_000), (journal.Lines[1].DebitMinorUnits, journal.Lines[1].CreditMinorUnits));
        Assert.Equal(destination.AccountNumber, journal.Lines[1].AccountNumber);

        // The posting shows up in the account history of the owner.
        var history = await ratna.GetFromJsonAsync<PagedResult<AccountTransactionDto>>($"/api/accounts/{source.Id}/transactions", ApiFixture.Json);
        var posting = Assert.Single(history!.Items, h => h.TransferId == transfer.Id);
        Assert.Equal("Debit", posting.Direction);
        Assert.Equal(destination.AccountNumber, posting.CounterpartyAccountNumber);
    }

    [Fact]
    public async Task SameKeyAndBodyReturnsTheOriginalResult_DifferentBodyConflicts()
    {
        using var admin = await api.AdminAsync();
        var source = await ApiFixture.NewFundedAccountAsync(admin, 1_000_000);
        var destination = await ApiFixture.NewFundedAccountAsync(admin, 0);
        var key = ApiFixture.NewKey();

        var first = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 300_000, key);
        var replay = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 300_000, key);
        var conflict = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 300_001, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_key_conflict", (await ApiFixture.ProblemAsync(conflict)).GetProperty("code").GetString());

        Assert.Equal(700_000, await ApiFixture.BalanceAsync(admin, source.Id));
        Assert.Equal(300_000, await ApiFixture.BalanceAsync(admin, destination.Id));
    }

    [Fact]
    public async Task IdempotencyKeysAreScopedPerUser()
    {
        using var admin = await api.AdminAsync();
        using var ratna = await api.RatnaAsync();
        var ratnaAccount = await ApiFixture.AccountByNumberAsync(ratna, ApiFixture.Ratna1);
        var other = await ApiFixture.NewFundedAccountAsync(admin, 10_000);
        var key = ApiFixture.NewKey();

        // Two users may pick the same key without colliding.
        var byRatna = await ApiFixture.TransferAsync(ratna, ratnaAccount.Id, other.Id, 1_000, key);
        var byAdmin = await ApiFixture.TransferAsync(admin, other.Id, ratnaAccount.Id, 2_000, key);

        Assert.Equal(HttpStatusCode.Created, byRatna.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byAdmin.StatusCode);
    }

    [Fact]
    public async Task InsufficientFundsIsRejectedRecordedAndLeavesBalancesUntouched()
    {
        using var admin = await api.AdminAsync();
        var source = await ApiFixture.NewFundedAccountAsync(admin, 10_000);
        var destination = await ApiFixture.NewFundedAccountAsync(admin, 0);
        var key = ApiFixture.NewKey();

        var response = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 10_001, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ApiFixture.ProblemAsync(response);
        Assert.Equal("insufficient_funds", problem.GetProperty("code").GetString());
        var recorded = problem.GetProperty("transfer");
        Assert.Equal("Rejected", recorded.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("journalEntryId").ValueKind);

        Assert.Equal(10_000, await ApiFixture.BalanceAsync(admin, source.Id));
        Assert.Equal(0, await ApiFixture.BalanceAsync(admin, destination.Id));

        var replay = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 10_001, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        var listed = await admin.GetFromJsonAsync<PagedResult<TransferDto>>(
            $"/api/admin/transactions?status=Rejected&accountId={source.Id}", ApiFixture.Json);
        Assert.Single(listed!.Items);
    }

    [Fact]
    public async Task FrozenAccountCannotSendOrReceive_ClosedRequiresZeroBalance()
    {
        using var admin = await api.AdminAsync();
        var frozen = await ApiFixture.NewFundedAccountAsync(admin, 10_000);
        var other = await ApiFixture.NewFundedAccountAsync(admin, 10_000);

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/admin/accounts/{frozen.Id}/status", new { status = "Frozen" })).StatusCode);

        var outgoing = await ApiFixture.TransferAsync(admin, frozen.Id, other.Id, 100, ApiFixture.NewKey());
        Assert.Equal("debit_account_not_active", (await ApiFixture.ProblemAsync(outgoing)).GetProperty("code").GetString());
        var incoming = await ApiFixture.TransferAsync(admin, other.Id, frozen.Id, 100, ApiFixture.NewKey());
        Assert.Equal("credit_account_not_active", (await ApiFixture.ProblemAsync(incoming)).GetProperty("code").GetString());

        var close = await admin.PutAsJsonAsync($"/api/admin/accounts/{frozen.Id}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.Conflict, close.StatusCode);
        Assert.Equal(10_000, await ApiFixture.BalanceAsync(admin, frozen.Id));
    }

    [Fact]
    public async Task ReversalPostsACompensatingEntryExactlyOnce()
    {
        using var admin = await api.AdminAsync();
        var source = await ApiFixture.NewFundedAccountAsync(admin, 500_000);
        var destination = await ApiFixture.NewFundedAccountAsync(admin, 0);
        var created = await ApiFixture.TransferAsync(admin, source.Id, destination.Id, 200_000, ApiFixture.NewKey());
        var original = (await created.Content.ReadFromJsonAsync<TransferDto>(ApiFixture.Json))!;

        async Task<HttpResponseMessage> ReverseAsync(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/transfers/{original.Id}/reversal");
            request.Headers.Add("Idempotency-Key", key);
            return await admin.SendAsync(request);
        }

        var reversalResponse = await ReverseAsync(ApiFixture.NewKey());
        Assert.Equal(HttpStatusCode.Created, reversalResponse.StatusCode);
        var reversal = (await reversalResponse.Content.ReadFromJsonAsync<TransferDto>(ApiFixture.Json))!;
        Assert.Equal("Reversal", reversal.Kind);
        Assert.Equal(original.Id, reversal.ReversalOfTransferId);
        Assert.Equal(original.DestinationAccountId, reversal.SourceAccountId);

        Assert.Equal(500_000, await ApiFixture.BalanceAsync(admin, source.Id));
        Assert.Equal(0, await ApiFixture.BalanceAsync(admin, destination.Id));

        // The original is untouched and still has its own journal.
        var unchanged = await admin.GetFromJsonAsync<TransferDto>($"/api/transfers/{original.Id}", ApiFixture.Json);
        Assert.Equal(original, unchanged);

        // A second reversal is refused; a reversal cannot be reversed.
        Assert.NotEqual(HttpStatusCode.Created, (await ReverseAsync(ApiFixture.NewKey())).StatusCode);
        using var reverseReversal = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/transfers/{reversal.Id}/reversal");
        reverseReversal.Headers.Add("Idempotency-Key", ApiFixture.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(reverseReversal)).StatusCode);
    }

    [Fact]
    public async Task LedgerVerificationPassesAfterAllOfTheAbove()
    {
        using var auditor = await api.AuditorAsync();
        var response = await auditor.GetAsync("/api/ledger/verify");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<LedgerVerificationReport>(ApiFixture.Json))!;
        Assert.True(report.IsConsistent, string.Join("; ", report.Checks.Where(c => !c.Passed).Select(c => c.Name)));
        Assert.Equal(report.TotalDebitsMinorUnits, report.TotalCreditsMinorUnits);
        Assert.Equal(report.TotalAssetBalanceMinorUnits, report.TotalLiabilityBalanceMinorUnits);
        Assert.True(report.JournalCount > 0);
    }

    [Fact]
    public async Task OverviewAndSystemHealthReportRealData()
    {
        using var admin = await api.AdminAsync();
        var overview = await admin.GetFromJsonAsync<OverviewDto>("/api/admin/overview", ApiFixture.Json);
        Assert.True(overview!.CustomerAccountCount >= 9);
        Assert.True(overview.RejectedTransferCount >= 2); // the seed contains two rejected transfers
        Assert.Equal(overview.TotalCustomerBalanceMinorUnits, overview.VaultCashBalanceMinorUnits);
        Assert.Equal(14, overview.DailyVolume.Count);
        Assert.Equal(overview.TransferCount, overview.DailyVolume.Sum(d => d.CompletedCount + d.RejectedCount));

        var health = await admin.GetFromJsonAsync<SystemHealthDto>("/api/admin/system-health", ApiFixture.Json);
        Assert.True(health!.Healthy);
        Assert.Contains(health.Components, c => c.Name == "Native transaction engine" && c.Healthy);
        Assert.Equal(0, health.PendingMigrations);
    }
}

public sealed class ValidationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private async Task<(HttpClient Admin, Guid A, Guid B)> ArrangeAsync()
    {
        var admin = await api.AdminAsync();
        var a = await ApiFixture.AccountByNumberAsync(admin, ApiFixture.Ratna1);
        var b = await ApiFixture.AccountByNumberAsync(admin, ApiFixture.Bima1);
        return (admin, a.Id, b.Id);
    }

    [Fact]
    public async Task IdempotencyKeyIsRequired()
    {
        var (admin, a, b) = await ArrangeAsync();
        var response = await ApiFixture.TransferAsync(admin, a, b, 1_000, key: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await ApiFixture.ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space in key")]
    public async Task MalformedIdempotencyKeyIsRejected(string key)
    {
        var (admin, a, b) = await ArrangeAsync();
        var response = await ApiFixture.TransferAsync(admin, a, b, 1_000, key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task NonPositiveAmountIsRejectedBeforeAnythingIsRecorded(long amount)
    {
        var (admin, a, b) = await ArrangeAsync();
        var key = ApiFixture.NewKey();
        var response = await ApiFixture.TransferAsync(admin, a, b, amount, key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The key was not consumed by the invalid request.
        var valid = await ApiFixture.TransferAsync(admin, a, b, 1_000, key);
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }

    [Fact]
    public async Task SameAccountUnsupportedCurrencyAndUnknownAccountAreRejected()
    {
        var (admin, a, b) = await ArrangeAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiFixture.TransferAsync(admin, a, a, 1_000, ApiFixture.NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiFixture.TransferAsync(admin, a, b, 1_000, ApiFixture.NewKey(), "USD")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiFixture.TransferAsync(admin, a, b, 1_000, ApiFixture.NewKey(), "RUPIAH")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiFixture.TransferAsync(admin, a, Guid.NewGuid(), 1_000, ApiFixture.NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiFixture.TransferAsync(admin, Guid.NewGuid(), b, 1_000, ApiFixture.NewKey())).StatusCode);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"sourceAccountId\":\"abc\",\"destinationAccountId\":\"def\",\"amountMinorUnits\":1,\"currency\":\"IDR\"}")]
    [InlineData("{\"sourceAccountId\":\"00000000-0000-0000-0000-000000000001\",\"destinationAccountId\":\"00000000-0000-0000-0000-000000000002\",\"amountMinorUnits\":1.5,\"currency\":\"IDR\"}")]
    [InlineData("{\"sourceAccountId\":\"00000000-0000-0000-0000-000000000001\",\"destinationAccountId\":\"00000000-0000-0000-0000-000000000002\",\"amountMinorUnits\":99999999999999999999,\"currency\":\"IDR\"}")]
    public async Task MalformedBodiesProduceProblemDetails(string body)
    {
        using var admin = await api.AdminAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", ApiFixture.NewKey());
        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiFixture.ProblemAsync(response);
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData("/api/accounts?pageSize=0")]
    [InlineData("/api/accounts?pageSize=1000")]
    [InlineData("/api/accounts?page=0")]
    [InlineData("/api/accounts?sort=passwordHash")]
    [InlineData("/api/accounts?status=Sleeping")]
    [InlineData("/api/transfers?sort=amount:upwards")]
    [InlineData("/api/transfers?kind=Wire")]
    [InlineData("/api/admin/overview?days=1000")]
    public async Task InvalidQueryParametersAreRejected(string path)
    {
        using var admin = await api.AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task PagingSortingAndFilteringWork()
    {
        using var admin = await api.AdminAsync();

        var firstPage = await admin.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?pageSize=3&sort=balance:desc", ApiFixture.Json);
        Assert.Equal(3, firstPage!.Items.Count);
        Assert.True(firstPage.TotalCount >= 9);
        Assert.Equal(firstPage.Items.OrderByDescending(a => a.AvailableBalanceMinorUnits).Select(a => a.Id), firstPage.Items.Select(a => a.Id));

        var secondPage = await admin.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?pageSize=3&page=2&sort=balance:desc", ApiFixture.Json);
        Assert.Empty(firstPage.Items.Select(a => a.Id).Intersect(secondPage!.Items.Select(a => a.Id)));
        Assert.True(firstPage.Items[^1].AvailableBalanceMinorUnits >= secondPage.Items[0].AvailableBalanceMinorUnits);

        var search = await admin.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?search=wulandari", ApiFixture.Json);
        Assert.Equal(2, search!.TotalCount);

        // LIKE wildcards in user input are literal characters, not patterns.
        var wildcard = await admin.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?search=%25", ApiFixture.Json);
        Assert.Equal(0, wildcard!.TotalCount);
        var injection = await admin.GetFromJsonAsync<PagedResult<AccountDto>>("/api/accounts?search=' OR 1=1 --", ApiFixture.Json);
        Assert.Equal(0, injection!.TotalCount);

        var deposits = await admin.GetFromJsonAsync<PagedResult<TransferDto>>("/api/admin/transactions?kind=OpeningDeposit&sort=amount:asc&pageSize=200", ApiFixture.Json);
        Assert.NotEmpty(deposits!.Items);
        Assert.All(deposits.Items, t => Assert.Equal("OpeningDeposit", t.Kind));
        Assert.Equal(deposits.Items.OrderBy(t => t.AmountMinorUnits).Select(t => t.Id), deposits.Items.Select(t => t.Id));
    }
}

/// <summary>Separate host with a tiny login budget to observe the rate limiter.</summary>
public sealed class RateLimitedApiFixture : ApiFixture
{
    protected override Dictionary<string, string> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["RateLimiting:Enabled"] = "true";
            settings["RateLimiting:AuthPermitPerMinute"] = "3";
            return settings;
        }
    }
}

public sealed class RateLimitingTests(RateLimitedApiFixture api) : IClassFixture<RateLimitedApiFixture>
{
    [Fact]
    public async Task LoginAttemptsBeyondTheBudgetGet429()
    {
        using var client = api.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { username = "ops.admin", password = "x" + i })).StatusCode);

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
