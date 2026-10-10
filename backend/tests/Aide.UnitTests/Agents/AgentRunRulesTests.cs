using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;

namespace Aide.UnitTests.Agents;

/// <summary>FR-22, FR-23, FR-25, FR-26: model choice, cost, failure classification, run identity and the token budget.</summary>
public class AgentRunRulesTests
{
    [Fact]
    public void FR22_The_agents_own_model_wins_over_the_default_model()
    {
        var models = new Dictionary<string, string> { ["idea_refinement"] = "claude-sonnet-5-5" };

        Assert.Equal(new LlmChoice(LlmProvider.Anthropic, "claude-sonnet-5-5"), AgentRunRules.ChooseModel(AgentKind.IdeaRefinement, LlmProvider.Anthropic, "claude-opus-5-5", models));
        Assert.Equal(new LlmChoice(LlmProvider.Anthropic, "claude-opus-5-5"), AgentRunRules.ChooseModel(AgentKind.SpecGenerator, LlmProvider.Anthropic, "claude-opus-5-5", models));
    }

    [Fact]
    public void FR22_Ollama_example_uses_the_projects_model()
    {
        var choice = AgentRunRules.ChooseModel(AgentKind.CodeGenerator, LlmProvider.Ollama, "qwen2.5-coder", new Dictionary<string, string>());

        Assert.Equal(new LlmChoice(LlmProvider.Ollama, "qwen2.5-coder"), choice);
    }

    [Fact]
    public void FR22_A_blank_agent_model_falls_back_to_the_default()
    {
        var models = new Dictionary<string, string> { ["code_review"] = "  " };

        Assert.Equal("fake-1", AgentRunRules.ChooseModel(AgentKind.CodeReview, LlmProvider.Fake, " fake-1 ", models).Model);
        Assert.Equal("fake-1", AgentRunRules.ChooseModel(AgentKind.CodeReview, LlmProvider.Fake, "fake-1", null).Model);
    }

    [Theory]
    [InlineData(1_000_000, 0, 4.0)]
    [InlineData(0, 1_000_000, 20.0)]
    [InlineData(12_345, 6_789, 0.1852)] // 12,345 × 4 / 1M + 6,789 × 20 / 1M = 0.04938 + 0.13578 = 0.18516
    [InlineData(1, 1, 0.0)]
    public void FR23_Cost_estimate_is_tokens_times_price_per_million_rounded_to_4_dp(int input, int output, double expected) =>
        Assert.Equal((decimal)expected, AgentRunRules.EstimateCost(input, output, new ModelPrice(4m, 20m)));

    [Fact]
    public void FR23_An_unpriced_model_costs_nothing_and_cost_is_capped_at_the_column_precision()
    {
        Assert.Equal(0m, AgentRunRules.EstimateCost(5000, 5000, null));
        Assert.Equal(AgentRunRules.MaxCostEstimate, AgentRunRules.EstimateCost(int.MaxValue, int.MaxValue, new ModelPrice(1_000_000m, 1_000_000m)));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(413, false)]
    [InlineData(422, false)]
    public void FR25_Timeouts_429_and_5xx_are_transient_other_4xx_permanent(int status, bool transient) =>
        Assert.Equal(transient, AgentRunRules.IsTransientStatus(status));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FR25_A_transient_failure_before_the_fourth_attempt_keeps_the_run_running_and_retries(int attempt) =>
        Assert.Equal(new ProviderFailureOutcome(AgentRunStatus.Running, RetryJob: true, DeadLetter: false), AgentRunRules.OnProviderFailure(true, attempt));

    [Fact]
    public void FR25_A_transient_failure_on_the_fourth_attempt_fails_the_run_and_dead_letters_the_job()
    {
        Assert.Equal(4, RetryPolicy.Default.MaxAttempts);
        Assert.Equal(new ProviderFailureOutcome(AgentRunStatus.Failed, RetryJob: false, DeadLetter: true), AgentRunRules.OnProviderFailure(true, 4));
    }

    [Fact]
    public void FR25_A_permanent_failure_fails_at_once_without_dead_letter() =>
        Assert.Equal(new ProviderFailureOutcome(AgentRunStatus.Failed, RetryJob: false, DeadLetter: false), AgentRunRules.OnProviderFailure(false, 1));

    [Fact]
    public void FR25_One_run_per_job_the_run_id_is_the_job_id_or_derived_from_job_and_key()
    {
        var job = Guid.CreateVersion7();

        Assert.Equal(job, AgentRunRules.RunId(job));
        Assert.Equal(AgentRunRules.RunId(job, "retry-1"), AgentRunRules.RunId(job, "retry-1"));
        Assert.NotEqual(AgentRunRules.RunId(job, "retry-1"), AgentRunRules.RunId(job, "retry-2"));
        Assert.NotEqual(AgentRunRules.RunId(job, "retry-1"), AgentRunRules.RunId(Guid.CreateVersion7(), "retry-1"));
        Assert.Equal(8, AgentRunRules.RunId(job, "x").Version);
    }

    [Fact]
    public void FR26_Budget_100k_with_100k_used_is_refused_with_BUDGET_EXCEEDED()
    {
        var result = TokenBudget.CheckCanStart(100_000, 100_000);

        Assert.Equal(ErrorCodes.BudgetExceeded, result.Error?.Code);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.BudgetExceeded));
    }

    [Theory]
    [InlineData(null, 5_000_000L, true)]
    [InlineData(100_000L, 99_999L, true)]
    [InlineData(100_000L, 100_000L, false)]
    [InlineData(100_000L, 150_000L, false)]
    public void FR26_A_run_may_start_only_while_the_budget_is_not_used_up(long? budget, long used, bool allowed)
    {
        Assert.Equal(allowed, TokenBudget.CheckCanStart(budget, used).IsSuccess);
        Assert.Equal(!allowed, TokenBudget.IsExhausted(budget, used));
    }
}
