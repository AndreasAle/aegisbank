using System.Security.Claims;
using AegisBank.Application;
using AegisBank.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AegisBank.Infrastructure.Web;

/// <summary>Claim names shared by the JWT (API) and cookie (dashboard) principals.</summary>
public static class AegisClaims
{
    public const string UserId = "sub";
    public const string Username = "name";
    public const string Role = "role";
    public const string CustomerId = "customer_id";

    public static IEnumerable<Claim> For(AuthenticatedUser user)
    {
        yield return new Claim(UserId, user.Id.ToString());
        yield return new Claim(Username, user.Username);
        yield return new Claim(Role, user.Role);
        if (user.CustomerId is { } customerId) yield return new Claim(CustomerId, customerId.ToString());
    }

    /// <summary>Builds the actor for the current request. Throws when the principal is not one of ours.</summary>
    public static Actor ToActor(this ClaimsPrincipal principal, HttpContext context)
    {
        if (!Guid.TryParse(principal.FindFirstValue(UserId), out var userId) ||
            !Enum.TryParse<UserRole>(principal.FindFirstValue(Role), out var role))
        {
            throw new AppException(ErrorKind.Unauthorized, "unauthenticated", "Authentication is required.");
        }
        Guid? customerId = Guid.TryParse(principal.FindFirstValue(CustomerId), out var c) ? c : null;
        return new Actor(userId, principal.FindFirstValue(Username) ?? "", role, customerId, context.GetCorrelationId());
    }
}

public static class Policies
{
    public const string Staff = "Staff";
    public const string Admin = "Admin";
}

public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "Aegis.CorrelationId";

    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string s ? s : context.TraceIdentifier;

    /// <summary>
    /// Accepts a caller-supplied correlation id when it is short and made of
    /// safe characters, otherwise generates one. The id is echoed in the
    /// response and attached to every log line of the request.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var id = IsSafe(supplied) ? supplied : Guid.CreateVersion7().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        var logger = context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger("AegisBank.Request")
            : null;
        using (logger?.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next();
        }
    });

    private static bool IsSafe(string value)
    {
        if (value.Length is < 8 or > 64) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        }
        return true;
    }
}

public static class SecurityHeaders
{
    public static IApplicationBuilder UseAegisSecurityHeaders(this IApplicationBuilder app, string contentSecurityPolicy) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = contentSecurityPolicy;
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next();
        });
}
