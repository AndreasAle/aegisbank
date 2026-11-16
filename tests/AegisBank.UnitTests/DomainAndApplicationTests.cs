using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;

namespace AegisBank.UnitTests;

public sealed class RequestFingerprintTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static string Fp(TransferKind kind = TransferKind.Internal, Guid? actor = null, Guid? from = null, Guid? to = null,
        long amount = 1000, string currency = "IDR", Guid? reversalOf = null) =>
        RequestFingerprint.Compute(kind, actor ?? Actor, from ?? A, to ?? B, amount, currency, reversalOf);

    [Fact]
    public void SameInputProducesSameFingerprint() => Assert.Equal(Fp(), Fp());

    [Fact]
    public void FingerprintIsLowercaseSha256Hex()
    {
        var fp = Fp();
        Assert.Equal(64, fp.Length);
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void CurrencyCasingAndWhitespaceDoNotMatter() => Assert.Equal(Fp(currency: "IDR"), Fp(currency: " idr "));

    [Fact]
    public void EveryFieldContributes()
    {
        var baseline = Fp();
        Assert.NotEqual(baseline, Fp(amount: 1001));
        Assert.NotEqual(baseline, Fp(from: B, to: A));
        Assert.NotEqual(baseline, Fp(actor: Guid.NewGuid()));
        Assert.NotEqual(baseline, Fp(kind: TransferKind.OpeningDeposit));
        Assert.NotEqual(baseline, Fp(currency: "USD"));
        Assert.NotEqual(baseline, Fp(reversalOf: Guid.NewGuid()));
    }

    [Fact]
    public void FieldsCannotBleedIntoEachOther()
    {
        // 1|23 must differ from 12|3: the canonical form uses fixed-width ids and a separator.
        Assert.NotEqual(Fp(amount: 12), Fp(amount: 123));
        Assert.NotEqual(Fp(amount: 1, currency: "IDR"), Fp(amount: 11, currency: "IDR"));
    }
}

public sealed class IdempotencyKeyTests
{
    [Theory]
    [InlineData("12345678")]
    [InlineData("order-2026-11-16_0001")]
    [InlineData("d9428888-122b-11e1-b85c-61cd3cbb3210")]
    public void AcceptsReasonableKeys(string key) => Assert.True(IdempotencyKeys.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space in it")]
    [InlineData("tab\tcharacter")]
    [InlineData("naïve-unicode")]
    [InlineData("line\nbreak-key")]
    public void RejectsUnsafeKeys(string? key) => Assert.False(IdempotencyKeys.IsValid(key));

    [Fact]
    public void RejectsKeysLongerThanTheLimit()
    {
        Assert.True(IdempotencyKeys.IsValid(new string('k', IdempotencyKeys.MaxLength)));
        Assert.False(IdempotencyKeys.IsValid(new string('k', IdempotencyKeys.MaxLength + 1)));
    }
}

public sealed class CurrencyTests
{
    [Theory]
    [InlineData(0, "Rp 0,00")]
    [InlineData(1, "Rp 0,01")]
    [InlineData(10_000_000, "Rp 100.000,00")]
    [InlineData(123_456_789_012, "Rp 1.234.567.890,12")]
    public void FormatsMinorUnitsInIndonesianStyle(long minor, string expected) =>
        Assert.Equal(expected, Currencies.Idr.Format(minor));

    [Theory]
    [InlineData("150000", 15_000_000)]
    [InlineData("150000.5", 15_000_050)]
    [InlineData("0.01", 1)]
    [InlineData(" 25 ", 2_500)]
    public void ParsesMajorUnitsExactly(string text, long expected)
    {
        Assert.True(Currencies.Idr.TryParseMajor(text, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.005")]            // more precision than the currency has
    [InlineData("1,000")]            // thousands separators are not accepted
    [InlineData("1e5")]
    [InlineData("99999999999999999999999")]
    public void RejectsInvalidAmounts(string text) => Assert.False(Currencies.Idr.TryParseMajor(text, out _));

    [Fact]
    public void OnlyIdrIsSupported()
    {
        Assert.True(Currencies.TryGet("IDR", out var idr));
        Assert.Equal(360u, idr.NumericCode);
        Assert.False(Currencies.TryGet("USD", out _));
        Assert.False(Currencies.TryGet(null, out _));
        Assert.Throws<ArgumentException>(() => Currencies.Get("EUR"));
    }
}

public sealed class AccountingConventionTests
{
    [Fact]
    public void AssetsAreDebitNormalAndLiabilitiesAreCreditNormal()
    {
        Assert.Equal(NormalSide.Debit, new Account { Class = AccountClass.Asset }.NormalSide);
        Assert.Equal(NormalSide.Credit, new Account { Class = AccountClass.Liability }.NormalSide);
    }

    [Fact]
    public void EngineSnapshotCarriesTheAccountsNormalSideAndCurrencyCode()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(), Class = AccountClass.Liability, Currency = "IDR",
            AvailableBalanceMinorUnits = 42, Status = AccountStatus.Frozen,
        };
        var snapshot = EngineAccount.From(account);
        Assert.Equal(account.Id, snapshot.Id);
        Assert.Equal(42, snapshot.BalanceMinorUnits);
        Assert.Equal(360u, snapshot.CurrencyNumeric);
        Assert.Equal(AccountStatus.Frozen, snapshot.Status);
        Assert.Equal(NormalSide.Credit, snapshot.NormalSide);
    }
}

public sealed class ActorPermissionTests
{
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Account Own = new() { Id = Guid.NewGuid(), CustomerId = CustomerId, Class = AccountClass.Liability };
    private static readonly Account Other = new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Class = AccountClass.Liability };
    private static readonly Account Vault = new() { Id = Guid.NewGuid(), CustomerId = null, Class = AccountClass.Asset };

