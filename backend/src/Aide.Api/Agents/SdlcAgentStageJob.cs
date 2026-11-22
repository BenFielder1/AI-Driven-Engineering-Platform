using Aide.Api.Features.Release;
using Aide.Api.Pipelines;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>FR-318: where an SDLC agent runs: the project's current pipeline run and the agent's stage in its pinned definition.</summary>
public abstract record SdlcStageTarget
{
    /// <summary>The project has no pipeline run yet (it predates runs and wasn't backfilled).</summary>
    public sealed record NoRun : SdlcStageTarget;

    /// <summary>
    /// The run is pinned to a definition this build can't run (FR-277, D-0.7.0-37): neither registered nor stored, or
    /// stored but naming a guard, handler or tool this build doesn't register (<paramref name="Missing"/>).
    /// </summary>
    public sealed record Unavailable(Guid RunId, string DefinitionHash, string Missing) : SdlcStageTarget;

    /// <summary>The pinned definition has no stage for the agent (until D-0.7.0-42 binds every agent).</summary>
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
/// </summary>
public sealed class SdlcStageLocator(AideDbContext db, PipelineDefinitionResolver definitions, PipelineRegistry registry) : ISdlcStageLocator
{
    public async Task<SdlcStageTarget> LocateAsync(Guid projectId, string agent, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var run = versionId is not { } subject
            ? null
            : await db.PipelineRuns.AsNoTracking()
                .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == subject)
                .Select(r => new { r.Id, r.DefinitionHash })
                .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return new SdlcStageTarget.NoRun();
        }

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

    /// <summary>D-0.7.0-42: the stage whose primary agent, or one of whose <c>agents:</c> bindings, is <paramref name="agent"/>.</summary>
    public static string? StageOf(PipelineDefinition definition, string agent)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stages.FirstOrDefault(s => s.ForAgent(agent) is not null)?.Id;
    }
}

/// <summary>
/// FR-318: runs an SDLC document agent's stage on the engine runner (<see cref="AgentStageCoordinator"/> with
/// <see cref="SdlcAgentExtension"/>) in place of <see cref="ArtifactAgentJob"/>, for the project's current pipeline run and
/// the stage whose agent it is. Same contract: the run id comes from the job (FR-310), a rejected reply is re-run once,
/// still rejected fails the job with the check's code, and a conflict while saving the draft fails the job transiently
/// (<c>CONFLICT</c>; the retry reuses the succeeded run). A run pinned to a definition this build can't run fails the job
/// permanently with <c>DEFINITION_UNAVAILABLE</c> (FR-277), as the SDLC's other moves do. Only a project without a
/// pipeline run, or a definition without the agent's stage (logged as a warning), still runs through
/// <see cref="ArtifactAgentJob"/>.
/// </summary>
public sealed partial class SdlcAgentStageJob(
    AgentStageCoordinator coordinator,
    SdlcAgentExtension extension,
    ISdlcStageLocator locator,
    IArtifactAgentJob legacy,
    ILogger<SdlcAgentStageJob> logger)
{
    /// <summary>Runs <paramref name="request"/> (with its corrective re-run if needed); returns the id of the run that produced the draft.</summary>
    public async Task<Guid> RunAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken)
    {
        var (runId, _) = await RunCoreAsync(request, spec, trigger, async () => (await legacy.RunAsync(request, spec, cancellationToken), null), cancellationToken);
        return runId;
    }

    /// <summary>
    /// FR-38: runs <paramref name="request"/> (with its corrective re-run if needed) without storing a draft
    /// (<see cref="ArtifactAgentSpec.StoresDraft"/> off); returns the accepted run, as 0.6.1's runner returned it.
    /// </summary>
    public async Task<AgentRunOutcome> RunReplyAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var reply = spec with { StoresDraft = false };
        var (_, outcome) = await RunCoreAsync(request, reply, trigger, async () => (default, await legacy.RunReplyAsync(request, reply, cancellationToken)), cancellationToken);
        return outcome!;
    }

    private async Task<(Guid RunId, AgentRunOutcome? Outcome)> RunCoreAsync(
        AgentRunRequest request, ArtifactAgentSpec spec, string trigger, Func<Task<(Guid, AgentRunOutcome?)>> legacyRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        switch (await locator.LocateAsync(request.ProjectId, request.Agent, cancellationToken))
        {
            case SdlcStageTarget.Found found:
                var run = await RunOnEngineAsync(request, spec, found, trigger, cancellationToken);
                return (run.Id, AgentRunner.Outcome(run));
            case SdlcStageTarget.Unavailable unavailable:
                LogUnavailable(logger, request.Agent, unavailable.RunId, unavailable.DefinitionHash, unavailable.Missing);
                throw ReleaseMoveFailure.Of(new DomainError(ErrorCodes.DefinitionUnavailable, SdlcLifecycle.DefinitionUnavailableMessage)
                {
                    Details = [unavailable.Missing],
                });
            case SdlcStageTarget.NoStage noStage:
                LogNoStage(logger, request.Agent, noStage.RunId);
                return await legacyRun();
            default:
                LogNoRun(logger, request.Agent);
                return await legacyRun();
        }
    }

    private async Task<AgentRun> RunOnEngineAsync(AgentRunRequest request, ArtifactAgentSpec spec, SdlcStageTarget.Found target, string trigger, CancellationToken cancellationToken)
    {
        // Each attempt opens its own log scope and span with its run id (SdlcAgentExtension.ObserveAttempt).
        // The job's run key is kept: 0.6.1's build fix keys its runs by the check (build-fix-<check>-<attempt>).
        var call = new SdlcAgentStageCall(request with { Check = null }, spec);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "No pipeline run for {Agent} yet; running it on the SDLC runner")]
    private static partial void LogNoRun(ILogger logger, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId}'s definition has no stage for {Agent}; running it on the SDLC runner")]
    private static partial void LogNoStage(ILogger logger, string agent, Guid pipelineRunId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline run {PipelineRunId} is pinned to definition {DefinitionHash}, which this build can't run (missing: {Missing}); {Agent} refused with DEFINITION_UNAVAILABLE")]
    private static partial void LogUnavailable(ILogger logger, string agent, Guid pipelineRunId, string definitionHash, string missing);
}
