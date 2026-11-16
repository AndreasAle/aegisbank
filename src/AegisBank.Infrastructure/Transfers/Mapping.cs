using System.Text.Json;
using AegisBank.Application;
using AegisBank.Domain;

namespace AegisBank.Infrastructure.Transfers;

internal static class Audit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AuditLog Entry(Actor? actor, string action, string entityType, string? entityId, object? metadata, TimeProvider clock) =>
        Entry(actor?.UserId, actor?.CorrelationId, action, entityType, entityId, metadata, clock);

    public static AuditLog Entry(Guid? actorId, string? correlationId, string action, string entityType, string? entityId, object? metadata, TimeProvider clock) => new()
    {
        Id = Guid.CreateVersion7(),
        ActorId = actorId,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        Metadata = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, Json),
        CorrelationId = correlationId is { Length: > 64 } ? correlationId[..64] : correlationId,
        CreatedAt = clock.GetUtcNow().UtcDateTime,
    };
}

/// <summary>Flat row used to project transfers in SQL before mapping to the DTO.</summary>
internal sealed record TransferRow(
    Guid Id, string ReferenceNumber, TransferKind Kind, TransferStatus Status,
    Guid SourceAccountId, string SourceAccountNumber, Guid DestinationAccountId, string DestinationAccountNumber,
    long AmountMinorUnits, string Currency, string? FailureCode, Guid? JournalEntryId, Guid? ReversalOfTransferId,
    Guid InitiatedByUserId, DateTime CreatedAt, DateTime? CompletedAt);

internal static class TransferMapping
{
    public static IQueryable<TransferRow> Project(IQueryable<Transfer> query) => query.Select(t => new TransferRow(
        t.Id, t.ReferenceNumber, t.Kind, t.Status,
        t.SourceAccountId, t.SourceAccount!.AccountNumber,
        t.DestinationAccountId, t.DestinationAccount!.AccountNumber,
        t.AmountMinorUnits, t.Currency, t.FailureCode,
        (Guid?)t.JournalEntry!.Id, t.ReversalOfTransferId, t.InitiatedByUserId, t.CreatedAt, t.CompletedAt));

    public static TransferDto ToDto(TransferRow r) => new(
        r.Id, r.ReferenceNumber, r.Kind.ToString(), r.Status.ToString(),
        r.SourceAccountId, r.SourceAccountNumber, r.DestinationAccountId, r.DestinationAccountNumber,
        r.AmountMinorUnits, r.Currency, r.FailureCode,
        r.FailureCode is null ? null : EngineMessages.Describe(r.FailureCode),
        r.JournalEntryId, r.ReversalOfTransferId, r.InitiatedByUserId, r.CreatedAt, r.CompletedAt);

    public static TransferDto ToDto(Transfer t, string sourceNumber, string destinationNumber, Guid? journalId) => new(
        t.Id, t.ReferenceNumber, t.Kind.ToString(), t.Status.ToString(),
        t.SourceAccountId, sourceNumber, t.DestinationAccountId, destinationNumber,
        t.AmountMinorUnits, t.Currency, t.FailureCode,
        t.FailureCode is null ? null : EngineMessages.Describe(t.FailureCode),
        journalId, t.ReversalOfTransferId, t.InitiatedByUserId, t.CreatedAt, t.CompletedAt);

    public static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw AppException.Validation("invalid_filter",
                $"Unknown {field} '{value}'. Allowed: {string.Join(", ", Enum.GetNames<T>())}.");
}
