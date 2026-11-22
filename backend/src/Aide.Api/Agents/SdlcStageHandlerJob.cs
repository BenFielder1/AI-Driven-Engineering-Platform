using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Stages;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-319, D-0.7.0-43: SDLC stage work that runs inside a job (the execution step loop, the build check). The job's own
/// handler stays the entry point for its job type; the work itself is this <see cref="IStageHandler"/>, which the engine
/// runs when the run's pinned definition declares <see cref="Name"/> (<see cref="SdlcStageHandlerJob"/>). The job arrives
/// as a <see cref="StageJobFact"/>, and the work keeps the job's semantics: it throws the job layer's exceptions
/// (<see cref="JobFailedException"/>, <see cref="JobDeferredException"/>) as it did inline.
/// </summary>
public abstract class SdlcJobStageHandler : IStageHandler
{
    /// <summary>The handler name (<see cref="SdlcStageHandlerNames"/>).</summary>
    public abstract string Name { get; }

    /// <summary>The work for <paramref name="job"/>, exactly as the job ran it inline in 0.6.1.</summary>
    public abstract Task RunJobAsync(JobExecution job, CancellationToken cancellationToken);

    public async Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var job = context.Facts.Get<StageJobFact>()?.Job
            ?? throw new JobFailedException($"Stage handler '{Name}' runs inside a job, and none was given.", transient: false, ErrorCodes.Validation);
        await RunJobAsync(job, cancellationToken);
        return Completed;
    }

    private static readonly StageHandlerResult Completed = new(StageHandlerOutcome.Completed);
}

/// <summary>A project's current SDLC pipeline run and the definition it is pinned to.</summary>
public sealed record SdlcRunPin(Guid RunId, string DefinitionHash);

/// <summary>D-0.7.0-43: finds a project's <see cref="SdlcRunPin"/> (a seam, so the dispatch is tested without a database).</summary>
public interface ISdlcRunPins
{
    /// <summary>The run of the project's current release version, or <c>null</c>. Read-only: it never creates a run.</summary>
    Task<SdlcRunPin?> FindAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary><see cref="ISdlcRunPins"/> on the database: the current version, then its SDLC run (untracked reads).</summary>
public sealed class SdlcRunPins(AideDbContext db) : ISdlcRunPins
{
    public async Task<SdlcRunPin?> FindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is not { } subject)
        {
            return null;
        }

        return await db.PipelineRuns.AsNoTracking()
            .Where(r => r.PipelineId == SdlcPack.PipelineId && r.SubjectId == subject)
            .Select(r => new SdlcRunPin(r.Id, r.DefinitionHash))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// FR-319, D-0.7.0-43: runs an SDLC job's stage work through the engine (<see cref="StageHandlerRunner"/>) when the
/// project's run is pinned to a definition that declares the handler (version 3 on), so the engine resolves it by name and
/// invokes it on its stage. Otherwise the job calls the same handler directly, with identical behaviour:
/// <list type="bullet">
/// <item>the run's definition declares no such handler (versions 1 and 2, until FR-321 re-pins the run);</item>
/// <item>the project has no run yet (the agent call creates it, as before: FR-320), or the job names no project;</item>
/// <item>the pinned definition can't run in this build: the direct call refuses it where 0.6.1's code path did (the agent
/// call's <c>DEFINITION_UNAVAILABLE</c>), so a step still starts and fails as it did before.</item>
/// </list>
/// Either way the same scoped handler instance runs, so the events, steps, commits and job outcome are the same.
/// </summary>
public sealed partial class SdlcStageHandlerJob(ISdlcRunPins pins, StageHandlerRunner runner, ILogger<SdlcStageHandlerJob> logger)
{
    public async Task RunAsync(JobExecution job, SdlcJobStageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(handler);
        if (job.ProjectId is { } projectId && await pins.FindAsync(projectId, cancellationToken) is { } pin)
        {
            var request = new StageHandlerRequest(pin.RunId, projectId, pin.DefinitionHash, handler.Name)
            {
                Facts = FactBag.Empty.With(new StageJobFact(job)),
            };
            if (await runner.RunAsync(request, cancellationToken) is StageHandlerDispatch.Ran ran)
            {
                ran.Result.ThrowIfFailed(handler.Name);
                return;
            }

            LogDirect(logger, handler.Name, pin.RunId);
        }

        await handler.RunJobAsync(job, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stage handler {Handler} runs directly: pipeline run {PipelineRunId}'s definition doesn't dispatch it")]
    private static partial void LogDirect(ILogger logger, string handler, Guid pipelineRunId);
}
