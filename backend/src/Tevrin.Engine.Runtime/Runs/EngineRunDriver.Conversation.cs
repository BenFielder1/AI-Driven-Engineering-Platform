using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-356, §4.69, D-0.8.0-24: conversation stages. Each turn is one <c>stage.run</c> job on the stage's own execution and
/// one agent run (id derived from the job, so a redelivery reuses it) for the next participant, given its own prompt, the
/// stage's inputs, its role (<c>{{ROLE}}</c>) and the turns so far (<c>{{TRANSCRIPT}}</c>); the run records
/// <see cref="AgentRun.TurnIndex"/> and <see cref="AgentRun.TurnRole"/>. The transcript is read back from the accepted turn
/// runs of the current entry, so nothing else holds conversation state. After each turn the stop checks run (phrase →
/// guard → <c>max_turns</c>): on a stop the transcript is stored as the stage's output and the stage transitions;
/// otherwise the job queues the next turn's job (touching the run, so a cancel or rollback meanwhile wins on its
/// <c>xmin</c>). A failed turn fails its job; the run stays active and a retry resumes at that turn. An escalated turn
/// queues nothing: the gate's decision re-queues the execution, which runs the turn again.
/// </summary>
public sealed partial class EngineRunDriver
{
    /// <summary>The run key of a conversation turn's agent run (its id is derived from the turn's job, FR-310).</summary>
    public const string TurnRunKey = "turn";

    private async Task DriveConversationAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var conversation = stage.Conversation!;
        var turns = await AcceptedTurnsAsync(run, stage, execution, cancellationToken);
        var ownRunId = AgentRunRules.RunId(job.Id, TurnRunKey);
        var own = await db.AgentRuns.AsNoTracking()
            .Where(r => r.Id == ownRunId)
            .Select(r => new { r.TurnIndex })
            .FirstOrDefaultAsync(cancellationToken);

        int turn;
        if (own?.TurnIndex is { } ownTurn)
        {
            // A redelivery of this turn's job: the same turn (its agent run is reused), unless a later turn has started.
            turn = ownTurn;
        }
        else
        {
            if (await RunHasPendingGateAsync(run, cancellationToken))
            {
                // An escalated turn (or the entry gate) waits for its decision; nothing else may run the conversation on.
                LogStale(logger, run.Id, execution.Id, "the run has a pending gate");
                return;
            }

            if (turns.Count == 0)
            {
                // The first turn runs the stage's entry: inputs, requirements and the gate, as for any stage.
                var facts = await EntryFactsAsync(run, stage, execution, definition, cancellationToken);
                if (RunDriverRules.Plan(stage, facts) is not DriverStep.RunConversation)
                {
                    await RunStageAsync(job, run, stage, execution, definition, cancellationToken);
                    return;
                }
            }
            else if (await StopReasonAsync(run, stage, conversation, turns, cancellationToken) is { } stopped)
            {
                // A retry after the stop was decided but the stage didn't move on: finish without another turn.
                await FinishConversationAsync(job, run, stage, execution, definition, turns, stopped, cancellationToken);
                return;
            }

            turn = turns.Count + 1;
        }

        if (await LaterTurnStartedAsync(run, stage, execution, turn, cancellationToken))
        {
            LogStale(logger, run.Id, execution.Id, "a later turn of the conversation has started");
            return;
        }

