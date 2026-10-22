namespace Aide.Domain.Artifacts;

/// <summary>Where the version's <c>repository_analysis</c> job is (FR-70): none queued, queued, running, or ended.</summary>
public enum AnalysisJobState
{
    None = 0,
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>
/// The Repository Analyst's progress for a release version as shown on the Refinement page (FR-70): <c>not_needed</c> (no
/// existing code), <c>queued</c>, <c>running</c>, <c>completed</c> (the summary exists), <c>failed</c> (the analyst ended
/// without a summary) or <c>timed_out</c> (still unfinished 10 minutes after the version started; refinement no longer
/// waits for it).
/// </summary>
public enum CodebaseSummaryState
{
    NotNeeded = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    TimedOut = 5,
}

/// <summary>A refinement run's decision about the summary: wait (re-queue at <see cref="RetryAt"/>), or go on (with a warning, if any).</summary>
public sealed record SummaryWaitDecision(bool Wait, DateTimeOffset? RetryAt, string? Warning)
{
    public static SummaryWaitDecision Proceed(string? warning = null) => new(false, null, warning);
}

/// <summary>
/// FR-70: refinement may start before the codebase summary exists. Its job then waits — re-queued, never a blocking sleep —
/// up to <see cref="MaxWait"/> after the release version started, polling every <see cref="PollInterval"/>, and then
/// continues without it (<c>summary_wait_timeout</c>). An analyst that ended without a summary isn't waited for
/// (<c>summary_unavailable</c>).
/// </summary>
public static class CodebaseSummaryWait
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public static SummaryWaitDecision Decide(bool hasExistingCode, bool summaryExists, AnalysisJobState analysis, DateTimeOffset versionStartedAt, DateTimeOffset now)
    {
        if (!hasExistingCode || summaryExists)
        {
            return SummaryWaitDecision.Proceed();
        }

        if (analysis is AnalysisJobState.None or AnalysisJobState.Succeeded or AnalysisJobState.Failed)
        {
            return SummaryWaitDecision.Proceed(Agents.AgentRunWarnings.SummaryUnavailable);
        }

        var deadline = versionStartedAt + MaxWait;
        if (now >= deadline)
        {
            return SummaryWaitDecision.Proceed(Agents.AgentRunWarnings.SummaryWaitTimeout);
        }

        var next = now + PollInterval;
        return new SummaryWaitDecision(true, next < deadline ? next : deadline, null);
    }

    /// <summary>The state shown for the version (<see cref="CodebaseSummaryState"/>).</summary>
    public static CodebaseSummaryState State(bool hasExistingCode, bool summaryExists, AnalysisJobState analysis, DateTimeOffset versionStartedAt, DateTimeOffset now)
    {
        if (summaryExists)
        {
            return CodebaseSummaryState.Completed;
        }

        if (!hasExistingCode && analysis == AnalysisJobState.None)
        {
            return CodebaseSummaryState.NotNeeded;
        }

        return analysis switch
        {
            AnalysisJobState.Queued or AnalysisJobState.Running when now >= versionStartedAt + MaxWait => CodebaseSummaryState.TimedOut,
            AnalysisJobState.Queued => CodebaseSummaryState.Queued,
            AnalysisJobState.Running => CodebaseSummaryState.Running,
            AnalysisJobState.None => CodebaseSummaryState.NotNeeded,
            _ => CodebaseSummaryState.Failed,
        };
    }
}
