using Aide.Domain.Common;
using Tevrin.Engine.Runtime.Agents;

namespace Aide.Api.Agents;

public sealed partial class SdlcAgentExtension
{
    /// <summary>
    /// FR-315, FR-311, 0.6.1's commits: the started run and the run before its call are plain saves. An answered run is
    /// dated (to the microsecond, as stored), gets <c>RUN_COMPLETED</c>, or <c>RUN_FAILED</c> when its corrective re-run
    /// was rejected too, and commits with its tokens in one transaction. A failed or kept-running call commits the turns it
    /// paid for with it. The repo snapshot is released once the call is over.
    /// </summary>
    public override async Task CommitRunAsync(AgentAttempt attempt, AgentRunCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(commit);
        var run = attempt.AgentRun!;
        switch (commit.Point)
        {
            case AgentRunCommitPoint.Started or AgentRunCommitPoint.Calling:
                await commit.Save(cancellationToken);
                return;

            case AgentRunCommitPoint.Answered:
                tools.Release();
                var utcNow = time.GetUtcNow();
                var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
                run.CompletedAt = now;
                if (commit.Failure is { } failure)
                {
                    SdlcRunEvents.Fail(run, failure.ErrorCode, failure.Message, now);
                    data.AddEvent(EventTypes.RunFailed, run.ProjectId, SdlcRunEvents.RunFailed(run, failure.ErrorCode, deadLetter: false), now);
                }
                else
                {
                    IReadOnlyList<string> warnings = AgentRunner.ReadOutput(run.Output)?.Warnings ?? [];
                    data.AddEvent(EventTypes.RunCompleted, run.ProjectId, SdlcRunEvents.RunCompleted(run, warnings), now);
                }

                await data.CommitWithTokensAsync(run.ProjectId, hooks.TakePendingTokens(), commit.Save);
                if (commit.Failure is { } rejected)
                {
                    LogOutputRejected(logger, rejected.ErrorCode, run.InputTokens, run.OutputTokens);
                }
                else
                {
                    LogSucceeded(logger, run.InputTokens, run.OutputTokens, run.DurationMs, run.CostEstimate);
                }

                return;

            default:
                tools.Release();
                var paid = hooks.TakePendingTokens();
                if (paid > 0)
                {
                    await data.CommitWithTokensAsync(run.ProjectId, paid, commit.Save);
                }
                else
                {
                    await commit.Save(CancellationToken.None);
                }

                return;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run succeeded: {InputTokens} input / {OutputTokens} output tokens in {DurationMs} ms (cost {CostEstimate})")]
    private static partial void LogSucceeded(ILogger logger, int inputTokens, int outputTokens, long durationMs, decimal costEstimate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run failed: its output was rejected with {ErrorCode} ({InputTokens} input / {OutputTokens} output tokens)")]
    private static partial void LogOutputRejected(ILogger logger, string errorCode, int inputTokens, int outputTokens);
}
