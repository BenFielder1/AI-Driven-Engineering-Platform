using System.Text;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-297, FR-298, D-0.7.0-16: the stored agent input (redacted, capped), stage execution rules and the prompt
/// comparison math.</summary>
public class RunLogTests
{
    [Fact]
    public void FR297_an_sk_ant_key_in_the_prompt_is_stored_redacted()
    {
        const string key = "sk-ant-api03-AbCdEf0123456789xyz";

        var input = AgentRunInput.Record(
            [new AgentInputMessage("system", "You are a spec writer."), new AgentInputMessage("user", $"Use {key} to call the API.")]);

        Assert.DoesNotContain(key, input.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", input.Text, StringComparison.Ordinal);
        Assert.Contains(SecretRedaction.Mask, input.Text, StringComparison.Ordinal);
        Assert.False(input.Truncated);
    }

    [Fact]
    public void FR297_the_input_keeps_each_message_under_its_role()
    {
        var input = AgentRunInput.Record([new AgentInputMessage("system", "S"), new AgentInputMessage("user", "U")]);

        Assert.Equal("[system]\nS\n\n[user]\nU", input.Text);
    }

    [Fact]
    public void FR297_the_callers_known_secret_is_redacted_too()
    {
        var input = AgentRunInput.Record([new AgentInputMessage("user", "token: ollama-local-secret-42")], "ollama-local-secret-42");

        Assert.DoesNotContain("ollama-local-secret-42", input.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR297_input_over_1_MB_is_capped_at_1_MB_of_UTF_8_and_flagged()
    {
        var input = AgentRunInput.Record([new AgentInputMessage("user", new string('a', AgentRunInput.MaxBytes + 10))]);

        Assert.True(input.Truncated);
        Assert.Equal(AgentRunInput.MaxBytes, Encoding.UTF8.GetByteCount(input.Text));
    }

    [Fact]
    public void FR297_input_of_exactly_1_MB_is_kept_whole()
    {
        var text = new string('b', AgentRunInput.MaxBytes);

        var capped = AgentRunInput.Cap(text);

        Assert.False(capped.Truncated);
        Assert.Equal(text, capped.Text);
    }

    [Fact]
    public void FR297_the_cap_never_splits_a_multi_byte_character()
    {
        // "é" is 2 bytes and "😀" 4 (a surrogate pair): a 5-byte cap keeps "aé" (3 bytes), not half of the emoji.
        var capped = AgentRunInput.Cap("aé😀z", maxBytes: 5);

        Assert.True(capped.Truncated);
        Assert.Equal("aé", capped.Text);
    }

    [Fact]
    public void FR297_the_cap_counts_bytes_not_characters()
    {
        var text = new string('é', AgentRunInput.MaxBytes / 2 + 1);

        var capped = AgentRunInput.Cap(text);

        Assert.True(capped.Truncated);
        Assert.Equal(AgentRunInput.MaxBytes / 2, capped.Text.Length);
    }

    [Theory]
    [InlineData(null, "refinement", "start", true)]
    [InlineData("execution", "review", "advance", true)]
    [InlineData("review", "execution", "loop", true)]
    [InlineData("review", "review", "rollback", true)]
    [InlineData("release", "release", "status_change", false)]
    [InlineData("release", "release", "noop", false)]
    [InlineData("planning", "execution", null, true)]
    public void FR297_which_transitions_enter_a_stage(string? from, string to, string? kind, bool entry) =>
        Assert.Equal(entry, StageExecutionRules.IsEntry(from, to, kind));

    [Theory]
    [InlineData(null, null, "start")]
    [InlineData("planning", null, "advance")]
    [InlineData("review", "loop", "loop")]
    public void FR297_the_outcome_of_the_execution_a_transition_leaves(string? from, string? kind, string outcome) =>
        Assert.Equal(outcome, StageExecutionRules.LeavingOutcome(from, kind));

    [Fact]
    public void FR297_run_totals_add_input_and_output_tokens()
    {
        var totals = new RunTotals(3, 0.25m, 1200, 300);

        Assert.Equal(1500, totals.Tokens);
        Assert.Equal(0, RunTotals.None.Tokens);
    }

    [Fact]
    public void FR298_averages_and_failure_rate_of_a_prompt_version()
    {
        // 4 finished runs, 1 failed; 3 recorded a latency.
        var stats = RunLogStats.Summarise(new PromptRunSums(
            "spec_generator", "3", Runs: 4, Finished: 4, Failed: 1, InputTokens: 4000, OutputTokens: 1000, Cost: 0.1m,
            LatencyRuns: 3, LatencyMs: 900, DurationMs: 8000));

        Assert.Equal(("spec_generator", "3", 4), (stats.Agent, stats.PromptVersion, stats.Runs));
        Assert.Equal(1000d, stats.AvgInputTokens);
        Assert.Equal(250d, stats.AvgOutputTokens);
        Assert.Equal(0.025m, stats.AvgCost);
        Assert.Equal(300d, stats.AvgLatencyMs);
        Assert.Equal(2000d, stats.AvgDurationMs);
        Assert.Equal(0.25d, stats.FailureRate);
    }

    [Fact]
    public void FR298_the_failure_rate_counts_only_finished_runs()
    {
        // 5 runs: 1 succeeded, 1 failed, and 3 queued, running or cancelled (no result yet, or none).
        var stats = RunLogStats.Summarise(new PromptRunSums("a", "1", Runs: 5, Finished: 2, Failed: 1, 0, 0, 0m, 0, 0, 0));

        Assert.Equal((5, 0.5d), (stats.Runs, stats.FailureRate));
    }

    [Fact]
    public void FR298_the_failure_rate_is_zero_when_no_run_finished()
    {
        var stats = RunLogStats.Summarise(new PromptRunSums("a", "1", Runs: 2, Finished: 0, Failed: 0, 0, 0, 0m, 0, 0, 0));

        Assert.Equal(0d, stats.FailureRate);
    }

    [Fact]
    public async Task FR298_prompt_stats_fail_closed_without_a_project_set()
    {
        await using var db = new OfflineEngineDbContext();
        var queries = new RunLogQueries(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() => queries.PromptStatsAsync("sdlc", null!, TestContext.Current.CancellationToken));

        // No visible project: nothing, and no query (the context is never connected).
        Assert.Empty(await queries.PromptStatsAsync("sdlc", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FR298_all_projects_stats_are_a_separate_explicitly_named_method()
    {
        var methods = typeof(RunLogQueries).GetMethods().Where(m => m.Name.StartsWith("PromptStats", StringComparison.Ordinal)).ToList();

        // M60: the visible-projects overload takes the caller's visibility query (composed in SQL); still fail-closed.
        Assert.Equal(["PromptStatsAsync", "PromptStatsForAllProjectsAsync", "PromptStatsForVisibleProjectsAsync"], methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        var scoped = methods.Single(m => m.Name == "PromptStatsAsync").GetParameters().Single(p => p.Name == "projectIds");
        Assert.Equal(System.Reflection.NullabilityState.NotNull, new System.Reflection.NullabilityInfoContext().Create(scoped).ReadState);
    }

    [Fact]
    public void FR298_latency_is_null_when_no_run_streamed()
    {
        var stats = RunLogStats.Summarise(new PromptRunSums("idea_refinement", "1", 2, 2, 0, 10, 10, 0m, 0, 0, 20));

        Assert.Null(stats.AvgLatencyMs);
        Assert.Equal(0d, stats.FailureRate);
    }

    [Fact]
    public void FR298_an_empty_group_averages_to_zero()
    {
        var stats = RunLogStats.Summarise(new PromptRunSums("a", "1", 0, 0, 0, 0, 0, 0m, 0, 0, 0));

        Assert.Equal((0d, 0d, 0m, (double?)null, 0d, 0d), (stats.AvgInputTokens, stats.AvgOutputTokens, stats.AvgCost, stats.AvgLatencyMs, stats.AvgDurationMs, stats.FailureRate));
    }

    [Fact]
    public void FR298_the_average_cost_is_rounded_to_6_decimals()
    {
        var stats = RunLogStats.Summarise(new PromptRunSums("a", "1", 3, 3, 0, 0, 0, 0.0001m, 0, 0, 0));

        Assert.Equal(0.000033m, stats.AvgCost);
    }

    [Fact]
    public void FR298_groups_are_sorted_by_agent_then_prompt_version()
    {
        var stats = RunLogStats.Summarise(
        [
            new PromptRunSums("spec_generator", "2", 1, 1, 0, 0, 0, 0m, 0, 0, 0),
            new PromptRunSums("idea_refinement", "1", 1, 1, 0, 0, 0, 0m, 0, 0, 0),
            new PromptRunSums("spec_generator", "1", 1, 1, 0, 0, 0, 0m, 0, 0, 0),
        ]);

        Assert.Equal(
            [("idea_refinement", "1"), ("spec_generator", "1"), ("spec_generator", "2")],
            stats.Select(s => (s.Agent, s.PromptVersion)));
    }
}
