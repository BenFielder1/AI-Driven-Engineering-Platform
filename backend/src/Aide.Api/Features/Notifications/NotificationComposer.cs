using System.Globalization;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// A logical notification ready to show or send: its text, the app page it opens, and its gate (gate types).
/// <see cref="ProjectId"/> is <c>null</c> (and <see cref="ProjectName"/> empty) for a personal notification raised outside any
/// project; <see cref="RecipientUserId"/> is the one recipient of a personal notification (FR-92).
/// </summary>
public sealed record ComposedNotification(
    NotificationType Type,
    Guid? ProjectId,
    string ProjectName,
    Guid SourceId,
    NotificationText Text,
    string LinkPath,
    ApprovalGate? Gate)
{
    public Guid? RecipientUserId { get; init; }

    /// <summary>FR-155: <c>false</c> when the event is recorded without a notification (<see cref="NotificationRules.Notifies"/>).</summary>
    public bool Notifies { get; init; } = true;

    public string Key => NotificationRules.Key(Type, SourceId);
}

/// <summary>
/// FR-47: reads what a notification is about from the database — the gate for gate notifications, the audit event for the
/// others — and words it with <see cref="NotificationMessages"/>. Used when the notification job fans out and again on
/// every channel retry, so retries need nothing stored but the key.
/// </summary>
public sealed class NotificationComposer(AideDbContext db)
{
    /// <summary>The notification, or <c>null</c> when its project, gate or event no longer exists.</summary>
    public async Task<ComposedNotification?> ComposeAsync(Guid? projectId, NotificationType type, Guid sourceId, CancellationToken cancellationToken)
    {
        string? projectName;
        if (projectId is { } id)
        {
            projectName = await db.Projects.AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (projectName is null)
            {
                return null;
            }
        }
        else if (NotificationRules.IsPersonal(type))
        {
            projectName = string.Empty;
        }
        else
        {
            return null;
        }

        if (NotificationRules.IsAboutGate(type))
        {
            var gate = await db.ApprovalGates.AsNoTracking().FirstOrDefaultAsync(g => g.Id == sourceId && g.ProjectId == projectId, cancellationToken);
            if (gate is null)
            {
                return null;
            }

            var artifact = gate.ArtifactId is { } artifactId
                ? await db.Artifacts.AsNoTracking().Where(a => a.Id == artifactId).Select(a => new { a.Type, a.Version }).FirstOrDefaultAsync(cancellationToken)
                : null;
            var facts = new NotificationFacts(type, projectName)
            {
                GateType = gate.Type,
                ArtifactType = artifact?.Type.ToWire(),
                ArtifactVersion = artifact?.Version,
            };
            return new ComposedNotification(type, projectId, projectName, sourceId, NotificationMessages.Compose(facts), NotificationRules.LinkPath(type, projectId), gate);
        }

        var payload = await db.Events.AsNoTracking()
            .Where(e => e.Id == sourceId && e.ProjectId == projectId)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var read = ReadFacts(type, projectName, payload);
        return new ComposedNotification(
            type,
            projectId,
            projectName,
            sourceId,
            NotificationMessages.Compose(read),
            NotificationRules.LinkPath(type, projectId, read.ArtifactType, read.DeploymentEnvironment, HasRunId(payload)),
            null)
        {
            RecipientUserId = NotificationRules.IsPersonal(type) ? UserId(payload) : null,
            Notifies = NotificationRules.Notifies(read),
        };
    }

    /// <summary>The <c>userId</c> of an event payload: the connection's owner of <c>CONNECTION_INVALID</c>.</summary>
    private static Guid? UserId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("userId", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out var userId)
                    ? userId
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a <c>MIGRATION_RUN_FAILED</c> payload names a recorded run (host-mode migrations have none).</summary>
    private static bool HasRunId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("runId", out var value)
                && value.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static NotificationFacts ReadFacts(NotificationType type, string projectName, string payload)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new NotificationFacts(type, projectName);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new NotificationFacts(type, projectName);
        }

        return new NotificationFacts(type, projectName)
        {
            ArtifactType = type == NotificationType.ArtifactReady ? Text(root, "type") : null,
            ArtifactVersion = Int(root, "version"),
            StepNumber = Int(root, "stepNumber"),
            Attempts = Int(root, "attempt"),
            Agent = Text(root, "agent"),
            ErrorCode = Text(root, "errorCode"),
            Cycle = Int(root, "cycle"),
            Score = root.TryGetProperty("overallScore", out var score) && score.ValueKind == JsonValueKind.Number
                ? score.GetDecimal().ToString("0.###", CultureInfo.InvariantCulture)
                : null,
            Decision = Text(root, "decision"),
            Tag = Text(root, "tag"),
            FromVersion = Int(root, "fromCheckpoint") ?? Int(root, "fromVersion"),
            TargetVersion = Int(root, "targetCheckpoint") ?? Int(root, "targetVersion"),
            Reason = type is NotificationType.Rollback or NotificationType.LlmBindingCleared or NotificationType.GitBindingCleared
                or NotificationType.DatabaseExpiring or NotificationType.DeploymentTargetAttention
                ? Text(root, "reason")
                : null,
            PullRequestUrl = Text(root, "pullRequestUrl"),
            Provider = Text(root, "provider"),
            DeploymentEnvironment = Text(root, "target") ?? Text(root, "environment"),
            Component = Text(root, "component"),
            ExpiresAt = root.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.ValueKind == JsonValueKind.String && expiresAt.TryGetDateTimeOffset(out var expiry)
                ? expiry
                : null,
            DeploymentUrl = Text(root, "url"),
            ReleaseVersion = Text(root, "releaseVersion"),
            MergeReasons = root.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array
                ? [.. reasons.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Object ? Text(r, "message") : null).OfType<string>()]
                : [],
        };
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
