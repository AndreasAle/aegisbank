using AegisBank.Domain;

namespace AegisBank.Application;

/// <summary>The authenticated principal on whose behalf an operation runs.</summary>
public sealed record Actor(Guid UserId, string Username, UserRole Role, Guid? CustomerId, string? CorrelationId = null)
{
    public bool IsStaff => Role is UserRole.Admin or UserRole.Auditor;
    public bool IsAdmin => Role == UserRole.Admin;

    /// <summary>True when the actor may read the given account.</summary>
    public bool CanView(Account account) =>
        IsStaff || (Role == UserRole.Customer && CustomerId is not null && account.CustomerId == CustomerId);

    /// <summary>True when the actor may move funds out of the given account.</summary>
    public bool CanDebit(Account account) =>
        !account.IsSystemAccount &&
        (IsAdmin || (Role == UserRole.Customer && CustomerId is not null && account.CustomerId == CustomerId));
}

public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unprocessable,
    Unavailable,
}

/// <summary>Well-known rows created by the first migration.</summary>
public static class SystemIds
{
    public static readonly Guid SystemUserId = new("00000000-0000-7000-8000-000000000001");
    public static readonly Guid VaultCashAccountId = new("00000000-0000-7000-8000-0000000000a1");
    public const string VaultCashAccountNumber = "1000000001";

    public static Actor SystemActor(string? correlationId = null) =>
        new(SystemUserId, "system", UserRole.System, null, correlationId);
}

/// <summary>
/// Expected failure with a client-safe message. Hosts translate it into a
/// Problem Details response; it never carries internal details.
/// </summary>
public sealed class AppException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;

    public static AppException NotFound(string what) => new(ErrorKind.NotFound, "not_found", $"{what} was not found.");
    public static AppException Forbidden(string message = "You are not allowed to perform this operation.") =>
        new(ErrorKind.Forbidden, "forbidden", message);
    public static AppException Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static AppException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 200;
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Parses "field" / "field:asc" / "field:desc" against a whitelist.</summary>
public readonly record struct SortSpec(string Field, bool Descending)
{
    public static SortSpec Parse(string? value, IReadOnlyCollection<string> allowed, string defaultField, bool defaultDescending)
    {
        if (string.IsNullOrWhiteSpace(value)) return new SortSpec(defaultField, defaultDescending);
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        var field = allowed.FirstOrDefault(a => string.Equals(a, parts[0], StringComparison.OrdinalIgnoreCase))
            ?? throw AppException.Validation("invalid_sort",
                $"Unsupported sort field. Allowed: {string.Join(", ", allowed)}.");
        var descending = parts.Length == 2
            ? parts[1].ToLowerInvariant() switch
            {
                "asc" => false,
                "desc" => true,
                _ => throw AppException.Validation("invalid_sort", "Sort direction must be 'asc' or 'desc'."),
            }
            : false;
        return new SortSpec(field, descending);
    }
}
