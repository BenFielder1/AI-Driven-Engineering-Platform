using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Activity;

/// <summary>
/// FR-50: a project's activity feed (§6 <c>/api/projects/:id/activity</c>): the project's audit events, newest first, keyset
/// paginated on (occurredAt, id) with an opaque <c>before</c> cursor, filterable by category.
/// </summary>
public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/activity")
            .WithTags("Activity")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListActivity)
            .WithName("ListProjectActivity")
            .WithSummary("The project's activity feed, newest first (members). Filter by category with type; load more with before = the previous page's nextBefore. limit defaults to 50 (max 100). releaseVersionId limits it to the events while that release version was current (from its start to the start of the next); without it, the whole history.")
            .Produces<ActivityPageDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<ActivityPageDto>, ValidationProblem, ProblemHttpResult, StatusCodeHttpResult>> ListActivity(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken,
        [WireEnum(typeof(EventCategory))] string? type = null,
        [MaxLength(ActivityLimits.CursorMaxLength)] string? before = null,
        [Range(1, ActivityLimits.MaxLimit)] int limit = ActivityLimits.DefaultLimit,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        ActivityCursor? cursor = null;
        if (before is not null)
        {
            if (ActivityCursor.TryDecode(before) is not { } decoded)
            {
                return ApiProblems.Validation(new Dictionary<string, string[]>
                {
                    ["before"] = ["before must be a nextBefore value from a previous page."],
                });
            }

            cursor = decoded;
        }

        var query = cursor is { } c
            ? db.Events.FromSql($"SELECT * FROM events WHERE (created_at, id) < ({c.OccurredAt}, {c.Id})")
            : db.Events;
        query = query.AsNoTracking().Where(e => e.ProjectId == id);
        if (releaseVersionId is { } versionId)
        {
            // Events carry no release version: a version's activity is the time from its start to the next version's start.
            var versions = await db.ReleaseVersions.AsNoTracking()
                .Where(v => v.ProjectId == id)
                .OrderBy(v => v.CreatedAt)
                .ThenBy(v => v.Id)
                .Select(v => new { v.Id, v.CreatedAt })
                .ToListAsync(cancellationToken);
            var index = versions.FindIndex(v => v.Id == versionId);
            if (index < 0)
            {
                return ApiProblems.NotFound("The release version was not found in this project.");
            }

            if (index > 0)
            {
                var from = versions[index].CreatedAt;
                query = query.Where(e => e.CreatedAt >= from);
            }

            if (index < versions.Count - 1)
            {
                var until = versions[index + 1].CreatedAt;
                query = query.Where(e => e.CreatedAt < until);
            }
        }
        if (type is not null)
        {
            var types = EventTypes.InCategory(EnumStrings.Parse<EventCategory>(type)).ToList();
            query = query.Where(e => types.Contains(e.Type));
        }

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(limit).ToList();
        var actorOf = await ActorResolver.ForActorsAsync(db, page.Select(e => e.Actor), cancellationToken);
        var items = page.Select(e => ToDto(e, actorOf(e.Actor))).ToList();
        var next = rows.Count > limit ? new ActivityCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return TypedResults.Ok(new ActivityPageDto(items, next));
    }

    private static ActivityItemDto ToDto(AuditEvent e, ActorDto actor)
    {
        var payload = ParsePayload(e.Payload);
        return new ActivityItemDto(e.Id, e.Type, EventTypes.CategoryOf(e.Type), e.CreatedAt, actor, ActivitySummary.For(e.Type, payload), payload);
    }

    /// <summary>An event or job payload as a JSON object; an unreadable or non-object payload is <c>{}</c>.</summary>
    internal static JsonObject ParsePayload(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Opaque keyset cursor: base64url of "&lt;UTC ticks&gt;.&lt;event id&gt;".</summary>
internal sealed record ActivityCursor(DateTimeOffset OccurredAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{Id:N}"));

    public static ActivityCursor? TryDecode(string value)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            var parts = text.Split('.');
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(parts[1], "N", out var id)
                    ? new ActivityCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>One-line descriptions of audit events for the activity feed (FR-50).</summary>
internal static class ActivitySummary
{
    public static string For(string type, JsonObject payload)
    {
        string? Get(string key) => payload.TryGetPropertyValue(key, out var node) && node is JsonValue value
            ? value.ToString()
            : null;

        return type switch
        {
            EventTypes.PhaseChanged => $"Phase changed from {Get("from") ?? "?"} to {Get("to") ?? "?"} (checkpoint {Get("checkpoint") ?? Get("projectVersion") ?? "?"})",
            EventTypes.GateCreated => $"{Get("type") ?? "Approval"} gate created",
            EventTypes.GateApproved => Get("decision") is { } decision
                ? $"{Get("type") ?? "Approval"} gate decided: {decision}"
                : $"{Get("type") ?? "Approval"} gate approved",
            EventTypes.GateRejected => $"{Get("type") ?? "Approval"} gate rejected",
            EventTypes.GateCancelled => $"{Get("type") ?? "Approval"} gate cancelled",
            EventTypes.GateOverdue => $"{Get("type") ?? "Approval"} gate is overdue",
            EventTypes.RollbackCompleted => $"Rolled back from checkpoint {Get("fromCheckpoint") ?? Get("fromVersion") ?? "?"} to checkpoint {Get("targetCheckpoint") ?? Get("targetVersion") ?? "?"}",
            EventTypes.VersionStarted => $"Version {Get("version") ?? "?"} started",
            EventTypes.PresetChanged => $"Preset of version {Get("version") ?? "?"} changed from {Get("fromPresetId") ?? "none"} to {Get("presetId") ?? "none"}",
            EventTypes.StackChanged => $"Stack of version {Get("version") ?? "?"} changed ({LayerList(payload)})",
            EventTypes.ArtifactRestored => $"{Get("type") ?? "Artifact"} v{Get("restoredFromVersion") ?? "?"} restored as draft v{Get("version") ?? "?"}",
            EventTypes.CodebaseSummaryCreated => $"Codebase summary v{Get("version") ?? "?"} created by the Repository Analyst",
            EventTypes.ReleaseAwaitingMerge => $"Release {Get("tag") ?? Get("version") ?? "?"} awaits a merge on {Domain.GitHosts.GitHostNames.Display(Get("provider"))}",
            EventTypes.ReleaseMergedExternally => $"Release {Get("tag") ?? Get("version") ?? "?"} merged on {Domain.GitHosts.GitHostNames.Display(Get("provider"))}",
            EventTypes.RunStarted => $"{Get("agent") ?? "Agent"} run started on {Get("model") ?? "?"}",
            EventTypes.RunCompleted => $"{Get("agent") ?? "Agent"} run completed ({Get("inputTokens") ?? "?"} in / {Get("outputTokens") ?? "?"} out tokens)",
            EventTypes.RunFailed => $"{Get("agent") ?? "Agent"} run failed: {Get("errorCode") ?? "error"}",
            EventTypes.BudgetExceeded => $"{Get("agent") ?? "Agent"} run refused: token budget used up",
            EventTypes.ConnectionInvalid => Get("provider") switch
            {
                "vercel" => "Vercel rejected the connection's token; it is marked invalid",
                "netlify" => "Netlify rejected the connection's token; it is marked invalid",
                "cloudflare" => "Cloudflare rejected the connection's token; it is marked invalid",
                "render" => "Render rejected the connection's API key; it is marked invalid",
                "railway" => "Railway rejected the connection's token; it is marked invalid",
                "supabase" => "Supabase rejected the connection's token; it is marked invalid",
                "github" => "GitHub rejected the user's connection; it is marked invalid",
                "gitlab" => "GitLab rejected the user's connection; it is marked invalid",
                "bitbucket" => "Bitbucket rejected the user's connection; it is marked invalid",
                var provider => $"{provider ?? "LLM"} rejected the bound connection's key; it is marked invalid",
            },
            EventTypes.LlmBindingCleared => $"LLM binding cleared ({Get("reason") ?? "?"})",
            EventTypes.GitBindingCleared => $"Git binding cleared ({Get("reason") ?? "?"})",
            EventTypes.StepStarted => $"Step {Get("stepNumber") ?? "?"} started (attempt {Get("attempt") ?? "?"})",
            EventTypes.StepCompleted => $"Step {Get("stepNumber") ?? "?"} completed",
            EventTypes.StepRejected => $"Step {Get("stepNumber") ?? "?"} rejected by the Execution Guardian (attempt {Get("attempt") ?? "?"})",
            EventTypes.StepBlocked => $"Step {Get("stepNumber") ?? "?"} blocked after {Get("attempt") ?? "?"} attempts",
            EventTypes.StepUnblocked => $"Step {Get("stepNumber") ?? "?"} unblocked",
            EventTypes.ExecutionStarted => "Execution started",
            EventTypes.ExecutionPaused => "Execution paused",
            EventTypes.ExecutionResumed => "Execution resumed",
            EventTypes.ExecutionCompleted => "Execution completed: every step is done",
            EventTypes.GitPushFailed => $"Pushing the project branch after step {Get("stepNumber") ?? "?"} failed; it will be pushed again",
            EventTypes.PullRequestOpened => Get("number") is { } number
                ? $"Pull request #{number} {(Get("reused") == "true" ? "reused" : "opened")}"
                : "Branch comparison recorded (local pull request)",
            EventTypes.PullRequestFailed => "Opening the pull request failed",
            EventTypes.PullRequestForgotten => "The recorded pull request was forgotten: the Git repository changed",
            EventTypes.ReviewStarted => $"Review cycle {Get("cycle") ?? "?"} started",
            EventTypes.ReviewCompleted => Get("decisionReason") is { } why
                ? $"Review cycle {Get("cycle") ?? "?"} completed: score {Get("overallScore") ?? "?"}, {Get("decision") ?? "?"} ({why})"
                : $"Review cycle {Get("cycle") ?? "?"} completed: score {Get("overallScore") ?? "?"}, {Get("decision") ?? "?"}",
            EventTypes.ReviewFailed => $"Review cycle {Get("cycle") ?? "?"} failed",
            EventTypes.ProjectCreated => "Project created",
            EventTypes.ProjectUpdated => "Project updated",
            EventTypes.MemberAdded => "Member added",
            EventTypes.MemberRoleChanged => "Member role changed",
            EventTypes.MemberRemoved => "Member removed",
            EventTypes.SettingsUpdated => "Settings updated",
            EventTypes.DeploymentCreated => $"{Get("target") ?? "Deployment"} deployment of {Get("gitRef") ?? "?"} recorded ({Get("trigger") ?? "auto"})",
            EventTypes.DeploymentUpdated => $"{Get("target") ?? "Deployment"} deployment {Get("status") ?? "updated"}{(Get("errorCode") is { } code ? $" ({code})" : string.Empty)}",
            EventTypes.DeploymentFailed => $"{Get("target") ?? "Production"} deployment failed: {Get("errorCode") ?? "error"}",
            EventTypes.DeploymentPreviewReady => $"Preview ready{(Get("url") is { } url ? $" at {url}" : string.Empty)}",
            EventTypes.PreviewRolloutCompleted => $"Preview rollout {Get("state") ?? "finished"}{(Get("errorCode") is { } rolloutCode ? $" ({rolloutCode})" : string.Empty)}",
            EventTypes.DatabaseReset => $"Preview database reset ({Get("method") ?? "reset_schema"})",
            EventTypes.MigrationRunFailed => $"{Get("environment") ?? "Database"} migration run failed: {Get("errorCode") ?? "error"}",
            EventTypes.MergedHeadMismatch => $"The host merged {Short(Get("mergedHeadSha"))}, not the migrated head {Short(Get("expectedHeadSha"))}; the production rollout migrates the merge commit",
            EventTypes.DeadLetterRetried => $"Dead-lettered {Get("jobType") ?? "?"} job retried by an Admin",
            _ when Get("type") is { } artifactType && Get("version") is { } version
                => $"{Humanize(type)}: {artifactType} v{version}",
            _ => Humanize(type),
        };
    }

    /// <summary>FR-117: <c>STACK_CHANGED.layersChanged</c> as "backend, data" ("no layers" when only re-pinned).</summary>
    private static string LayerList(JsonObject payload) =>
        payload.TryGetPropertyValue("layersChanged", out var node) && node is JsonArray layers && layers.Count > 0
            ? string.Join(", ", layers.Select(l => l?.ToString() ?? "?"))
            : "no layers";

    /// <summary>A commit id shortened to 12 characters ("?" when missing).</summary>
    private static string Short(string? sha) => sha is null ? "?" : sha[..Math.Min(12, sha.Length)];

    /// <summary><c>ARTIFACT_APPROVED</c> → "Artifact approved".</summary>
    private static string Humanize(string type)
    {
        var words = type.ToLowerInvariant().Replace('_', ' ');
        return words.Length == 0 ? type : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
