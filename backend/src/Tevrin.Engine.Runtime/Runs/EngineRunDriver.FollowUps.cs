using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-354, §4.70: follow-up stages. When a run completes, one delayed <c>stage.run</c> job per declared duration is queued
/// in the completing save (<c>run_after = completed_at + after</c>). Each runs its stage's agent on the completed run: it
/// reads the run's artifacts and outcome scores, stores its output with variant <c>followup:&lt;duration&gt;</c> (never the
/// main flow's latest-locked version), and records a stage execution with that <c>followup</c> label. The run stays
/// <c>completed</c>; a failure retries the job like any other and never changes the run.
/// </summary>
public sealed partial class EngineRunDriver
{
    /// <summary>Queues the run's follow-ups (in the caller's save), due from <paramref name="completedAt"/>.</summary>
    private void ScheduleFollowUps(PipelineRun run, PipelineDefinition definition, DateTimeOffset completedAt)
    {
        foreach (var followUp in FollowUpRules.Schedule(definition, completedAt))
        {
            jobs.Enqueue(
                EngineJobTypes.StageRun,
                new StageRunPayload(run.Id, Guid.CreateVersion7()) { FollowUpStage = followUp.Stage, FollowUp = followUp.Label },
                run.ProjectId,
                completedAt,
                followUp.DueAt);
            LogFollowUpScheduled(logger, run.Id, followUp.Stage, followUp.Label, followUp.DueAt);
        }
    }

    private async Task DriveFollowUpAsync(JobExecution job, PipelineRun run, StageRunPayload payload, CancellationToken cancellationToken)
    {
        var execution = await db.StageExecutions.FirstOrDefaultAsync(e => e.Id == payload.StageExecutionId, cancellationToken);
        if (FollowUpRules.IsStale(run, execution))
        {
            LogStale(logger, run.Id, payload.StageExecutionId, "the run is not completed or the follow-up already ran");
            return;
        }

        var resolved = await definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
        if (resolved is not { Available: true })
        {
            throw new JobFailedException(
                $"Pipeline run {run.Id} is pinned to a definition this build can't run.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        }

        var definition = resolved.Definition;
        if (!FollowUpRules.IsDeclared(definition, payload.FollowUpStage, payload.FollowUp) || definition.FindStage(payload.FollowUpStage) is not { } stage)
        {
            throw new JobFailedException(
                $"Follow-up '{payload.FollowUp}' of stage '{payload.FollowUpStage}' is not in the run's definition.", transient: false, EngineErrorCodes.Validation);
        }

        if (execution is null)
        {
            execution = await StageExecutionWriter.EnterFollowUpAsync(
                db, payload.StageExecutionId, run.Id, stage.Id, payload.FollowUp!, run.Checkpoint, time.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var result = await agents.RunAsync(
            new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
            {
                AgentRunId = AgentRunRules.RunId(job.Id, AgentRunKey),
                JobAttempt = job.Attempt,
                Variant = payload.FollowUp,
            },
            cancellationToken);
        result.ThrowIfFailed();
        if (result.End != AgentStageEnd.Stored)
        {
            throw new JobFailedException(
                $"Follow-up '{payload.FollowUp}' of stage '{stage.Id}' stored no output.", transient: false, EngineErrorCodes.Validation);
        }

        execution.ExitedAt = time.GetUtcNow();
        execution.Outcome = GenericRunStatus.Completed.ToWire();
        await db.SaveChangesAsync(cancellationToken);
        LogFollowUpDone(logger, run.Id, stage.Id, payload.FollowUp!);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} follow-up {StageId} ({FollowUp}) scheduled for {DueAt}")]
    private static partial void LogFollowUpScheduled(ILogger logger, Guid pipelineRunId, string stageId, string followUp, DateTimeOffset dueAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} follow-up {StageId} ({FollowUp}) stored its output")]
    private static partial void LogFollowUpDone(ILogger logger, Guid pipelineRunId, string stageId, string followUp);
}