        var speaker = ConversationRules.SpeakerOf(conversation, turn);
        var earlier = turns.Take(turn - 1).ToList();
        var result = await agents.RunAsync(
            new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
            {
                AgentKey = speaker.Agent,
                AgentRunId = ownRunId,
                JobAttempt = job.Attempt,
                TurnIndex = turn,
                TurnRole = speaker.Role,
                PromptValues = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [AgentPromptPlaceholders.Transcript] = ConversationRules.RenderTranscript(earlier),
                    [AgentPromptPlaceholders.Role] = speaker.Role,
                },
            },
            cancellationToken);
        result.ThrowIfFailed();
        if (result.End != AgentStageEnd.Stored)
        {
            // Escalated: the escalation gate is pending; its decision re-queues this execution, which runs the turn again.
            return;
        }

        LogTurn(logger, run.Id, stage.Id, turn, speaker.Role);
        var reply = ConversationRules.ReadReply(OutputText(result.Last));
        List<ConversationTurn> transcript = [.. earlier, new ConversationTurn(speaker.Role, speaker.Agent, reply, result.Last.AgentRun.Id)];
        if (await StopReasonAsync(run, stage, conversation, transcript, cancellationToken) is { } reason)
        {
            await FinishConversationAsync(job, run, stage, execution, definition, transcript, reason, cancellationToken);
            return;
        }

        await QueueNextTurnAsync(job, run, execution, cancellationToken);
    }

    /// <summary>§4.69: the stop checks after the last of <paramref name="turns"/>: the phrase, the guard (asked only when the
    /// phrase didn't stop it, with the transcript), then <c>max_turns</c>.</summary>
    private async Task<ConversationStopReason?> StopReasonAsync(
        PipelineRun run, StageDefinition stage, ConversationDefinition conversation, List<ConversationTurn> turns, CancellationToken cancellationToken)
    {
        var phrase = turns.Count > 0 && ConversationRules.PhraseIn(conversation.Stop, turns[^1].Text);
        var guardPassed = false;
        if (ConversationRules.NeedsGuard(conversation, phrase) && turns.Count > 0)
        {
            var name = conversation.Stop!.Guard!;
            var guard = services.GetPipelineGuard(name)
                ?? throw new JobFailedException($"Guard '{name}' is not bound.", transient: false, EngineErrorCodes.DefinitionUnavailable);
            var facts = FactBag.Empty
                .With(new ConversationTranscriptFact(turns))
                .With(new StageOutputFact(stage.Output ?? string.Empty, ArtifactFormat.Json, ConversationRules.TranscriptJson(turns)));
            guardPassed = (await guard.EvaluateAsync(new GuardContext(run.Id, run.ProjectId, stage.Id, run.ApprovalMode, facts), cancellationToken)).Ok;
        }

        return ConversationRules.StopAfter(conversation, turns.Count, phrase, guardPassed);
    }

    /// <summary>
    /// The conversation stopped: the transcript is stored as the stage's output (once per entry: a redelivery that already
    /// stored it stores nothing more), then the stage's transition (veto, route, next) in the same save.
    /// </summary>
    private async Task FinishConversationAsync(
        JobExecution job,
        PipelineRun run,
        StageDefinition stage,
        StageExecution execution,
        PipelineDefinition definition,
        List<ConversationTurn> turns,
        ConversationStopReason reason,
        CancellationToken cancellationToken)
    {
        LogConversationStopped(logger, run.Id, stage.Id, turns.Count, reason);
        var output = ConversationRules.TranscriptJson(turns);
        await StoreMapOutputAsync(run, stage, definition, output, itemIndex: null, execution.EnteredAt, cancellationToken);
        await WriteFeedbackAsync(run, stage, execution, definition, output, cancellationToken);
        try
        {
            await TransitionAsync(job, run, stage, execution, definition, output, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Moved(ex);
        }
    }

    /// <summary>
    /// Queues the next turn's job on the conversation's execution, unless another job of it is already queued or running
    /// (a redelivery after the hand-over). The run is touched, so a cancel or rollback meanwhile makes this save fail and the
    /// job retry, then stop as stale.
    /// </summary>
    private async Task QueueNextTurnAsync(JobExecution job, PipelineRun run, StageExecution execution, CancellationToken cancellationToken)
    {
        if ((await MapJobsAsync(run, [execution.Id], cancellationToken)).Any(j => j.Active && j.JobId != job.Id))
        {
            LogStale(logger, run.Id, execution.Id, "the next turn is already queued");
            return;
        }

        EngineRunMoves.Touch(db, run);
        EngineRunMoves.QueueDriver(jobs, run, execution.Id, time.GetUtcNow());
        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>
    /// The accepted turns of the conversation's current entry, in order from turn 1: per turn, the newest succeeded agent
    /// run that wasn't rejected (an escalated or retried attempt doesn't count), up to the first missing turn.
    /// </summary>
    private async Task<List<ConversationTurn>> AcceptedTurnsAsync(
        PipelineRun run, StageDefinition stage, StageExecution execution, CancellationToken cancellationToken)
    {
        var (runId, stageId, since) = (run.Id, stage.Id, execution.EnteredAt);
        var rows = await db.AgentRuns.AsNoTracking()
            .Where(r => r.PipelineRunId == runId && r.Stage == stageId && r.TurnIndex != null && r.CreatedAt >= since
                && r.Status == AgentRunStatus.Succeeded && r.Error == null)
            .Select(r => new { r.Id, r.TurnIndex, r.TurnRole, r.Agent, r.Output, r.CreatedAt })
            .ToListAsync(cancellationToken);
        var byTurn = rows.GroupBy(r => r.TurnIndex!.Value).ToDictionary(g => g.Key, g => g.MaxBy(r => r.CreatedAt)!);
        var turns = new List<ConversationTurn>();
        for (var turn = 1; byTurn.TryGetValue(turn, out var row); turn++)
        {
            var text = ConversationRules.ReadReply(AgentStageRunner.ReadOutput(row.Output)?.Text);
            turns.Add(new ConversationTurn(row.TurnRole ?? string.Empty, row.Agent, text, row.Id));
        }

        return turns;
    }

    /// <summary>Whether a turn after <paramref name="turn"/> of the current entry has an agent run (this job is stale).</summary>
    private Task<bool> LaterTurnStartedAsync(PipelineRun run, StageDefinition stage, StageExecution execution, int turn, CancellationToken cancellationToken)
    {
        var (runId, stageId, since) = (run.Id, stage.Id, execution.EnteredAt);
        return db.AgentRuns.AnyAsync(
            r => r.PipelineRunId == runId && r.Stage == stageId && r.TurnIndex > turn && r.CreatedAt >= since, cancellationToken);
    }

    private Task<bool> RunHasPendingGateAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        var runId = run.Id;
        return db.ApprovalGates.AnyAsync(g => g.PipelineRunId == runId && g.Status == GateStatus.Pending, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} conversation {StageId}: turn {Turn} spoken by {Role}")]
    private static partial void LogTurn(ILogger logger, Guid pipelineRunId, string stageId, int turn, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} conversation {StageId} stopped after {TurnCount} turns ({Reason})")]
    private static partial void LogConversationStopped(ILogger logger, Guid pipelineRunId, string stageId, int turnCount, ConversationStopReason reason);
}
