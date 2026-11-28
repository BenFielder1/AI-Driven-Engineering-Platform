using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Features.Release;
using Tevrin.Api.Pipelines;
using Tevrin.Domain.Common;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Agents;

/// <summary>FR-318: where an SDLC agent runs: the project's current pipeline run and the agent's stage in its pinned definition.</summary>
public abstract record SdlcStageTarget
{
    /// <summary>
    /// The project has no pipeline run, even after <see cref="SdlcStageLocator"/> created its missing one (FR-294's lazy
    /// backfill): it has no release version.
    /// </summary>
    public sealed record NoRun : SdlcStageTarget;

    /// <summary>
    /// The run is pinned to a definition this build can't run (FR-277, D-0.7.0-37): neither registered nor stored, or
    /// stored but naming a guard, handler or tool this build doesn't register (<paramref name="Missing"/>).
    /// </summary>
    public sealed record Unavailable(Guid RunId, string DefinitionHash, string Missing) : SdlcStageTarget;

    /// <summary>The pinned definition has no stage for the agent (D-0.7.0-42 binds every SDLC agent, so this is an error).</summary>
    public sealed record NoStage(Guid RunId) : SdlcStageTarget;

    public sealed record Found(Guid RunId, string StageId) : SdlcStageTarget;
}

/// <summary>FR-318: finds an agent's <see cref="SdlcStageTarget"/> (a seam, so the job's rules are tested without a database).</summary>
public interface ISdlcStageLocator
{
    Task<SdlcStageTarget> LocateAsync(Guid projectId, string agent, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISdlcStageLocator"/> on the database (one run-row read) and <see cref="PipelineDefinitionResolver"/>, as
/// <see cref="SdlcLifecycle.PinnedAsync"/> resolves a move's definition: registered, or stored with every name registered.
/// FR-320: a project that predates runs gets its missing run here, for that project only, as the save interceptor's lazy
/// backfill creates it (FR-294: superseded runs closed, ended stage executions closed, missing runs created, pinned to the
/// current SDLC definition). Rows aren't linked: a raw update would change the <c>xmin</c> of rows the job may hold.
/// </summary>
public sealed partial class SdlcStageLocator(
    TevrinDbContext db, PipelineDefinitionResolver definitions, PipelineRegistry registry, TimeProvider time, ILogger<SdlcStageLocator> logger) : ISdlcStageLocator
{
    public async Task<SdlcStageTarget> LocateAsync(Guid projectId, string agent, CancellationToken cancellationToken)
    {
        if (await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is not { } subject)
        {
            return new SdlcStageTarget.NoRun();
        }

        var run = await RunAsync(subject, cancellationToken);
        if (run is null && registry.TryGet(SdlcPack.PipelineId, out var sdlc))
        {
            var now = time.GetUtcNow();
            await PipelineRunBackfill.CloseSupersededAsync(db, sdlc, projectId, null, now, cancellationToken);
            await StageExecutionWriter.CloseEndedAsync(db, projectId, now, cancellationToken);
            var created = await PipelineRunBackfill.CreateRunsAsync(db, sdlc, projectId, now, cancellationToken);
            if (created > 0)
            {
                LogCreated(logger, projectId, created);
            }

            run = await RunAsync(subject, cancellationToken);
        }

        if (run is null)
        {
            return new SdlcStageTarget.NoRun();
        }

        // FR-310, FR-320: runs a 0.6.1 build stored for this version (a rolling deploy) have no pipeline run; link them, so
        // a retried job's run belongs to this one and resumes. No agent run is tracked yet, so no xmin moves under the job.
        var runId = run.Id;
        await db.AgentRuns.Where(Unlinked(projectId, subject))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PipelineRunId, (Guid?)runId), cancellationToken);

        var resolved = await definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
        if (resolved is not { Available: true })
        {
            var missing = resolved is null
                ? "(the definition is neither registered nor stored)"
                : string.Join(", ", PipelineDefinitionResolver.MissingNames(registry, resolved.Definition));
            return new SdlcStageTarget.Unavailable(run.Id, run.DefinitionHash, missing);
        }

        return StageOf(resolved.Definition, agent) is { } stageId
            ? new SdlcStageTarget.Found(run.Id, stageId)
            : new SdlcStageTarget.NoStage(run.Id);
    }

    private Task<RunRow?> RunAsync(Guid subject, CancellationToken cancellationToken) =>
        db.PipelineRuns.AsNoTracking()
            .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == subject)
            .Select(r => new RunRow(r.Id, r.DefinitionHash))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>FR-320: the project's agent runs of <paramref name="versionId"/> that no pipeline run owns (0.6.1's).</summary>
    public static Expression<Func<AgentRun, bool>> Unlinked(Guid projectId, Guid versionId) =>
        r => r.ProjectId == projectId && r.ReleaseVersionId == versionId && r.PipelineRunId == null;

    private sealed record RunRow(Guid Id, string DefinitionHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} had no pipeline run for its current version; the backfill created {Created} run(s) (FR-294)")]
    private static partial void LogCreated(ILogger logger, Guid projectId, int created);

    /// <summary>D-0.7.0-42: the stage whose primary agent, or one of whose <c>agents:</c> bindings, is <paramref name="agent"/>.</summary>
    public static string? StageOf(PipelineDefinition definition, string agent)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stages.FirstOrDefault(s => s.ForAgent(agent) is not null)?.Id;
    }
}

