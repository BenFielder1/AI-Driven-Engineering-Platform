using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Artifacts;
using Tevrin.Domain.Common;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Pipelines;

/// <summary>
/// FR-292 (switch-over): every lifecycle decision in the app. Decisions run only on the engine
/// (<see cref="PipelineStateMachine"/> through <see cref="SdlcMachine"/>); with no valid SDLC definition they throw, as every
/// save touching a project does (D-0.7.0-28, fail closed).
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
/// <para>
/// <b>Pinning (FR-277).</b> A move decides with the definition the project's current run is pinned to
/// (<c>pipeline_runs.definition_hash</c>), resolved by <see cref="PipelineDefinitionResolver"/>: the registered definition,
/// else the stored canonical JSON. When this build can't run it, <see cref="PinnedAsync"/> fails with 409
/// <c>DEFINITION_UNAVAILABLE</c> and logs the missing names; reads never resolve it. A new run (project creation, a new
/// version) starts on <see cref="Definition"/>, the registry's current definition.
/// </para>
/// The review-loop position is <c>projects.review_cycle</c>, never the engine's iteration count (M58c review notes).
/// </summary>
public sealed partial class SdlcLifecycle(PipelineRegistryHost registries, PipelineDefinitionCache definitions, ILogger<SdlcLifecycle> logger)
{
    /// <summary>The 409 message, as the pipeline-run gate route's.</summary>
    public const string DefinitionUnavailableMessage = "This run uses a pipeline definition this version of Tevrin can't run.";

    /// <summary>The registry's current valid SDLC definition, which new runs are pinned to (throws when there is none).</summary>
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

    /// <summary>A position on the current definition (a run not stored yet, or pure decisions in tests).</summary>
    public SdlcRun OnCurrent(LifecycleState state)
    {
        var current = SdlcRunDefinition.Current(registries);
        var definition = current.Definition
            ?? throw new InvalidOperationException($"The '{SdlcPack.PipelineId}' pipeline definition is not valid.");
        return new SdlcRun(state, definition, null, current.Hash!);
    }

    /// <summary>The project's lifecycle position, read from its run (see the class remarks for when the copy is used).</summary>
    public async Task<LifecycleState> StateAsync(TevrinDbContext db, Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        return StateOf(project, Unsaved(db, project) ? null : await ReadRunAsync(db, project, cancellationToken));
    }

    /// <summary>
    /// FR-277: the project's lifecycle position and the definition its current run is pinned to, for a move. A project with
    /// no run yet (created in this unit of work, or before runs) decides with the current definition, which the run sync
    /// pins its new run to. Fails with <c>DEFINITION_UNAVAILABLE</c> when the pinned definition is neither registered nor
    /// stored, or names a guard, handler or tool this build doesn't register (the missing names are logged).
    /// </summary>
    public async Task<Result<SdlcRun>> PinnedAsync(TevrinDbContext db, Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);

        // One run-row read, as before pinning: it carries the state and the hash (§8: a move adds at most one run read).
        var run = db.Entry(project).State == EntityState.Added ? null : await ReadRunAsync(db, project, cancellationToken);
        var state = StateOf(project, Unsaved(db, project) ? null : run);
        if (run is null)
        {
            return Result.Success(OnCurrent(state));
        }

        // A registered hash (every run started by this build) resolves without a database read.
        var resolved = await new PipelineDefinitionResolver(registries, db, definitions).ResolveAsync(run.DefinitionHash, cancellationToken);
        if (resolved is not { Available: true })
        {
            var missing = resolved is null
                ? "(the definition is neither registered nor stored)"
                : registries.Registry is { } registry
                    ? string.Join(", ", PipelineDefinitionResolver.MissingNames(registry, resolved.Definition))
                    : "(no pipeline registry)";
            LogDefinitionUnavailable(logger, project.Id, run.Id, run.DefinitionHash, missing);
            return Result.Failure<SdlcRun>(new DomainError(ErrorCodes.DefinitionUnavailable, DefinitionUnavailableMessage) { Details = [missing] });
        }

