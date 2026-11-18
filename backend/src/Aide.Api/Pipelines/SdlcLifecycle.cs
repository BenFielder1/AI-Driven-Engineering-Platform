using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Pipelines;

/// <summary>
/// FR-292 (switch-over): every lifecycle decision in the app. Decisions run only on the engine
/// (<see cref="PipelineStateMachine"/> through <see cref="SdlcMachine"/>) with the registry's SDLC definition; with no valid
/// definition they throw, as every save touching a project does (D-0.7.0-28, fail closed).
/// <para>
/// <b>Where state lives.</b> The run (<c>pipeline_runs</c>) is the source of truth: <see cref="StateAsync"/> reads the stage,
/// pack status and checkpoint from the current release version's run row. Writers keep writing the project's
/// <c>current_phase</c>, <c>status</c> and <c>checkpoint</c> (<c>LifecycleWriter</c>), and <see cref="PipelineRunSyncInterceptor"/>
/// copies them onto the run in the same transaction, so the project columns stay the 0.6.1 copy for queries and DTOs.
/// Two cases use the project's copy instead: a move already applied in this unit of work (not saved yet, so the run gets it
/// at the save), and a project row written outside the app's unit of work after the run (a 0.6.1 replica during an
/// overlapping deploy, the dev seed, a direct SQL write): the newer project copy wins, as in the start-up catch-up
/// (D-0.7.0-28). The disagreement is logged (Information: a concurrent writer makes it normal) and nothing is written; the
/// run sync re-writes the run from the project in the same transaction as the next save that changes the project.
/// </para>
/// The review-loop position is <c>projects.review_cycle</c>, never the engine's iteration count (M58c review notes).
/// </summary>
public sealed partial class SdlcLifecycle(PipelineRegistryHost registries, ILogger<SdlcLifecycle> logger)
{
    /// <summary>The registry's valid SDLC definition (throws when there is none).</summary>
    public PipelineDefinition Definition => SdlcRunDefinition.Current(registries).Definition
        ?? throw new InvalidOperationException($"The '{SdlcPack.PipelineId}' pipeline definition is not valid.");

    /// <summary>The review-loop position of <paramref name="project"/> (D-0.7.0-27, D-0.7.0-30, FR-14).</summary>
    public static ReviewLoopState LoopOf(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ReviewLoopState.Of(project.ReviewCycle, project.MaxReviewCycles, project.ExtraReviewCycles);
    }

    /// <summary>The project's columns as a lifecycle state (its copy of the run).</summary>
    public static LifecycleState CopyOf(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint);
    }

    /// <summary>The project's lifecycle position, read from its run (see the class remarks for when the copy is used).</summary>
    public async Task<LifecycleState> StateAsync(AideDbContext db, Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        var copy = CopyOf(project);
        var entry = db.Entry(project);
        if (entry.State is EntityState.Added or EntityState.Detached
            || entry.Property(p => p.CurrentPhase).IsModified
            || entry.Property(p => p.Status).IsModified
            || entry.Property(p => p.Checkpoint).IsModified)
        {
            return copy;
        }

        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken);
        if (versionId is not { } subject)
        {
            return copy;
        }

        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == subject)
            .Select(r => new { r.Id, r.CurrentStage, r.PackStatus, r.Checkpoint })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null
            || !EnumStrings.TryParse<Phase>(run.CurrentStage, out var stage)
            || !EnumStrings.TryParse<ProjectStatus>(run.PackStatus, out var status))
        {
            // A project that predates runs: the lazy backfill creates its run from the project at the next save.
            return copy;
        }

        var state = new LifecycleState(stage, status, run.Checkpoint);
        if (state == copy)
        {
            return state;
        }

        // The run and the project copy disagree: a write outside the dual write, or a concurrent writer that committed after
        // this project was loaded. The project copy wins for this decision; nothing is written here (a write could put stale
        // state on the run). The run sync re-writes the run from the project in the same transaction as the next save that
        // changes the project, and the project's xmin rejects a decision made on a stale copy.
        var (projectPhase, projectStatus) = (copy.Phase.ToWire(), copy.Status.ToWire());
        LogRunDisagrees(logger, project.Id, run.CurrentStage, run.PackStatus, run.Checkpoint, projectPhase, projectStatus, copy.Version);
        return copy;
    }

    /// <summary>Applies <paramref name="move"/> to the run at <paramref name="state"/>.</summary>
    public Result<PhaseTransition> Apply(LifecycleState state, Project project, Transition move, LifecycleSnapshot snapshot) =>
        SdlcMachine.Apply(state, move, snapshot, LoopOf(project), Definition);

    /// <summary>Applies <paramref name="move"/> to <paramref name="project"/>'s run.</summary>
    public async Task<Result<PhaseTransition>> ApplyAsync(
        AideDbContext db, Project project, Transition move, LifecycleSnapshot snapshot, CancellationToken cancellationToken) =>
        Apply(await StateAsync(db, project, cancellationToken), project, move, snapshot);

    /// <summary>FR-12: advance <paramref name="project"/>'s run at <paramref name="state"/>, or name the gate it needs.</summary>
    public Result<AdvanceOutcome> RequestAdvance(LifecycleState state, Project project, LifecycleSnapshot snapshot, string? reason = null) =>
        SdlcMachine.RequestAdvance(state, snapshot, LoopOf(project), Definition, reason);

    /// <summary>A new project's first run (FR-178).</summary>
    public Result<LifecycleState> Start(bool infersStack, bool baseHasCode) => SdlcMachine.Start(infersStack, baseHasCode, Definition);

    /// <summary>§4.10, FR-60, FR-186: start a new release version of <paramref name="project"/> entering <paramref name="entering"/>.</summary>
    public async Task<Result<PhaseTransition>> NewVersionAsync(
        AideDbContext db, Project project, Phase entering, LifecycleSnapshot snapshot, string? reason, CancellationToken cancellationToken) =>
        SdlcMachine.NewVersion(await StateAsync(db, project, cancellationToken), entering, snapshot, Definition, reason);

    /// <summary>§4.4, §4.54: approving <paramref name="approved"/> while in <paramref name="currentPhase"/>.</summary>
    public InvalidationEffect CascadeEffect(string approved, Phase currentPhase) => SdlcMachine.CascadeEffect(approved, currentPhase, Definition);

    /// <summary>The phase an advance from <paramref name="phase"/> enters, or <c>null</c> from the last.</summary>
    public Phase? Next(Phase phase) => SdlcStages.Next(Definition, phase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run of project {ProjectId} is at {RunStage}/{RunStatus} checkpoint {RunCheckpoint}, the project at {ProjectPhase}/{ProjectStatus} checkpoint {ProjectCheckpoint}; the project's copy decides")]
    private static partial void LogRunDisagrees(
        ILogger logger, Guid projectId, string runStage, string runStatus, int runCheckpoint, string projectPhase, string projectStatus, int projectCheckpoint);
}