/// <summary>
/// FR-318–FR-320: runs an SDLC agent's stage on the engine runner (<see cref="AgentStageCoordinator"/> with
/// <see cref="SdlcAgentExtension"/>), the only SDLC agent path, for the project's current pipeline run and the stage that
/// binds the agent. The run id comes from the job (FR-310), a rejected reply is re-run once, still rejected fails the job
/// with the check's code, and a conflict while saving the draft fails the job transiently (<c>CONFLICT</c>; the retry
/// reuses the succeeded run). The job fails permanently, without calling the provider and with an error log, when:
/// <list type="bullet">
/// <item>the run is pinned to a definition this build can't run: <c>DEFINITION_UNAVAILABLE</c> (FR-277), as the SDLC's
/// other moves;</item>
/// <item>the pinned definition binds no stage to the agent: <c>DEFINITION_UNAVAILABLE</c> too (this build can't run the
/// agent on that definition);</item>
/// <item>the project still has no run once <see cref="SdlcStageLocator"/> created its missing one (no release version):
/// <c>INVALID_STATE</c>.</item>
/// </list>
/// </summary>
public sealed partial class SdlcAgentStageJob(
    AgentStageCoordinator coordinator,
    SdlcAgentExtension extension,
    ISdlcStageLocator locator,
    ILogger<SdlcAgentStageJob> logger)
{
    /// <summary>The message of a job refused because the project has no pipeline run.</summary>
    public const string NoRunMessage = "The project has no pipeline run for its current version, so its agents can't run.";

    /// <summary>Runs <paramref name="request"/> (with its corrective re-run if needed); returns the id of the run that produced the draft.</summary>
    public async Task<Guid> RunAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken) =>
        (await RunCoreAsync(request, spec, trigger, cancellationToken)).Id;

    /// <summary>
    /// FR-38: runs <paramref name="request"/> (with its corrective re-run if needed) without storing a draft
    /// (<see cref="ArtifactAgentSpec.StoresDraft"/> off); returns the accepted run, as 0.6.1's runner returned it.
    /// </summary>
    public async Task<AgentRunOutcome> RunReplyAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return SdlcAgentRuns.Outcome(await RunCoreAsync(request, spec with { StoresDraft = false }, trigger, cancellationToken));
    }

    private async Task<AgentRun> RunCoreAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        switch (await locator.LocateAsync(request.ProjectId, request.Agent, cancellationToken))
        {
            case SdlcStageTarget.Found found:
                return await RunOnEngineAsync(request, spec, found, trigger, cancellationToken);
            case SdlcStageTarget.Unavailable unavailable:
                LogUnavailable(logger, request.Agent, unavailable.RunId, unavailable.DefinitionHash, unavailable.Missing);
                throw Unavailable(unavailable.Missing);
            case SdlcStageTarget.NoStage noStage:
                LogNoStage(logger, request.Agent, noStage.RunId);
                throw Unavailable($"stage for agent {request.Agent}");
            default:
                LogNoRun(logger, request.Agent, request.ProjectId);
                throw new JobFailedException(NoRunMessage, transient: false, ErrorCodes.InvalidState);
        }
    }

    private static JobFailedException Unavailable(string missing) =>
        ReleaseMoveFailure.Of(new DomainError(ErrorCodes.DefinitionUnavailable, SdlcLifecycle.DefinitionUnavailableMessage)
        {
            Details = [missing],
        });

    private async Task<AgentRun> RunOnEngineAsync(AgentRunRequest request, ArtifactAgentSpec spec, SdlcStageTarget.Found target, string trigger, CancellationToken cancellationToken)
    {
        // Each attempt opens its own log scope and span with its run id (SdlcAgentExtension.ObserveAttempt).
        // The job's run key is kept: 0.6.1's build fix keys its runs by the check (build-fix-<check>-<attempt>).
        var call = new SdlcAgentStageCall(request, spec);
        AgentStageResult result;
        try
        {
            result = await coordinator.RunAsync(call.ToStageRequest(target.RunId, target.StageId, trigger), cancellationToken);
        }
        catch (DbUpdateException ex) when (extension.SavingDraft)
        {
            // The project changed (e.g. a rollback or phase fallback), a concurrent writer took the version or changed the
            // superseded draft: retry. The retry re-uses the succeeded run and re-checks whether the draft is still allowed.
            throw new JobFailedException($"The {spec.Type} draft collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }

        result.ThrowIfFailed();
        return result.Last.AgentRun;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Project {ProjectId} has no pipeline run for its current version; {Agent} refused with INVALID_STATE")]
    private static partial void LogNoRun(ILogger logger, string agent, Guid projectId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline run {PipelineRunId}'s definition has no stage for {Agent}; refused with DEFINITION_UNAVAILABLE")]
    private static partial void LogNoStage(ILogger logger, string agent, Guid pipelineRunId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline run {PipelineRunId} is pinned to definition {DefinitionHash}, which this build can't run (missing: {Missing}); {Agent} refused with DEFINITION_UNAVAILABLE")]
    private static partial void LogUnavailable(ILogger logger, string agent, Guid pipelineRunId, string definitionHash, string missing);
}
