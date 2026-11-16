using System.Security.Cryptography;
using System.Text;
using AegisBank.Application;
using AegisBank.Domain;
using AegisBank.Infrastructure.Persistence;
using AegisBank.Infrastructure.Transfers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AegisBank.Infrastructure.Auth;

public sealed class AuthOptions
{
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class AuthService(
    AegisDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly AuthOptions _options = options.Value;

    // Verified when the username is unknown so that both paths cost one hash.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<User>().HashPassword(new User(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    public async Task<AuthenticatedUser?> ValidateCredentialsAsync(string username, string password, string? correlationId, CancellationToken ct = default)
    {
        var normalized = NormalizeUsername(username);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null || user.Role == UserRole.System)
        {
            hasher.VerifyHashedPassword(new User(), DummyHash.Value, password);
            await RecordFailureAsync(null, normalized, "unknown_user", correlationId, ct);
            return null;
        }

        if (!user.IsActive || (user.LockoutEndsAt is { } until && until > now))
        {
            hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            await RecordFailureAsync(user.Id, normalized, user.IsActive ? "locked_out" : "inactive", correlationId, ct);
            return null;
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _options.MaxFailedLogins)
            {
                user.LockoutEndsAt = now.AddMinutes(_options.LockoutMinutes);
                user.FailedLoginCount = 0;
                db.AuditLogs.Add(Audit.Entry(user.Id, correlationId, "auth.lockout", "User", user.Id.ToString(),
                    new { until = user.LockoutEndsAt }, clock));
                logger.LogWarning("Account locked after repeated failed sign-ins: {UserId}", user.Id);
            }
            user.UpdatedAt = now;
            await RecordFailureAsync(user.Id, normalized, "bad_password", correlationId, ct);
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, password);
        user.FailedLoginCount = 0;
        user.LockoutEndsAt = null;
        user.UpdatedAt = now;
        db.AuditLogs.Add(Audit.Entry(user.Id, correlationId, "auth.login.succeeded", "User", user.Id.ToString(), null, clock));
        await db.SaveChangesAsync(ct);
        return ToAuthenticated(user);
    }

    public async Task<AuthenticatedUser?> FindActiveUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        return user is null || user.Role == UserRole.System ? null : ToAuthenticated(user);
    }

    public async Task<RefreshTokenGrant> IssueRefreshTokenAsync(AuthenticatedUser user, CancellationToken ct = default)
    {
        var (token, entity) = NewToken(user.Id, Guid.CreateVersion7());
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return new RefreshTokenGrant(user, token, entity.ExpiresAt);
    }

    public async Task<RefreshTokenGrant?> RotateRefreshTokenAsync(string refreshToken, string? correlationId, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Row lock: two concurrent uses of the same token are serialized, so
        // exactly one of them rotates and the other is seen as reuse.
        var current = (await db.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();
        if (current is null) return null;

        if (current.RevokedAt is not null)
        {
            if (current.ReplacedByTokenId is not null)
            {
                // A token that was already exchanged is being presented again:
                // treat the whole family as compromised.
                await db.RefreshTokens
                    .Where(t => t.FamilyId == current.FamilyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
                db.AuditLogs.Add(Audit.Entry(current.UserId, correlationId, "auth.refresh.reuse_detected", "User",
                    current.UserId.ToString(), new { family = current.FamilyId }, clock));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogWarning("Refresh token reuse detected for user {UserId}; token family revoked", current.UserId);
            }
            return null;
        }
        if (current.ExpiresAt <= now) return null;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId && u.IsActive, ct);
        if (user is null) return null;

        var (token, next) = NewToken(user.Id, current.FamilyId);
        current.RevokedAt = now;
        current.ReplacedByTokenId = next.Id;
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new RefreshTokenGrant(ToAuthenticated(user), token, next.ExpiresAt);
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, Guid userId, string? correlationId, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var token = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
        if (token is not null)
        {
            await db.RefreshTokens
                .Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }
        await RecordSignOutAsync(userId, "api", correlationId, ct);
    }

    public async Task RecordSignOutAsync(Guid userId, string channel, string? correlationId, CancellationToken ct = default)
    {
        db.AuditLogs.Add(Audit.Entry(userId, correlationId, "auth.logout", "User", userId.ToString(), new { channel }, clock));
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(Guid? userId, string username, string reason, string? correlationId, CancellationToken ct)
    {
        // The reason is kept for operators; the caller only ever learns "invalid credentials".
        db.AuditLogs.Add(Audit.Entry(userId, correlationId, "auth.login.failed", "User", userId?.ToString(),
            new { username = username.Length > 64 ? username[..64] : username, reason }, clock));
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Failed sign-in ({Reason})", reason);
    }

    private (string Token, RefreshToken Entity) NewToken(Guid userId, Guid familyId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        return (token, new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
        });
    }

    public static string NormalizeUsername(string username) => (username ?? "").Trim().ToLowerInvariant();

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AuthenticatedUser ToAuthenticated(User u) => new(u.Id, u.Username, u.Email, u.Role.ToString(), u.CustomerId);
}
