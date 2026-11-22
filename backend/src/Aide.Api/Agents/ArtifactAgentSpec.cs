using Aide.Api.Features.Artifacts;
using Aide.Api.Jobs;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;

namespace Aide.Api.Agents;

/// <summary>
/// How a producing agent's reply becomes an artifact draft: <see cref="ToContent"/> checks the reply and returns the
/// draft Markdown (or the error that rejects it); <see cref="Corrective"/> builds the one corrective re-run's request from
/// the rejected reply and the error (FR-28, FR-29).
/// </summary>
public sealed record ArtifactAgentSpec(
    string Type,
    Func<string, Result<string>> ToContent,
    Func<string, DomainError, AgentRunRequest> Corrective)
{
    /// <summary>Called with a newly added draft (not one a redelivered run already stored), before it is saved: e.g. its event (FR-193).</summary>
    public Action<Artifact, AgentRunOutcome>? OnAdded { get; init; }

    /// <summary>The corrective re-run's check, when it differs from the first run's (FR-38: the review checks only that its reply reads).</summary>
    public Func<string, Result<string>>? CorrectiveToContent { get; init; }

    /// <summary>Whether the accepted reply becomes a draft here; <c>false</c>: the caller decides what it stores (FR-38's optimisation plan).</summary>
    public bool StoresDraft { get; init; } = true;

    /// <summary>The job logs a rejected reply itself (0.6.1's line), so the engine path doesn't log it again.</summary>
    public bool LogsRejection { get; init; }

    /// <summary>The spec of the corrective re-run (its check is <see cref="CorrectiveToContent"/> when set).</summary>
    public ArtifactAgentSpec ForCorrective() => CorrectiveToContent is { } check ? this with { ToContent = check, CorrectiveToContent = null } : this;

    /// <summary>Joins feedback parts (blank ones dropped), or <c>null</c>.</summary>
    public static string? JoinFeedback(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }
}

/// <summary>The project and artifact inputs of an agent job (FR-24), read with their integrity re-checked (FR-20).</summary>
public sealed class AgentJobInputs(AideDbContext db, ArtifactIntegrityGuard integrity)
{
    /// <summary>The job payload; a payload that can't be read fails the job permanently.</summary>
    public static LifecycleJobPayload Payload(JobExecution job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            return AideJson.Deserialize<LifecycleJobPayload>(job.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new JobFailedException($"The {job.Type} job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

    /// <summary>The current (highest locked) version of a type whose content still matches its hash, or <c>null</c>.</summary>
    public async Task<Artifact?> CurrentAsync(Guid projectId, string type, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == type && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return await IntactAsync(artifact, cancellationToken);
    }

    /// <summary>The highest version of a type that is intact (not corrupted), whatever its status, or <c>null</c>.</summary>
    public async Task<Artifact?> LatestIntactAsync(Guid projectId, string type, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var candidates = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == type && a.Status != ArtifactStatus.Corrupted)
            .OrderByDescending(a => a.Version)
            .Take(5)
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            if (await IntactAsync(candidate, cancellationToken) is { } intact)
            {
                return intact;
            }
        }

        return null;
    }

    /// <summary>
    /// The version a re-run should revise (FR-13, FR-17): the rejected artifact, or the artifact of the rejected gate, when
    /// it is of <paramref name="type"/> and intact. Phase entry and regeneration after corruption (FR-20) revise nothing.
    /// </summary>
    public async Task<Artifact?> PreviousAsync(LifecycleJobPayload payload, string type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Guid? artifactId = payload.Trigger switch
        {
            LifecycleJobPayload.ArtifactRejected => payload.ArtifactId,
            LifecycleJobPayload.GateRejected when payload.GateId is { } gateId => await db.ApprovalGates.AsNoTracking()
                .Where(g => g.Id == gateId)
                .Select(g => g.ArtifactId)
                .FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
        if (artifactId is not { } id)
        {
            return null;
        }

        var artifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.ProjectId == payload.ProjectId && a.Type == type, cancellationToken);
        return await IntactAsync(artifact, cancellationToken);
    }

    /// <summary>
    /// Should a job of this trigger still produce a <paramref name="type"/> draft? Phase-entry jobs only while the project
    /// is still in the producing phase (a stale job after a rollback or fallback does nothing); re-runs whenever a draft of
    /// the type may be created (<see cref="SdlcArtifactRules.CanCreateDraft"/>).
    /// </summary>
    public static bool ShouldRun(LifecycleJobPayload payload, string type, Project project)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(project);
        if (SdlcArtifactRules.CanCreateDraft(type, project.CurrentPhase, project.Status).IsFailure)
        {
            return false;
        }

        return payload.Trigger != LifecycleJobPayload.StageEntered || project.CurrentPhase == SdlcArtifactRules.ProducingPhase(type);
    }

    /// <summary>FR-172: for a release version, when its oldest still-queued or running import was queued, and whether a failed import awaits a retry.</summary>
    public async Task<(DateTimeOffset? OldestPending, bool HasFailed)> ImportStateAsync(Guid releaseVersionId, CancellationToken cancellationToken)
    {
        var imports = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == releaseVersionId
                && (i.Status == Domain.Trackers.TrackerImportStatus.Queued || i.Status == Domain.Trackers.TrackerImportStatus.Running || i.Status == Domain.Trackers.TrackerImportStatus.Failed))
            .Select(i => new { i.Status, i.CreatedAt })
            .ToListAsync(cancellationToken);
        var pending = imports.Where(i => i.Status != Domain.Trackers.TrackerImportStatus.Failed).Select(i => (DateTimeOffset?)i.CreatedAt).Min();
        return (pending, imports.Any(i => i.Status == Domain.Trackers.TrackerImportStatus.Failed));
    }

    public static InputArtifactRef Ref(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new InputArtifactRef(artifact.Id, artifact.Type, artifact.Version);
    }

    /// <summary><paramref name="artifact"/> when its content still matches its hash (FR-20), else <c>null</c>.</summary>
    public async Task<Artifact?> IntactAsync(Artifact? artifact, CancellationToken cancellationToken)
    {
        if (artifact is null)
        {
            return null;
        }

        // A mismatch is marked corrupted (with its alert and regeneration) and never fed to an agent.
        return await integrity.CheckAsync(artifact, cancellationToken) == ArtifactIntegrity.Ok ? artifact : null;
    }
}

/// <summary>Logs why an agent job ended without running.</summary>
internal static partial class AgentJobLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping the {JobType} job ({Trigger}): the project is gone or its phase ({Phase}) no longer needs it")]
    public static partial void Skipped(ILogger logger, string jobType, string trigger, Phase? phase);
}
