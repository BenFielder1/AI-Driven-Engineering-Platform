using Aide.Api.Features.Artifacts;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// How a producing agent's reply becomes an artifact draft: <see cref="ToContent"/> checks the reply and returns the
/// draft Markdown (or the error that rejects it); <see cref="Corrective"/> builds the one corrective re-run's request from
/// the rejected reply and the error (FR-28, FR-29).
/// </summary>
public sealed record ArtifactAgentSpec(
    ArtifactType Type,
    Func<string, Result<string>> ToContent,
    Func<string, DomainError, AgentRunRequest> Corrective);

/// <summary>
/// The shared flow of the refinement, spec and plan jobs (FR-27–FR-29), on top of <see cref="AgentRunner"/>:
/// <list type="number">
/// <item>Run the agent. The runner commits the run's completion and token count on its own; if the reply is accepted,
/// the draft is then added (agent-authored, parent = latest, older draft superseded) and saved.</item>
/// <item>If the reply is rejected, re-run once (run key <see cref="AgentOutput.CorrectiveRunKey"/>) with the reasons as
/// feedback. That run is given the check: if its reply is rejected too, the run fails with the error's code and no
/// draft is created.</item>
/// </list>
/// Redelivery is idempotent: run ids are derived from the job id, a succeeded run returns its stored reply without a
/// provider call, the same deterministic check picks the same branch, and the draft writer returns the draft a run
/// already created. A conflict while saving the draft fails the job transiently; the retry re-uses the succeeded run, so
/// the provider is never called (or paid) twice for one reply (FR-26).
/// </summary>
public sealed partial class ArtifactAgentJob(AgentRunner runner, AgentDraftWriter drafts, AideDbContext db, ILogger<ArtifactAgentJob> logger)
{
    /// <summary>Runs <paramref name="request"/> (with its corrective re-run if needed); returns the id of the run that produced the draft.</summary>
    public async Task<Guid> RunAsync(AgentRunRequest request, ArtifactAgentSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);

        // The first reply is kept on a succeeded run even when rejected: the corrective re-run gets it as context.
        var first = await runner.RunAsync(request with { RunKey = null, Check = null }, cancellationToken);
        var checkedFirst = spec.ToContent(first.Text);
        if (checkedFirst.IsSuccess)
        {
            await AddDraftAsync(request.ProjectId, spec.Type, checkedFirst.Value, first, cancellationToken);
            return first.RunId;
        }

        LogRejected(logger, request.Agent, checkedFirst.Error!.Code, checkedFirst.Error.Message);
        var corrective = spec.Corrective(first.Text, checkedFirst.Error);
        var second = await runner.RunAsync(
            corrective with
            {
                RunKey = AgentOutput.CorrectiveRunKey,
                Check = text => spec.ToContent(text) is { IsFailure: true } failed ? Result.Failure(failed.Error!) : Result.Success(),
            },
            cancellationToken);
        var checkedSecond = spec.ToContent(second.Text);
        if (checkedSecond.IsFailure)
        {
            // Only reachable if a stored succeeded run no longer passes (e.g. the rules changed): never draft it.
            throw new JobFailedException(checkedSecond.Error!.Message, transient: false, checkedSecond.Error.Code);
        }

        await AddDraftAsync(request.ProjectId, spec.Type, checkedSecond.Value, second, cancellationToken);
        return second.RunId;
    }

    private async Task AddDraftAsync(Guid projectId, ArtifactType type, string content, AgentRunOutcome run, CancellationToken cancellationToken)
    {
        var draft = await drafts.AddAsync(projectId, type, content, run.RunId, run.CompletedAt, cancellationToken);
        if (draft is null)
        {
            LogDraftSkipped(logger, type);
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The project changed (e.g. a rollback or phase fallback), a concurrent writer took the version or changed the
            // superseded draft: retry. The retry re-uses the succeeded run and re-checks whether the draft is still allowed.
            throw new JobFailedException($"The {type.ToWire()} draft collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    /// <summary>Joins feedback parts (blank ones dropped), or <c>null</c>.</summary>
    public static string? JoinFeedback(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Agent} reply was rejected with {ErrorCode}; re-running once with the reasons as feedback: {Reason}")]
    private static partial void LogRejected(ILogger logger, AgentKind agent, string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "No {ArtifactType} draft created: the project's phase no longer allows it")]
    private static partial void LogDraftSkipped(ILogger logger, ArtifactType artifactType);
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
    public async Task<Artifact?> CurrentAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == type && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return await IntactAsync(artifact, cancellationToken);
    }

    /// <summary>The highest version of a type that is intact (not corrupted), whatever its status, or <c>null</c>.</summary>
    public async Task<Artifact?> LatestIntactAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
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
    public async Task<Artifact?> PreviousAsync(LifecycleJobPayload payload, ArtifactType type, CancellationToken cancellationToken)
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
    /// the type may be created (<see cref="ArtifactRules.CanCreateDraft"/>).
    /// </summary>
    public static bool ShouldRun(LifecycleJobPayload payload, ArtifactType type, Project project)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(project);
        if (ArtifactRules.CanCreateDraft(type, project.CurrentPhase, project.Status).IsFailure)
        {
            return false;
        }

        return payload.Trigger != LifecycleJobPayload.PhaseEntered || project.CurrentPhase == ArtifactRules.ProducingPhase(type);
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
