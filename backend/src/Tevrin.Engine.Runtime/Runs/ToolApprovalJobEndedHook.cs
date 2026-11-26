using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-359, §4.73: when a <c>stage.run</c> job ends without succeeding (failed permanently, e.g. <c>DEFINITION_UNAVAILABLE</c>,
/// or dead-lettered), a destructive call it saved can never resume: only that job runs it. Each such call (its state names
/// the job) is closed: its pending <c>tool_approval</c> gate is cancelled, the saved loop cleared and the agent run failed,
/// so the tool never runs and the stage can be retried.
/// <para>
/// It runs inside the worker's final save, so it never throws for a row it can't settle and writes with guarded
/// set-based updates (in the worker's transaction): no change tracking, no concurrency token to conflict on. A gate decided
/// meanwhile, or a run another writer already ended, is simply left alone. The host's <c>AgentRunJobEndedHook</c> finds
/// runs by the job id and the SDLC's <c>RUN_STARTED</c> events, which engine <c>stage.run</c> agent runs (ids derived from
/// the job id, no such event) don't have, so it never acts on these runs and the order of the two hooks doesn't matter;
/// the guard on <c>status = running</c> keeps that true if it ever does.
/// </para>
/// </summary>
public sealed class ToolApprovalJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public const string ClosedError = "The job ended before the destructive tool call's approval was settled; the tool was not run.";

    public async Task OnJobEndedAsync(IEngineDbContext engineDb, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engineDb);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != EngineJobTypes.StageRun)
        {
            return;
        }

        var payload = await engineDb.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is null || StageRunPayload.Parse(payload) is not { } stageRun)
        {
            return;
        }

        var waiting = await engineDb.AgentRuns.AsNoTracking()
            .Where(r => r.PipelineRunId == stageRun.RunId && r.Status == AgentRunStatus.Running && r.ToolLoopState != null)
            .Select(r => new { r.Id, r.ToolLoopState })
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var row in waiting)
        {
            if (ToolLoopStates.Read(row.ToolLoopState) is not { } state || state.JobId != job.JobId)
            {
                continue;
            }

            if (state.GateId is { } gateId)
            {
                await engineDb.ApprovalGates
                    .Where(g => g.Id == gateId && g.Type == EngineGates.ToolApproval && g.Status == GateStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GateStatus.Cancelled).SetProperty(g => g.DecidedAt, now), cancellationToken);
            }

            await engineDb.AgentRuns
                .Where(r => r.Id == row.Id && r.Status == AgentRunStatus.Running)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.ToolLoopState, (string?)null)
                        .SetProperty(r => r.Status, AgentRunStatus.Failed)
                        .SetProperty(r => r.Error, ClosedError)
                        .SetProperty(r => r.CompletedAt, now),
                    cancellationToken);
        }
    }
}
