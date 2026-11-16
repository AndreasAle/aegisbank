using System.Security.Claims;
using System.Text;
using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AegisBank.Api.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "aegisbank";
    public string Audience { get; set; } = "aegisbank-api";
    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supplied through the Jwt__SigningKey environment variable.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 10;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be configured with at least 32 bytes (set Jwt__SigningKey).");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record AccessToken(string Value, DateTime ExpiresAt);

public sealed class JwtTokenFactory(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;
    private readonly SigningCredentials _credentials = new(options.Value.CreateKey(), SecurityAlgorithms.HmacSha256);
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Create(AuthenticatedUser user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = AegisClaims.For(user).Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("N")));
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = expires,
            SigningCredentials = _credentials,
        });
        return new AccessToken(token, expires);
    }
}
