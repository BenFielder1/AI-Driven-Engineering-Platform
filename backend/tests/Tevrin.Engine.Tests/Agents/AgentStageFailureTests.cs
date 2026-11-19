using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-295, FR-25, FR-297: provider failures, latency and budget hooks of the agent stage runner.</summary>
public class AgentStageFailureTests
{
    private const string Key = "sk-ant-api03-abcdefghijklmnop";

    private static AgentStageHarness With(ILlmProvider provider) => new([], resolver: new KeyedResolver(provider, Key));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR295_a_provider_error_is_stored_sanitized_and_fails_the_job_as_the_provider_classified_it(bool transient)
    {
        var failure = new LlmProviderException($"401 invalid x-api-key {Key}", transient, 401);
        using var harness = With(new StreamingProvider(AgentStageHarness.ValidDraft, failure));

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((transient, EngineErrorCodes.ProviderError), (ex.Transient, ex.ErrorCode));
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.StartsWith("PROVIDER_ERROR: ", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, run.Input, StringComparison.Ordinal);
        Assert.NotNull(run.CompletedAt);
        Assert.True(harness.Store.Saves > 0);
    }

    [Fact]
    public async Task FR295_an_unexpected_exception_is_stored_by_type_only_and_is_transient_for_network_errors()
    {
        using var harness = With(new StreamingProvider("x", new HttpRequestException($"connect failed {Key}")));

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.True(ex.Transient);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Failed, "PROVIDER_ERROR: anthropic call failed unexpectedly (HttpRequestException)."), (run.Status, run.Error));
    }

    [Fact]
    public async Task FR295_a_format_error_from_the_provider_is_permanent()
    {
        using var harness = With(new StreamingProvider("x", new FormatException("bad header")));

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.False(ex.Transient);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR295_a_cancelled_call_saves_the_run_as_cancelled()
    {
        using var harness = With(new StreamingProvider("x", new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.RunStageAsync("draft"));

        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(harness.Store.AgentRuns).Status);
        Assert.True(harness.Store.Saves > 0);
    }

    [Fact]
    public async Task FR297_a_streamed_answer_records_its_latency_and_the_fake_provider_none()
    {
        using var streamed = With(new StreamingProvider(AgentStageHarness.ValidDraft));
        using var fake = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)]);

        await streamed.RunStageAsync("draft");
        await fake.RunStageAsync("draft");

        Assert.NotNull(Assert.Single(streamed.Store.AgentRuns).LatencyMs);
        Assert.Null(Assert.Single(fake.Store.AgentRuns).LatencyMs);
    }

    [Fact]
    public async Task FR295_FR26_a_budget_refusal_fails_the_job_before_the_model_is_called()
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        using var harness = new AgentStageHarness([drafter]);
        harness.Hooks.Refuse = "The project's token budget is used up.";

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((false, EngineErrorCodes.BudgetExceeded), (ex.Transient, ex.ErrorCode));
        Assert.Empty(drafter.Prompts);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR295_hooks_see_tokens_and_created_and_superseded_artifacts()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", AgentStageHarness.ValidReport)]);

        await harness.RunStageAsync("check");
        await harness.RunStageAsync("check");

        Assert.Equal(
            ["budget:checker", "tokens:checker", "created:report:1", "budget:checker", "tokens:checker", "superseded:report:1>2", "created:report:2"],
            harness.Hooks.Calls);
        Assert.Equal(harness.Store.AgentRuns.Sum(r => (long)r.InputTokens + r.OutputTokens), harness.Hooks.Tokens);
    }
}
