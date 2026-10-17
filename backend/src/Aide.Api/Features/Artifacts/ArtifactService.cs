using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Presets;
using Aide.Api.Jobs;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// FR-16–FR-19: creating, approving and rejecting artifact versions. <see cref="ArtifactRules"/>,
/// <see cref="InvalidationCascade"/>, <see cref="GateRules"/> and the lifecycle state machine decide; this service loads
/// the rows and writes each outcome — status changes, events, cascade, phase fallback, stale gates and follow-up jobs —
/// in one <c>SaveChanges</c> (one transaction) guarded by the project's and the artifacts' <c>xmin</c>.
/// </summary>
public sealed partial class ArtifactService(
    AideDbContext db,
    ArtifactIntegrityGuard integrity,
    LifecycleSnapshotLoader snapshots,
    LifecycleWriter writer,
    JobEnqueuer jobs,
    VersionPresetReader presets,
    TimeProvider time,
    ILogger<ArtifactService> logger)
{
    /// <summary>
    /// FR-18: a new draft version from edited Markdown (version = latest + 1, parent = latest). With
    /// <paramref name="baseVersion"/>, <c>CONFLICT</c> unless that is still the latest version.
    /// </summary>
    public async Task<Result<Artifact>> CreateDraftAsync(
        Guid projectId, ArtifactType type, string content, int? baseVersion, Guid userId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return ProjectNotFound<Artifact>();
        }

        var allowed = ArtifactRules.CanCreateDraft(type, project.CurrentPhase, project.Status);
        if (allowed.IsFailure)
        {
            return Result.Failure<Artifact>(allowed.Error!);
        }

        // FR-18, FR-29: an edited plan is validated from its aide-plan block like a generated one, and stored re-rendered
        // from it, so the readable step list always matches the steps that will run. An Owner's edit may declare protected
        // paths (§4.6); the re-rendered document must read back to the same steps before it is stored. FR-76: with a preset,
        // its agents and plan rules apply as to a generated plan.
        if (IsPlan(type))
        {
            var agents = (await presets.CurrentAsync(projectId, cancellationToken)).PlanAgents(type);
            var plan = PlanDocument.ParseMarkdown(content, project.MaxFilesPerStep, agents);
            var rendered = plan.IsSuccess ? PlanDocument.RenderChecked(type, plan.Value, project.MaxFilesPerStep, agents) : Result.Failure<string>(plan.Error!);
            if (rendered.IsFailure)
            {
                return Result.Failure<Artifact>(rendered.Error!);
            }

            content = rendered.Value;
        }

        var existing = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);
        var current = ArtifactRules.CheckBaseVersion(baseVersion, existing);
        if (current.IsFailure)
        {
            return Result.Failure<Artifact>(current.Error!);
        }

        var numbering = ArtifactRules.NewDraft(existing);
        var now = time.GetUtcNow();
        await SupersedeOpenDraftsAsync(project, type, numbering, actor, now, cancellationToken);

        var artifact = new Artifact
        {
            ProjectId = projectId,
            Type = type,
            Version = numbering.Version,
            ParentVersion = numbering.ParentVersion,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Draft,
            CheckpointAtCreation = project.Checkpoint,
            ReleaseVersionId = project.CurrentReleaseVersionId ?? Guid.Empty,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        db.Artifacts.Add(artifact);
        db.AddEvent(
            EventTypes.ArtifactCreated,
            projectId,
            new { artifactId = artifact.Id, type, version = artifact.Version, parentVersion = artifact.ParentVersion, author = "user" },
            actor,
            now);
        project.UpdatedAt = now;

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<Artifact>(saved.Error!);
        }

        LogDraftCreated(logger, projectId, type, artifact.Version);
        return Result.Success(artifact);
    }

    /// <summary>
    /// FR-58, D-V2-15: restore <paramref name="version"/> of <paramref name="type"/> as a new draft with its content and
    /// hash (version = latest + 1, parent = latest, <c>restored_from_version</c> = the chosen version). The chosen version
    /// is re-hashed first (FR-20). <see cref="RestoreRules"/> decides; open drafts of the release version in development
    /// are superseded as for any new draft, <c>ARTIFACT_RESTORED</c> is recorded, and the draft then follows the normal
    /// approval rules (FR-17).
    /// </summary>
    public async Task<Result<Artifact>> RestoreAsync(
        Guid projectId, ArtifactType type, int version, Guid userId, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return ProjectNotFound<Artifact>();
        }

        var chosen = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Type == type && a.Version == version, cancellationToken);
        if (chosen is null)
        {
            return Result.Failure<Artifact>(new DomainError(ErrorCodes.NotFound, $"Version {version} of the {type.ToWire()} was not found."));
        }

        var chosenIntegrity = await integrity.CheckAsync(chosen, cancellationToken);
        var existing = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);
        var decided = RestoreRules.Check(new RestoreFacts(
            type,
            project.CurrentPhase,
            project.Status,
            project.DevelopmentVersion is null ? null : project.CurrentReleaseVersionId,
            chosen.ReleaseVersionId,
            chosen.Version,
            chosenIntegrity,
            existing.Max(a => a.Version)));
        if (decided.IsFailure)
        {
            return Result.Failure<Artifact>(decided.Error!);
        }

        var numbering = ArtifactRules.NewDraft(existing);
        var now = time.GetUtcNow();
        await SupersedeOpenDraftsAsync(project, type, numbering, actor, now, cancellationToken);

        var artifact = new Artifact
        {
            ProjectId = projectId,
            Type = type,
            Version = decided.Value.Version,
            ParentVersion = decided.Value.ParentVersion,
            RestoredFromVersion = decided.Value.RestoredFromVersion,
            Content = chosen.Content,
            ContentHash = chosen.ContentHash,
            Status = ArtifactStatus.Draft,
            CheckpointAtCreation = project.Checkpoint,
            ReleaseVersionId = chosen.ReleaseVersionId,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        db.Artifacts.Add(artifact);
        db.AddEvent(
            EventTypes.ArtifactRestored,
            projectId,
            new
            {
                artifactId = artifact.Id,
                type,
                version = artifact.Version,
                parentVersion = artifact.ParentVersion,
                restoredFromVersion = artifact.RestoredFromVersion,
                restoredFromArtifactId = chosen.Id,
            },
            actor,
            now);
        project.UpdatedAt = now;

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<Artifact>(saved.Error!);
        }

        LogRestored(logger, projectId, type, chosen.Version, artifact.Version);
        return Result.Success(artifact);
    }

    /// <summary>
    /// §4.10, D-V2-3: a new draft supersedes the open drafts of the current release version only; a closed version's
    /// drafts are history. Version numbers stay project-wide.
    /// </summary>
    private async Task SupersedeOpenDraftsAsync(
        Project project, ArtifactType type, NewDraftPlan numbering, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (numbering.SupersededVersions.Count == 0)
        {
            return;
        }

        var drafts = await db.Artifacts
            .Where(a => a.ProjectId == project.Id && a.ReleaseVersionId == project.CurrentReleaseVersionId && a.Type == type && numbering.SupersededVersions.Contains(a.Version))
            .ToListAsync(cancellationToken);
        foreach (var draft in drafts.Where(d => ArtifactRules.CanTransition(d.Status, ArtifactStatus.Superseded)))
        {
            draft.Status = ArtifactStatus.Superseded;
            db.AddEvent(
                EventTypes.ArtifactSuperseded,
                project.Id,
                new { artifactId = draft.Id, type, version = draft.Version, supersededByVersion = numbering.Version },
                actor,
                now);
        }
    }

    /// <summary>
    /// FR-17, FR-19: approve a draft → locked (approver and time recorded), then the §4.4 cascade: downstream versions
    /// invalidated, steps reset, the phase falls back through the state machine, stale gates are cancelled, and queued
    /// execution jobs are cancelled when steps were reset. Approving an optimisation plan during review starts the review
    /// cycle (review → execution) when its conditions hold. Approving a locked artifact is a no-op.
    /// </summary>
    public async Task<Result<Artifact>> ApproveAsync(Guid projectId, Guid artifactId, string? comment, Guid userId, string actor, CancellationToken cancellationToken)
    {
        var checkedArtifact = await CheckedAsync(projectId, artifactId, cancellationToken);
        if (checkedArtifact is null)
        {
            return ArtifactNotFound();
        }

        var decided = ArtifactRules.Approve(checkedArtifact.Status);
        if (decided.IsFailure)
        {
            return Result.Failure<Artifact>(decided.Error!);
        }

        if (!decided.Value.Changed)
        {
            return Result.Success(checkedArtifact);
        }

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        if (InOtherVersion(project, checkedArtifact) is { } baseline)
        {
            return Result.Failure<Artifact>(baseline);
        }

        // §4.10: the cascade works on the release version in development only; earlier versions' artifacts are baseline
        // inputs and are never invalidated.
        var versionId = checkedArtifact.ReleaseVersionId;
        var artifacts = await db.Artifacts.Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId).ToListAsync(cancellationToken);
        var artifact = artifacts.Single(a => a.Id == artifactId);
        decided = ArtifactRules.Approve(artifact.Status);
        if (decided.IsFailure || !decided.Value.Changed)
        {
            return decided.IsFailure ? Result.Failure<Artifact>(decided.Error!) : Result.Success(artifact);
        }

        // A draft written after the version the project was rolled back to is discarded work (the rollback's own
        // invalidation and the project's xmin normally catch it; this closes any gap).
        var current = ArtifactRules.EnsureNotStale(artifact.CheckpointAtCreation, project.Checkpoint);
        if (current.IsFailure)
        {
            return Result.Failure<Artifact>(current.Error!);
        }

        // FR-30: a plan's execution steps are created from its aide-plan block when it is locked (once per plan version),
        // checked against the project's current files-per-step limit.
        var steps = await db.ExecutionSteps.Where(s => s.ProjectId == projectId && s.ReleaseVersionId == versionId).ToListAsync(cancellationToken);
        IReadOnlyList<PlanStep> planSteps = [];
        if (IsPlan(artifact.Type) && !steps.Any(s => s.PlanArtifactId == artifact.Id))
        {
            var agents = (await presets.ForVersionAsync(versionId, cancellationToken)).PlanAgents(artifact.Type);
            var parsed = PlanDocument.ParseMarkdown(artifact.Content, project.MaxFilesPerStep, agents);
            if (parsed.IsFailure)
            {
                return Result.Failure<Artifact>(parsed.Error!);
            }

            planSteps = parsed.Value;
        }

        // Stored at PostgreSQL's precision (1 µs), so this response and later reads agree.
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % EventLog.Step.Ticks));
        var cleanComment = Clean(comment);
        artifact.Status = decided.Value.NewStatus;
        artifact.ApprovedBy = userId;
        artifact.ApprovedAt = now;
        project.UpdatedAt = now;

        // §4.4 cascade.
        var cascade = InvalidationCascade.Plan(
            artifact.Id,
            artifact.Type,
            project.CurrentPhase,
            [.. artifacts.Select(a => new ArtifactRef(a.Id, a.Type, a.Status, a.CheckpointAtCreation))],
            [.. steps.Select(s => new StepRef(s.Id, s.PlanArtifactId, s.Status))]);

        db.AddEvent(
            EventTypes.ArtifactApproved,
            projectId,
            new
            {
                artifactId = artifact.Id,
                type = artifact.Type,
                version = artifact.Version,
                comment = cleanComment,
                invalidatedArtifactIds = cascade.InvalidatedArtifactIds,
                resetStepIds = cascade.ResetStepIds,
                createdStepCount = planSteps.Count,
            },
            actor,
            now);

        var invalidated = cascade.InvalidatedArtifactIds.ToHashSet();
        foreach (var downstream in artifacts.Where(a => invalidated.Contains(a.Id)))
        {
            downstream.Status = ArtifactStatus.Invalidated;
            db.AddEvent(
                EventTypes.ArtifactInvalidated,
                projectId,
                new
                {
                    artifactId = downstream.Id,
                    type = downstream.Type,
                    version = downstream.Version,
                    cause = "cascade",
                    approvedArtifactId = artifact.Id,
                    approvedType = artifact.Type,
                    approvedVersion = artifact.Version,
                },
                actor,
                now);
        }

        var reset = cascade.ResetStepIds.ToHashSet();
        foreach (var step in steps.Where(s => reset.Contains(s.Id)))
        {
            step.Status = StepStatus.Invalidated;
        }

        var fellBack = false;
        if (cascade.Effect.FallbackPhase is { } fallback)
        {
            var state = new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint);
            var reason = $"{artifact.Type.ToWire()} v{artifact.Version} approved; downstream work invalidated";
            var transition = LifecycleStateMachine.Apply(state, TransitionRequest.Fallback(fallback, reason), LifecycleSnapshot.Empty);
            if (transition.IsFailure)
            {
                return Result.Failure<Artifact>(transition.Error!);
            }

            await writer.ApplyAsync(project, transition.Value, actor, now, cancellationToken);
            fellBack = true;
        }

        await CancelStaleGatesAsync(project, artifact, invalidated, fellBack, actor, now, cancellationToken);
        if (reset.Count > 0)
        {
            await CancelQueuedExecutionAsync(projectId, now, cancellationToken);
        }

        foreach (var planStep in planSteps)
        {
            var step = NewStep(projectId, artifact.ReleaseVersionId, artifact.Id, planStep);
            db.ExecutionSteps.Add(step);
            steps.Add(step);
        }

        if (artifact.Type == ArtifactType.OptimizationPlan && project.CurrentPhase == Phase.Review)
        {
            await StartReviewCycleAsync(project, artifact, steps, actor, now, cancellationToken);
        }

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<Artifact>(saved.Error!);
        }

        LogApproved(logger, projectId, artifact.Type, artifact.Version, invalidated.Count, reset.Count, cascade.Effect.FallbackPhase);
        return Result.Success(artifact);
    }

    /// <summary>
    /// FR-17: reject a draft → rejected with the comment. An agent-produced draft re-runs its producing agent with the
    /// comment as feedback (the job is written in the same transaction); a human-edited draft (FR-18) re-runs nothing.
    /// </summary>
    public async Task<Result<Artifact>> RejectAsync(Guid projectId, Guid artifactId, string? comment, string actor, CancellationToken cancellationToken)
    {
        var checkedArtifact = await CheckedAsync(projectId, artifactId, cancellationToken);
        if (checkedArtifact is null)
        {
            return ArtifactNotFound();
        }

        var authoredByAgent = ArtifactRules.IsAuthoredByAgent(checkedArtifact.CreatedByUserId);
        var decided = ArtifactRules.Reject(checkedArtifact.Type, checkedArtifact.Status, authoredByAgent);
        if (decided.IsFailure)
        {
            return Result.Failure<Artifact>(decided.Error!);
        }

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        if (InOtherVersion(project, checkedArtifact) is { } baseline)
        {
            return Result.Failure<Artifact>(baseline);
        }

        var artifact = await db.Artifacts.FirstAsync(a => a.Id == artifactId, cancellationToken);
        decided = ArtifactRules.Reject(artifact.Type, artifact.Status, authoredByAgent);
        if (decided.IsFailure)
        {
            return Result.Failure<Artifact>(decided.Error!);
        }

        var now = time.GetUtcNow();
        var cleanComment = Clean(comment);
        artifact.Status = decided.Value.NewStatus;
        artifact.RejectionComment = cleanComment;
        project.UpdatedAt = now;

        Guid? jobId = null;
        if (decided.Value.RerunAgent is { } agent)
        {
            var job = await jobs.EnqueueAsync(
                JobTypes.ForAgent(agent),
                new LifecycleJobPayload(projectId, project.Checkpoint, LifecycleJobPayload.ArtifactRejected, cleanComment, ArtifactId: artifact.Id),
                projectId,
                cancellationToken,
                now);
            jobId = job.Id;
        }

        db.AddEvent(
            EventTypes.ArtifactRejected,
            projectId,
            new { artifactId = artifact.Id, type = artifact.Type, version = artifact.Version, comment = cleanComment, rerunAgent = decided.Value.RerunAgent, jobId },
            actor,
            now);

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<Artifact>(saved.Error!);
        }

        LogRejected(logger, projectId, artifact.Type, artifact.Version, decided.Value.RerunAgent is not null);
        return Result.Success(artifact);
    }

    /// <summary>The artifact (untracked) after its FR-20 integrity check; a corrupted one comes back as <c>corrupted</c>.</summary>
    private async Task<Artifact?> CheckedAsync(Guid projectId, Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId && a.ProjectId == projectId, cancellationToken);
        if (artifact is not null)
        {
            await integrity.CheckAsync(artifact, cancellationToken);
        }

        return artifact;
    }

    private async Task CancelStaleGatesAsync(
        Project project, Artifact approved, IReadOnlyCollection<Guid> invalidated, bool fellBack, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await db.ApprovalGates
            .Where(g => g.ProjectId == project.Id && g.Status == GateStatus.Pending)
            .ToListAsync(cancellationToken);
        var stale = GateRules.StaleAfterArtifactApproval(
            approved.Id,
            approved.Type,
            invalidated,
            fellBack,
            [.. pending.Select(g => new PendingGateRef(g.Id, g.Type, g.ArtifactId))]).ToHashSet();
        foreach (var gate in pending.Where(g => stale.Contains(g.Id)))
        {
            gate.Status = GateRules.Cancel(gate.Status);
            gate.DecidedAt = now;
            gate.Comment = $"Cancelled: {approved.Type.ToWire()} v{approved.Version} was approved.";
            db.AddEvent(
                EventTypes.GateCancelled,
                project.Id,
                new { gateId = gate.Id, type = gate.Type, cause = "artifact_approved", artifactId = approved.Id, artifactType = approved.Type, artifactVersion = approved.Version },
                actor,
                now);
        }
    }

    /// <summary>Queued execution jobs would run steps the cascade just reset; running ones finish and find them invalidated.</summary>
    private async Task CancelQueuedExecutionAsync(Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = await db.Jobs
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.Execution && j.Status == JobStatus.Queued)
            .ToListAsync(cancellationToken);
        foreach (var job in queued)
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }
    }

    /// <summary>
    /// Decisions (review cycle): approving the optimisation plan during review applies review → execution through the
    /// state machine when its conditions hold (review_required or escalation continue, the plan is the latest review's and
    /// has steps to run, working copy).
    /// </summary>
    private async Task StartReviewCycleAsync(
        Project project, Artifact plan, IReadOnlyList<ExecutionStep> steps, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only the latest review's plan (its draft or a later version of it) starts the cycle; its new steps are all to run.
        var ofCycle = await snapshots.IsCyclePlanAsync(project, plan, cancellationToken);
        var snapshot = await snapshots.LoadAsync(project, cancellationToken) with
        {
            OptimizationPlanLocked = ofCycle,
            OptimizationPlanStepCount = ofCycle
                ? steps.Count(s => s.PlanArtifactId == plan.Id && s.Status is not StepStatus.Invalidated and not StepStatus.Completed)
                : 0,
        };
        var state = new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint);
        var cycle = LifecycleStateMachine.Apply(
            state,
            TransitionRequest.ReviewCycle($"optimization_plan v{plan.Version} approved: re-entering execution"),
            snapshot);
        if (cycle.IsSuccess)
        {
            await writer.ApplyAsync(project, cycle.Value, actor, now, cancellationToken);

            // FR-40: the optimisation steps run on the same branch straight away (unless execution is paused, FR-37).
            if (!project.ExecutionPaused)
            {
                await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(project.Id, ExecutionJobPayload.ReviewCycle), project.Id, cancellationToken, now);
            }
        }
    }

    /// <summary>
    /// §4.10: only artifacts of the release version in development can be decided. Those of earlier versions are baseline
    /// inputs (read-only, never approvable), and a released project has nothing in development: <c>WRONG_PHASE</c>.
    /// </summary>
    private static DomainError? InOtherVersion(Project project, Artifact artifact) =>
        project.DevelopmentVersion is not null && artifact.ReleaseVersionId == project.CurrentReleaseVersionId
            ? null
            : new DomainError(
                ErrorCodes.WrongPhase,
                $"{artifact.Type.ToWire()} v{artifact.Version} belongs to a released version and is read-only. Start a new version to change it.");

    private static bool IsPlan(ArtifactType type) => type is ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan;

    /// <summary>FR-30: a <c>pending</c> execution step of a locked plan version.</summary>
    private static ExecutionStep NewStep(Guid projectId, Guid releaseVersionId, Guid planArtifactId, PlanStep step) => new()
    {
        ProjectId = projectId,
        ReleaseVersionId = releaseVersionId,
        PlanArtifactId = planArtifactId,
        StepNumber = step.Number,
        Title = step.Title,
        Task = step.Task,
        DeclaredFiles = step.Files,
        Dependencies = step.Dependencies,
        UserInstructions = step.UserInstructions,

        // FR-76: the agent that runs the step (generalist without a preset) and the requirements it covers.
        AgentKey = step.Agent,
        Covers = step.Covers,
        Status = StepStatus.Pending,
        Attempt = 0,
    };

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    private static Result<T> ProjectNotFound<T>() =>
        Result.Failure<T>(new DomainError(ErrorCodes.NotFound, "The project was not found."));

    private static Result<Artifact> ArtifactNotFound() =>
        Result.Failure<Artifact>(new DomainError(ErrorCodes.NotFound, "The artifact was not found."));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: draft {ArtifactType} v{Version} created")]
    private static partial void LogDraftCreated(ILogger logger, Guid projectId, ArtifactType artifactType, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {ArtifactType} v{RestoredFromVersion} restored as draft v{Version}")]
    private static partial void LogRestored(ILogger logger, Guid projectId, ArtifactType artifactType, int restoredFromVersion, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {ArtifactType} v{Version} approved ({Invalidated} artifacts invalidated, {Reset} steps reset, fallback {Fallback})")]
    private static partial void LogApproved(ILogger logger, Guid projectId, ArtifactType artifactType, int version, int invalidated, int reset, Phase? fallback);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {ArtifactType} v{Version} rejected (agent re-run: {Rerun})")]
    private static partial void LogRejected(ILogger logger, Guid projectId, ArtifactType artifactType, int version, bool rerun);
}
