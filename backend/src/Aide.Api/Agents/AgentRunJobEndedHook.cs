using System.Text.Json;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// FR-25: when a job ends without succeeding (dead-lettered by a handler failure or by the lease reaper, or failed
/// permanently), every agent run it left <c>running</c> is failed with <c>PROVIDER_ERROR</c> and a <c>RUN_FAILED</c>
/// event (<c>deadLettered</c> as the job), so no run stays <c>running</c> forever.
/// <para>
/// The job's runs are found without a schema change: a run's id is the job id, or derived from the job id and a run key
/// (<see cref="AgentRunRules.RunId"/>); every run's <c>RUN_STARTED</c> event records <c>jobId</c>, so the derived ids are
/// read back from the project's <c>RUN_STARTED</c> events for that job.
/// </para>
/// </summary>
public sealed partial class AgentRunJobEndedHook(TimeProvider time, ILogger<AgentRunJobEndedHook> logger) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        var ids = new HashSet<Guid> { job.JobId };
        if (job.ProjectId is { } projectId)
        {
            var jobId = job.JobId.ToString();
            var payloads = await db.Events
                .FromSql($"SELECT * FROM events WHERE project_id = {projectId} AND type = {EventTypes.RunStarted} AND payload ->> 'jobId' = {jobId}")
                .AsNoTracking()
                .Select(e => e.Payload)
                .ToListAsync(cancellationToken);
            foreach (var payload in payloads)
            {
                if (RunIdOf(payload) is { } runId)
                {
                    ids.Add(runId);
                }
            }
        }

        var runs = await db.AgentRuns
            .Where(r => ids.Contains(r.Id) && r.Status == AgentRunStatus.Running)
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var reason = job.DeadLettered ? "The job was dead-lettered before the run finished" : "The job failed before the run finished";
        var message = string.IsNullOrWhiteSpace(job.Error)
            ? $"{reason}."
            : $"{reason}: {SecretRedaction.SanitizeProviderError(job.Error)}";
        foreach (var run in runs)
        {
            AgentRunner.MarkFailed(db, run, ErrorCodes.ProviderError, message, job.DeadLettered, now);
            LogRunFailed(logger, run.Id, job.JobId, job.DeadLettered);
        }
    }

    private static Guid? RunIdOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("runId", out var value)
                && value.TryGetGuid(out var id)
                    ? id
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent run {RunId} failed because its job {JobId} ended (dead letter: {DeadLetter})")]
    private static partial void LogRunFailed(ILogger logger, Guid runId, Guid jobId, bool deadLetter);
}
