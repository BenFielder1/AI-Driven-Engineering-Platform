using System.Globalization;
using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>
/// D-V2-14: writes a step attempt's outcome to <c>execution_step_attempts</c> in the caller's unit of work, so the
/// history row is saved in the same transaction as the step's outcome (the caller saves). <see cref="StepAttemptRules"/>
/// numbers the row and makes a redelivered attempt update its row instead of adding a second one.
/// </summary>
public static class StepAttemptRecorder
{
    /// <summary>The run key of a step attempt's Code Generator run (the run id derives from the job id and this key).</summary>
    public static string RunKey(Guid stepId, int attempt) =>
        $"step-{stepId:N}-attempt-{attempt.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The agent run of <paramref name="step"/>'s current attempt within job <paramref name="jobId"/>, if that run row
    /// exists (an attempt completed from an existing commit or interrupted before the provider call may have none).
    /// </summary>
    public static async Task<Guid?> AttemptRunIdAsync(AideDbContext db, Guid jobId, ExecutionStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(step);
        var runId = AgentRunRules.RunId(jobId, RunKey(step.Id, step.Attempt));
        return await db.AgentRuns.AsNoTracking().AnyAsync(r => r.Id == runId, cancellationToken) ? runId : null;
    }

    /// <summary>
    /// Records the outcome of <paramref name="step"/>'s current attempt. With <paramref name="keepExisting"/>, an outcome
    /// already recorded for the same run is left as it is (an interruption never overwrites a recorded outcome).
    /// </summary>
    public static async Task RecordAsync(
        AideDbContext db,
        ExecutionStep step,
        StepAttemptOutcome outcome,
        Guid? agentRunId,
        IEnumerable<GuardianViolation> violations,
        IEnumerable<GuardianViolation> warnings,
        IEnumerable<FileManifestEntry> producedFiles,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool keepExisting = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(step);
        var recorded = await db.ExecutionStepAttempts.Where(a => a.StepId == step.Id).ToListAsync(cancellationToken);
        var slot = StepAttemptRules.Slot([.. recorded.Select(r => new RecordedAttempt(r.Attempt, r.AgentRunId))], agentRunId);
        ExecutionStepAttempt row;
        if (slot.Existing)
        {
            if (keepExisting)
            {
                return;
            }

            row = recorded.First(r => r.Attempt == slot.Attempt);
        }
        else
        {
            row = new ExecutionStepAttempt { StepId = step.Id, Attempt = slot.Attempt, CreatedAt = now };
            db.ExecutionStepAttempts.Add(row);
        }

        row.AgentRunId = agentRunId;
        row.Outcome = outcome;
        row.Violations = [.. violations];
        row.Warnings = [.. warnings];
        row.ProducedFiles = [.. producedFiles];
    }
}
