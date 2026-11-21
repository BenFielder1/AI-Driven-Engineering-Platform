using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318 (FR-25, FR-26, FR-78, FR-315): <c>spec_generator</c>'s provider failures and budget refusal on the engine
/// runner, with 0.6.1's run states, codes, events and payloads (<c>AgentRunnerTests</c> is the reference).
/// </summary>
public class SdlcSpecGeneratorFailureTests
{
    [Fact]
    public async Task FR318_spec_generator_transient_failure_keeps_the_run_running_then_the_retry_succeeds()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Provider.Failures.Enqueue(new LlmProviderException("overloaded", transient: true, statusCode: 529));
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

        Assert.Equal((true, "PROVIDER_ERROR"), (ex.Transient, ex.ErrorCode));
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Running, 1, "PROVIDER_ERROR: overloaded", (DateTimeOffset?)null), (run.Status, run.Attempts, run.Error, run.CompletedAt));
        Assert.Equal(["RUN_STARTED"], harness.Data.Events.Select(e => e.Type));

        await harness.RunAsync(job with { Attempt = 2 });

        run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Succeeded, 2, (string?)null), (run.Status, run.Attempts, run.Error));
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task FR318_spec_generator_transient_failure_on_the_last_attempt_fails_the_run_dead_lettered()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Provider.Failures.Enqueue(new LlmProviderException("overloaded", transient: true, statusCode: 529));
        var job = SdlcAgentStageHarness.Job(attempt: RetryPolicy.Default.MaxAttempts);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

        Assert.Equal((true, "PROVIDER_ERROR"), (ex.Transient, ex.ErrorCode));
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Failed, "PROVIDER_ERROR: overloaded"), (run.Status, run.Error));
        Assert.Equal(["RUN_STARTED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = run.Id, agent = SdlcAgents.SpecGenerator, errorCode = "PROVIDER_ERROR", error = run.Error, attempts = 1, deadLettered = true }),
            harness.Data.Events[1].Payload);
    }

    [Fact]
    public async Task FR318_FR78_a_rejected_bound_key_invalidates_the_connection_and_fails_with_CREDENTIAL_INVALID()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Provider.Failures.Enqueue(new LlmProviderException("unauthorized", transient: false, statusCode: 401));

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(SdlcAgentStageHarness.Job()));

        Assert.Equal((false, "CREDENTIAL_INVALID"), (ex.Transient, ex.ErrorCode));
        Assert.Equal("The bound fake connection's key was rejected (HTTP 401); its owner must replace it.", ex.Message);
        Assert.Equal(["RUN_STARTED", "CONNECTION_INVALID", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR318_FR26_a_project_over_its_budget_is_refused_with_its_event_and_no_run_or_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Data.Project.TokenBudget = 100;
        harness.Data.Project.TokensUsed = 100;
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

        Assert.Equal((false, "BUDGET_EXCEEDED"), (ex.Transient, ex.ErrorCode));
        Assert.Empty(harness.Provider.Requests);
        Assert.Empty(harness.Store.AgentRuns);
        var budgetEvent = Assert.Single(harness.Data.Events);
        Assert.Equal(
            ("BUDGET_EXCEEDED", AideJson.Serialize(new { agent = SdlcAgents.SpecGenerator, tokenBudget = (long?)100, tokensUsed = 100L, jobId = job.Id })),
            budgetEvent);
    }

    [Fact]
    public async Task FR318_a_missing_project_request_fails_the_run_with_MISSING_INPUT_without_a_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(SdlcAgentStageHarness.Job(), missingInput: true));

        Assert.Equal("MISSING_INPUT", ex.ErrorCode);
        Assert.Empty(harness.Provider.Requests);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal((AgentRunStatus.Failed, 0), (run.Status, run.Attempts));
        Assert.StartsWith("MISSING_INPUT: ", run.Error, StringComparison.Ordinal);
        Assert.Equal(["RUN_STARTED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
    }
}
