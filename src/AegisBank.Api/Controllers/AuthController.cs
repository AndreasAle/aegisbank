using System.ComponentModel.DataAnnotations;
using AegisBank.Api.Security;
using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AegisBank.Api.Controllers;

public sealed record LoginRequest(
    [Required, StringLength(64, MinimumLength = 1)] string Username,
    [Required, StringLength(256, MinimumLength = 1)] string Password);

public sealed record RefreshRequest([Required, StringLength(256, MinimumLength = 16)] string RefreshToken);

public sealed record TokenResponse(
    string AccessToken, string TokenType, int ExpiresInSeconds, DateTime AccessTokenExpiresAt,
    string RefreshToken, DateTime RefreshTokenExpiresAt);

public sealed record MeResponse(Guid Id, string Username, string Email, string Role, Guid? CustomerId);

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(IAuthService auth, JwtTokenFactory tokens, TimeProvider clock) : ControllerBase
{
    /// <summary>Exchanges a username and password for an access token and a refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await auth.ValidateCredentialsAsync(request.Username, request.Password, HttpContext.GetCorrelationId(), ct);
        // One answer for every failure cause: unknown user, wrong password, locked or disabled.
        if (user is null) return InvalidCredentials();
        return Ok(ToResponse(await auth.IssueRefreshTokenAsync(user, ct)));
    }

    /// <summary>Rotates a refresh token. The presented token becomes invalid.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var grant = await auth.RotateRefreshTokenAsync(request.RefreshToken, HttpContext.GetCorrelationId(), ct);
        if (grant is null)
            return Problems.Result(HttpContext, Problems.Create(401, "invalid_refresh_token", "The refresh token is not valid."));
        return Ok(ToResponse(grant));
    }

    /// <summary>Revokes the refresh token family of the presented token.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        var actor = User.ToActor(HttpContext);
        await auth.RevokeRefreshTokenAsync(request.RefreshToken, actor.UserId, actor.CorrelationId, ct);
        return NoContent();
    }

    /// <summary>Returns the authenticated user.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var actor = User.ToActor(HttpContext);
        var user = await auth.FindActiveUserAsync(actor.UserId, ct);
        if (user is null) return InvalidCredentials();
        return Ok(new MeResponse(user.Id, user.Username, user.Email, user.Role, user.CustomerId));
    }

    private JsonResult InvalidCredentials() =>
        Problems.Result(HttpContext, Problems.Create(401, "invalid_credentials", "Invalid credentials."));

    private TokenResponse ToResponse(RefreshTokenGrant grant)
    {
        var access = tokens.Create(grant.User);
        var seconds = (int)Math.Round((access.ExpiresAt - clock.GetUtcNow().UtcDateTime).TotalSeconds);
        return new TokenResponse(access.Value, "Bearer", seconds, access.ExpiresAt, grant.RefreshToken, grant.RefreshTokenExpiresAt);
    }
}
