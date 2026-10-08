using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;

namespace Aide.Domain.Agents;

/// <summary>Price of a model in USD per million tokens (FR-23 cost estimate, D15).</summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>The provider and model an agent run uses (FR-22).</summary>
public sealed record LlmChoice(LlmProvider Provider, string Model);

/// <summary>What happens to a run after a failed provider attempt (FR-25).</summary>
/// <param name="RunStatus"><c>running</c> while the job will retry, else <c>failed</c>.</param>
/// <param name="RetryJob">The job is retried with backoff (transient failure, attempts left).</param>
/// <param name="DeadLetter">The job becomes a dead letter (transient failure, last attempt).</param>
public sealed record ProviderFailureOutcome(AgentRunStatus RunStatus, bool RetryJob, bool DeadLetter);

/// <summary>FR-26: the optional per-project token budget.</summary>
public static class TokenBudget
{
    /// <summary>The budget is used up: a budget is set and the tokens used reach it.</summary>
    public static bool IsExhausted(long? budget, long tokensUsed) => budget is { } b && tokensUsed >= b;

    /// <summary>
    /// A run (or execution, FR-26 example) may start only while the budget isn't used up; otherwise
    /// <c>BUDGET_EXCEEDED</c> (409). No budget means unlimited.
    /// </summary>
    public static Result CheckCanStart(long? budget, long tokensUsed) =>
        IsExhausted(budget, tokensUsed)
            ? Result.Failure(new DomainError(
                ErrorCodes.BudgetExceeded,
                $"The project's token budget of {budget!.Value:N0} tokens is used up ({tokensUsed:N0} used). An Owner can raise the budget."))
            : Result.Success();
}

/// <summary>FR-22–FR-25: model choice, cost estimates, failure classification and run identity.</summary>
public static class AgentRunRules
{
    /// <summary>The largest value <c>agent_runs.cost_estimate numeric(10,4)</c> can hold.</summary>
    public const decimal MaxCostEstimate = 999_999.9999m;

    /// <summary>
    /// FR-22: the model for an agent under the given LLM settings: the agent's own model when set (keyed by the
    /// agent's wire name, e.g. <c>idea_refinement</c>), else the default model.
    /// </summary>
    public static LlmChoice ChooseModel(AgentKind agent, LlmProvider provider, string defaultModel, IReadOnlyDictionary<string, string>? modelsByAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        var model = modelsByAgent is not null && modelsByAgent.TryGetValue(agent.ToWire(), out var own) && !string.IsNullOrWhiteSpace(own)
            ? own.Trim()
            : defaultModel.Trim();
        return new LlmChoice(provider, model);
    }

    /// <summary>
    /// FR-23: cost = input × input price + output × output price, per million tokens, rounded to 4 dp (the column's
    /// precision). An unpriced model costs 0.
    /// </summary>
    public static decimal EstimateCost(int inputTokens, int outputTokens, ModelPrice? price)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        if (price is null)
        {
            return 0m;
        }

        var cost = ((inputTokens * price.InputPerMTok) + (outputTokens * price.OutputPerMTok)) / 1_000_000m;
        return Math.Min(Math.Round(cost, 4, MidpointRounding.AwayFromZero), MaxCostEstimate);
    }

    /// <summary>
    /// FR-25: an HTTP status from a provider is transient (worth retrying) for 408, 425, 429 and every 5xx
    /// (including Anthropic's 529 "overloaded"). Other 4xx (auth, validation, unknown model) are permanent.
    /// Timeouts and connection failures have no status and are always transient.
    /// </summary>
    public static bool IsTransientStatus(int statusCode) => statusCode is 408 or 425 or 429 || statusCode >= 500;

    /// <summary>
    /// FR-25: after a failed provider attempt in job attempt <paramref name="jobAttempt"/> (1-based): a transient
    /// failure keeps the run <c>running</c> and retries the job while attempts are left (§4.9: 4 in total); on the last
    /// attempt the run is <c>failed</c> and the job is dead-lettered. A permanent failure fails the run and the job at once.
    /// </summary>
    public static ProviderFailureOutcome OnProviderFailure(bool transient, int jobAttempt, RetryPolicy? policy = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(jobAttempt, 1);
        policy ??= RetryPolicy.Default;
        if (!transient)
        {
            return new ProviderFailureOutcome(AgentRunStatus.Failed, RetryJob: false, DeadLetter: false);
        }

        return policy.ShouldRetry(jobAttempt)
            ? new ProviderFailureOutcome(AgentRunStatus.Running, RetryJob: true, DeadLetter: false)
            : new ProviderFailureOutcome(AgentRunStatus.Failed, RetryJob: false, DeadLetter: true);
    }

    /// <summary>
    /// FR-25: one run row per job. The run id is the job id, so every redelivery and retry of the job updates the
    /// same row. A job that runs several agents (e.g. a corrective re-run, FR-28, or one run per step, FR-31) passes a
    /// <paramref name="runKey"/>; the id is then derived deterministically from the job id and key (SHA-256, RFC 9562
    /// version 8).
    /// </summary>
    public static Guid RunId(Guid jobId, string? runKey = null)
    {
        if (string.IsNullOrEmpty(runKey))
        {
            return jobId;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId:N}/{runKey}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // version 8 (custom), in Guid's little-endian layout
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes);
    }
}