    private static Actor ActorOf(UserRole role, Guid? customer = null) => new(Guid.NewGuid(), "u", role, customer);

    [Fact]
    public void CustomerCanViewAndDebitOnlyOwnAccounts()
    {
        var customer = ActorOf(UserRole.Customer, CustomerId);
        Assert.True(customer.CanView(Own));
        Assert.True(customer.CanDebit(Own));
        Assert.False(customer.CanView(Other));
        Assert.False(customer.CanDebit(Other));
        Assert.False(customer.CanView(Vault));
        Assert.False(customer.CanDebit(Vault));
    }

    [Fact]
    public void CustomerWithoutCustomerLinkSeesNothing()
    {
        var orphan = ActorOf(UserRole.Customer);
        Assert.False(orphan.CanView(Own));
        Assert.False(orphan.CanDebit(Own));
        Assert.False(orphan.CanView(Vault)); // null == null must not grant access
    }

    [Fact]
    public void AuditorCanViewEverythingButMoveNothing()
    {
        var auditor = ActorOf(UserRole.Auditor);
        Assert.True(auditor.CanView(Own));
        Assert.True(auditor.CanView(Vault));
        Assert.False(auditor.CanDebit(Own));
    }

    [Fact]
    public void AdminCanDebitCustomerAccountsButNeverSystemAccounts()
    {
        var admin = ActorOf(UserRole.Admin);
        Assert.True(admin.CanDebit(Own));
        Assert.True(admin.CanDebit(Other));
        Assert.False(admin.CanDebit(Vault));
    }
}

public sealed class PagingAndSortingTests
{
    [Theory]
    [InlineData(0, 0, 1, 1, 0)]
    [InlineData(-3, 5000, 1, 200, 0)]
    [InlineData(3, 25, 3, 25, 50)]
    public void PageRequestIsClampedToSafeValues(int page, int size, int safePage, int safeSize, int skip)
    {
        var request = new PageRequest(page, size);
        Assert.Equal(safePage, request.SafePage);
        Assert.Equal(safeSize, request.SafePageSize);
        Assert.Equal(skip, request.Skip);
    }

    [Fact]
    public void TotalPagesRoundsUp()
    {
        Assert.Equal(0, new PagedResult<int>([], 1, 25, 0).TotalPages);
        Assert.Equal(1, new PagedResult<int>([], 1, 25, 25).TotalPages);
        Assert.Equal(2, new PagedResult<int>([], 1, 25, 26).TotalPages);
    }

    [Fact]
    public void SortSpecParsesWhitelistedFields()
    {
        string[] allowed = ["createdAt", "amount"];
        Assert.Equal(new SortSpec("createdAt", true), SortSpec.Parse(null, allowed, "createdAt", true));
        Assert.Equal(new SortSpec("amount", false), SortSpec.Parse("AMOUNT", allowed, "createdAt", true));
        Assert.Equal(new SortSpec("amount", true), SortSpec.Parse("amount:desc", allowed, "createdAt", true));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("amount:sideways")]
    [InlineData("amount; DROP TABLE accounts")]
    public void SortSpecRejectsAnythingElse(string value)
    {
        var ex = Assert.Throws<AppException>(() => SortSpec.Parse(value, ["createdAt", "amount"], "createdAt", true));
        Assert.Equal(ErrorKind.Validation, ex.Kind);
    }
}

public sealed class NamingConventionTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("CustomerId", "customer_id")]
    [InlineData("AvailableBalanceMinorUnits", "available_balance_minor_units")]
    [InlineData("IsActive", "is_active")]
    [InlineData("PostingTxId", "posting_tx_id")]
    public void ColumnNamesAreSnakeCase(string property, string column) =>
        Assert.Equal(column, AegisDbContext.ToSnakeCase(property));
}
