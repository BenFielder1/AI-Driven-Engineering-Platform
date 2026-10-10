namespace Aide.Domain.Entities;

/// <summary>Append-only audit event, table <c>events</c> (FR-11, FR-50, FR-51); the source of the live and activity feeds.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid? ProjectId { get; init; }

    public string Type { get; init; } = string.Empty;

    /// <summary>Event payload as raw JSON (schemaless).</summary>
    public string Payload { get; init; } = "{}";

    public string Actor { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}