        return Result.Success(new SdlcRun(state, resolved.Definition, run.Id, run.DefinitionHash));
    }

    /// <summary>
    /// <paramref name="run"/> (already pinned in this unit of work) after the moves applied since: the project's copy once
    /// its lifecycle columns are set, as <see cref="StateAsync"/> would return, so a follow-up move reads the run only once.
    /// </summary>
    public static SdlcRun Refresh(TevrinDbContext db, SdlcRun run, Project project)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(project);
        return Unsaved(db, project) ? run with { State = CopyOf(project) } : run;
    }

    /// <summary>Applies <paramref name="move"/> to <paramref name="run"/> with its pinned definition.</summary>
    public static Result<PhaseTransition> Apply(SdlcRun run, Project project, Transition move, LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(run);
        return SdlcMachine.Apply(run.State, move, snapshot, LoopOf(project), run.Definition);
    }

    /// <summary>Applies <paramref name="move"/> to <paramref name="project"/>'s run (<c>DEFINITION_UNAVAILABLE</c> when its
    /// pinned definition can't run, FR-277).</summary>
    public async Task<Result<PhaseTransition>> ApplyAsync(
        TevrinDbContext db, Project project, Transition move, LifecycleSnapshot snapshot, CancellationToken cancellationToken)
    {
        var run = await PinnedAsync(db, project, cancellationToken);
        return run.IsFailure ? Result.Failure<PhaseTransition>(run.Error!) : Apply(run.Value, project, move, snapshot);
    }

    /// <summary>FR-12: advance <paramref name="project"/>'s <paramref name="run"/>, or name the gate it needs.</summary>
    public static Result<AdvanceOutcome> RequestAdvance(SdlcRun run, Project project, LifecycleSnapshot snapshot, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return SdlcMachine.RequestAdvance(run.State, snapshot, LoopOf(project), run.Definition, reason);
    }

    /// <summary>A new project's first run (FR-178), on the current definition.</summary>
    public Result<LifecycleState> Start(bool infersStack, bool baseHasCode) => SdlcMachine.Start(infersStack, baseHasCode, Definition);

    /// <summary>§4.10, FR-60, FR-186: start a new release version of <paramref name="project"/> entering
    /// <paramref name="entering"/>; the new version's run starts on the current definition.</summary>
    public async Task<Result<PhaseTransition>> NewVersionAsync(
        TevrinDbContext db, Project project, Phase entering, LifecycleSnapshot snapshot, string? reason, CancellationToken cancellationToken) =>
        SdlcMachine.NewVersion(await StateAsync(db, project, cancellationToken), entering, snapshot, Definition, reason);

    /// <summary>§4.4, §4.54: approving <paramref name="approved"/> in <paramref name="run"/>'s current phase.</summary>
    public static InvalidationEffect CascadeEffect(string approved, SdlcRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return SdlcMachine.CascadeEffect(approved, run.State.Phase, run.Definition);
    }

    /// <summary>The phase an advance from <paramref name="run"/>'s phase enters, or <c>null</c> from the last.</summary>
    public static Phase? Next(SdlcRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return SdlcStages.Next(run.Definition, run.State.Phase);
    }

    /// <summary>A project whose lifecycle columns this unit of work has set (or a new or detached project): its copy decides.</summary>
    private static bool Unsaved(TevrinDbContext db, Project project)
    {
        var entry = db.Entry(project);
        return entry.State is EntityState.Added or EntityState.Detached
            || entry.Property(p => p.CurrentPhase).IsModified
            || entry.Property(p => p.Status).IsModified
            || entry.Property(p => p.Checkpoint).IsModified;
    }

    /// <summary>The current release version's run row (no tracking), or <c>null</c> when there is none yet.</summary>
    private static async Task<RunRow?> ReadRunAsync(TevrinDbContext db, Project project, CancellationToken cancellationToken)
    {
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken);
        if (versionId is not { } subject)
        {
            return null;
        }

        return await db.PipelineRuns.AsNoTracking()
            .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == subject)
            .Select(r => new RunRow(r.Id, r.CurrentStage, r.PackStatus, r.Checkpoint, r.DefinitionHash))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The position from <paramref name="run"/>, or the project's copy (no run, an unsaved move, or a disagreement).</summary>
    private LifecycleState StateOf(Project project, RunRow? run)
    {
        var copy = CopyOf(project);
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

    private sealed record RunRow(Guid Id, string CurrentStage, string PackStatus, int Checkpoint, string DefinitionHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run of project {ProjectId} is at {RunStage}/{RunStatus} checkpoint {RunCheckpoint}, the project at {ProjectPhase}/{ProjectStatus} checkpoint {ProjectCheckpoint}; the project's copy decides")]
    private static partial void LogRunDisagrees(
        ILogger logger, Guid projectId, string runStage, string runStatus, int runCheckpoint, string projectPhase, string projectStatus, int projectCheckpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DEFINITION_UNAVAILABLE: project {ProjectId} run {PipelineRunId} is pinned to definition {DefinitionHash}, which this build can't run; missing: {MissingNames}")]
    private static partial void LogDefinitionUnavailable(ILogger logger, Guid projectId, Guid pipelineRunId, string definitionHash, string missingNames);
}

/// <summary>FR-277: a project's lifecycle position and the definition its current run is pinned to (<see cref="RunId"/> is
/// <c>null</c> before the run is stored).</summary>
public sealed record SdlcRun(LifecycleState State, PipelineDefinition Definition, Guid? RunId, string DefinitionHash);
