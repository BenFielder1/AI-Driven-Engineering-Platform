namespace Aide.Domain.Agents;

/// <summary>
/// Non-blocking conditions recorded on an agent run (FR-70, FR-71): stored with the run's output and in its
/// <c>RUN_COMPLETED</c> event payload (<c>warnings</c>).
/// </summary>
public static class AgentRunWarnings
{
    /// <summary>FR-71: the provider/model can't call tools; the run used a context-only prompt.</summary>
    public const string ToolsUnsupported = "tools_unsupported";

    /// <summary>FR-71: the repository snapshot the tools need couldn't be made; the run had no repo tools.</summary>
    public const string SnapshotUnavailable = "snapshot_unavailable";

    /// <summary>FR-70: refinement waited 10 minutes for the codebase summary and continued without it.</summary>
    public const string SummaryWaitTimeout = "summary_wait_timeout";

    /// <summary>FR-70: the Repository Analyst failed (or produced nothing), so refinement continued without a summary.</summary>
    public const string SummaryUnavailable = "summary_unavailable";
}
