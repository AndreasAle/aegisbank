using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AegisBank.Api.Security;
using AegisBank.Infrastructure;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------ logging
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
else
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

// ------------------------------------------------------------------ services
builder.Services.AddAegisInfrastructure(builder.Configuration);

builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= 32,
        "Jwt:SigningKey must be at least 32 bytes. Set the Jwt__SigningKey environment variable.")
    .ValidateOnStart();
builder.Services.AddSingleton<JwtTokenFactory>();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Deferred so that a missing key fails options validation with a clear message first.
            IssuerSigningKeyResolver = (_, _, _, _) => [jwt.CreateKey()],
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AegisClaims.Username,
            RoleClaimType = AegisClaims.Role,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.RequireRole("Admin", "Auditor"))
    .AddPolicy(Policies.Admin, p => p.RequireRole("Admin"));

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = Problems.Decorate);
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model-binding and validation failures use the same problem shape as everything else.
        o.InvalidModelStateResponseFactory = context =>
        {
            var problem = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = "One or more fields are invalid.",
                Type = "https://aegisbank.example/problems/validation",
            };
            problem.Extensions["code"] = "validation_failed";
            return Problems.Result(context.HttpContext, problem);
        };
    });

// Rate limiting: a general per-caller budget and a much smaller one for credential endpoints.
var rateLimitingEnabled = builder.Configuration.GetValue("RateLimiting:Enabled", true);
var generalPerMinute = builder.Configuration.GetValue("RateLimiting:PermitPerMinute", 600);
var authPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = Problems.Create(429, "rate_limited", "Too many requests. Slow down and retry."),
        });
    };

    static string CallerKey(HttpContext http) =>
        http.User.FindFirst(AegisClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        !rateLimitingEnabled || http.Request.Path.StartsWithSegments("/health")
            ? RateLimitPartition.GetNoLimiter("unlimited")
            : RateLimitPartition.GetTokenBucketLimiter(CallerKey(http), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = generalPerMinute,
                TokensPerPeriod = Math.Max(1, generalPerMinute / 6),
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    o.AddPolicy("auth", http => !rateLimitingEnabled
        ? RateLimitPartition.GetNoLimiter("unlimited")
        : RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", builder.Environment.IsDevelopment());
if (openApiEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "AegisBank API",
            Version = "v1",
            Description = "Simulated banking transaction engine. Educational project: no real money, no real banking network.",
        });
        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access token from POST /api/auth/login.",
        });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
        });
        var xml = Path.Combine(AppContext.BaseDirectory, "AegisBank.Api.xml");
        if (File.Exists(xml)) o.IncludeXmlComments(xml);
    });
}

var app = builder.Build();

// ------------------------------------------------------------------ one-shot commands
// `dotnet AegisBank.Api.dll migrate` and `... seed` run and exit without starting the server.
if (args.Contains("migrate") || args.Contains("seed"))
{
    builder.Configuration["Database:MigrateOnStartup"] = "true";
    if (args.Contains("seed")) builder.Configuration["Seed:Enabled"] = "true";
    await app.Services.InitializeAegisDatabaseAsync();
    return;
}

await app.Services.InitializeAegisDatabaseAsync();

// ------------------------------------------------------------------ pipeline
app.UseForwardedHeaders();
app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAegisSecurityHeaders(openApiEnabled
    ? "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
    : "default-src 'none'; frame-ancestors 'none'");

if (app.Configuration.GetValue("Security:RequireHttps", !app.Environment.IsDevelopment()))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (openApiEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "AegisBank API");
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// Liveness: the process answers. Readiness: database reachable and native engine loaded.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponse.WriteAsync,
}).AllowAnonymous();

app.Run();

public partial class Program;
