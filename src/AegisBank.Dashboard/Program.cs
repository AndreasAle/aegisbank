using System.Security.Claims;
using System.Threading.RateLimiting;
using AegisBank.Application;
using AegisBank.Dashboard;
using AegisBank.Infrastructure;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
else
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

builder.Services.AddAegisInfrastructure(builder.Configuration);
builder.Services.Configure<BenchmarkOptions>(builder.Configuration.GetSection("Benchmarks"));
builder.Services.AddSingleton<BenchmarkResultsReader>();

var requireHttps = builder.Configuration.GetValue("Security:RequireHttps", !builder.Environment.IsDevelopment());

// Cookie keys must survive restarts and be shared by replicas, otherwise sessions break.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("AegisBank.Dashboard");
if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "aegis.console";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        o.SlidingExpiration = true;
        // A disabled user or a changed role takes effect on the next request, not at cookie expiry.
        o.Events.OnValidatePrincipal = async context =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
            var valid = Guid.TryParse(context.Principal?.FindFirstValue(AegisClaims.UserId), out var userId)
                        && await auth.FindActiveUserAsync(userId, context.HttpContext.RequestAborted) is { } user
                        && user.Role == context.Principal!.FindFirstValue(AegisClaims.Role);
            if (!valid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.RequireRole("Admin", "Auditor"))
    .AddPolicy(Policies.Admin, p => p.RequireRole("Admin"));

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "aegis.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

// The console is for staff only: every page requires the Staff policy unless it opts out.
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", Policies.Staff);
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
});

var loginPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => HttpMethods.IsPost(http.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("get"));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

await app.Services.InitializeAegisDatabaseAsync();

app.UseForwardedHeaders();
app.UseCorrelationId();
app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?status={0}");
app.UseAegisSecurityHeaders(
    "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'");

if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapRazorPages();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

public partial class Program;
