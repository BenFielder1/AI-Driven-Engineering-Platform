using Aide.Api.Logging;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-318: runs an SDLC document agent's stage on the engine runner (<see cref="AgentStageCoordinator"/> with
/// <see cref="SdlcAgentExtension"/>) in place of <see cref="ArtifactAgentJob"/>, for the project's current pipeline run and
/// the stage whose agent it is. Same contract: the run id comes from the job (FR-310), a rejected reply is re-run once,
/// still rejected fails the job with the check's code, and a conflict while saving the draft fails the job transiently
/// (<c>CONFLICT</c>; the retry reuses the succeeded run). A project without a pipeline run yet (one that predates them and
/// wasn't backfilled) still runs through <see cref="ArtifactAgentJob"/>.
/// </summary>
public sealed partial class SdlcAgentStageJob(
    AgentStageCoordinator coordinator,
    SdlcAgentExtension extension,
    PipelineRegistry registry,
    AideDbContext db,
    ArtifactAgentJob legacy,
    ILogger<SdlcAgentStageJob> logger)
{
    /// <summary>Runs <paramref name="request"/> (with its corrective re-run if needed); returns the id of the run that produced the draft.</summary>
    public async Task<Guid> RunAsync(AgentRunRequest request, ArtifactAgentSpec spec, string trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        if (await StageOfAsync(request.ProjectId, request.Agent, cancellationToken) is not { } target)
        {
            LogNoRun(logger, request.Agent);
            return await legacy.RunAsync(request, spec, cancellationToken);
        }

        var call = new SdlcAgentStageCall(request with { RunKey = null, Check = null }, spec);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RunId"] = AgentRunRules.RunId(request.Job.Id, null),
            ["Agent"] = request.Agent,
            [ProjectLogScope.ProjectIdKey] = request.ProjectId,
        });
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
        return result.Last.AgentRun.Id;
    }

    /// <summary>The current release version's SDLC run and the stage of <paramref name="agent"/> in its pinned definition.</summary>
    private async Task<(Guid RunId, string StageId)?> StageOfAsync(Guid projectId, string agent, CancellationToken cancellationToken)
    {
        if (await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is not { } versionId)
        {
            return null;
        }

        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == versionId)
            .Select(r => new { r.Id, r.DefinitionHash })
            .FirstOrDefaultAsync(cancellationToken);
        var stage = run is null ? null : registry.GetByHash(run.DefinitionHash)?.Definition?.Stages.FirstOrDefault(s => s.Agent?.Key == agent);
        return stage is null ? null : (run!.Id, stage.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No pipeline run stage for {Agent} yet; running it on the SDLC runner")]
    private static partial void LogNoRun(ILogger logger, string agent);
}
