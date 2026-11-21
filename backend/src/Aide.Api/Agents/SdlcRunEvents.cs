using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;

namespace Aide.Api.Agents;

/// <summary>
/// FR-311: the payloads of the SDLC's agent run events (<c>RUN_STARTED</c>, <c>RUN_COMPLETED</c>, <c>RUN_FAILED</c>,
/// <c>BUDGET_EXCEEDED</c>) and the failed-run fields, 0.6.1's exactly. Shared by <see cref="AgentRunner"/> and the
/// engine path (<see cref="SdlcAgentExtension"/>), so both write the same rows.
/// </summary>
internal static class SdlcRunEvents
{
    public static object RunStarted(AgentRun run, Guid jobId, string? stack, string? legacyAgentKey) =>
        new { runId = run.Id, jobId, agent = run.Agent, provider = run.Provider.ToWire(), model = run.Model, promptVersion = run.PromptVersion, stepId = run.StepId, stack, agentKey = run.AgentKey, legacyAgentKey };

    public static object RunCompleted(AgentRun run, IReadOnlyList<string> warnings) =>
        new { runId = run.Id, agent = run.Agent, inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings };

    public static object RunFailed(AgentRun run, string errorCode, bool deadLetter) =>
        new { runId = run.Id, agent = run.Agent, errorCode, error = run.Error, attempts = run.Attempts, deadLettered = deadLetter };

    /// <summary>FR-26: a new run refused at its start.</summary>
    public static object BudgetRefused(string agent, long? tokenBudget, long tokensUsed, Guid jobId) =>
        new { agent, tokenBudget, tokensUsed, jobId };

    /// <summary>FR-26: a run stopped between tool-loop turns.</summary>
    public static object BudgetStopped(AgentRun run, long? tokenBudget, long tokensUsed, Guid jobId) =>
        new { agent = run.Agent, tokenBudget, tokensUsed, jobId, runId = run.Id };

    /// <summary>The failed run's fields (its <c>RUN_FAILED</c> is <see cref="RunFailed"/>).</summary>
    public static void Fail(AgentRun run, string errorCode, string message, DateTimeOffset now)
    {
        run.Status = AgentRunStatus.Failed;
        run.Error = Truncate($"{errorCode}: {message}");
        run.CompletedAt = now;
    }

    public static string Truncate(string value) => value.Length > AgentRunner.MaxErrorLength ? value[..AgentRunner.MaxErrorLength] : value;
}
