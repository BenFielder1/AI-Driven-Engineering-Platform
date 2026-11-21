using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>§4.60: the order of the hooks around an agent stage attempt, with the host committing the run separately.</summary>
public class AgentStageHookOrderTests
{
    private const string Invalid = """{"consistent":false,"findings":["weekly volume +25% > 10%"]}""";

    private static readonly string[] SuccessfulAttempt =
    [
        "pre-run", "budget:checker", "describe", "started", "commit:Started:None", "pre-render", "pre-call", "options", "commit:Calling:None",
        "tokens:checker", "commit:Answered:Valid", "transform", "should-store", "created:report:1", "stored:report",
    ];

    /// <summary>The fixture with a separately committing extension that records into the engine hooks' list (one ordered list).</summary>
    private static (AgentStageHarness Harness, RecordingExtension Extension) Ordered(ILlmProvider provider)
    {
        var extension = new RecordingExtension([], separate: true);
        var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"), extension: extension);
        extension.Calls = harness.Hooks.Calls;
        return (harness, extension);
    }

    private static Task<JobFailedException> Throws(Func<Task> run) => Assert.ThrowsAsync<JobFailedException>(run);

    [Fact]
    public async Task Section4_60_success_first_time()
    {
        var (harness, extension) = Ordered(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;

        await harness.RunStageAsync("check", Guid.CreateVersion7());

        Assert.Equal(SuccessfulAttempt, extension.Calls);
    }

    [Fact]
    public async Task Section4_60_under_the_default_policy_the_answered_commit_still_carries_every_verdict()
    {
        var extension = new RecordingExtension([]);
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(new SequenceProvider(Invalid, AgentStageHarness.ValidReport), "sk-test"), extension: extension);
        extension.Calls = harness.Hooks.Calls;

        await harness.RunStageAsync("check");

        Assert.Equal(
            [
                "pre-run", "budget:checker", "describe", "started", "pre-render", "pre-call", "options", "tokens:checker", "commit:Answered:Retry", "next-attempt",
                "pre-run", "budget:checker", "describe", "started", "pre-render", "pre-call", "options", "tokens:checker", "commit:Answered:Valid", "transform", "should-store", "created:report:1", "stored:report",
            ],
            extension.Calls);
    }

    [Fact]
    public async Task Section4_60_invalid_then_valid_retries_from_step_1_with_the_corrective_input()
    {
        var provider = new SequenceProvider(Invalid, AgentStageHarness.ValidReport);
        var (harness, extension) = Ordered(provider);
        using var _ = harness;

        await harness.RunStageAsync("check", Guid.CreateVersion7());

        Assert.Equal(
            [
                "pre-run", "budget:checker", "describe", "started", "commit:Started:None", "pre-render", "pre-call", "options", "commit:Calling:None",
                "tokens:checker", "commit:Answered:Retry", "next-attempt", .. SuccessfulAttempt,
            ],
            extension.Calls);
    }

    [Fact]
    public async Task Section4_60_a_pre_run_refusal_stops_before_the_budget()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, extension) = Ordered(provider);
        using var _ = harness;
        extension.RefuseBeforeRun = new AgentStageFailure("HOST_REFUSED", "No.");

        await Throws(() => harness.RunStageAsync("check", Guid.CreateVersion7()));

        Assert.Equal(["pre-run"], extension.Calls);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Section4_60_a_budget_refusal_reaches_the_host_before_any_run()
    {
        var (harness, extension) = Ordered(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        harness.Hooks.Refuse = "Budget used up.";

        await Throws(() => harness.RunStageAsync("check", Guid.CreateVersion7()));

        Assert.Equal(["pre-run", "budget:checker", "budget-exceeded"], extension.Calls);
    }

    [Fact]
    public async Task Section4_60_a_redelivered_succeeded_run_stops_at_step_3()
    {
        var (harness, extension) = Ordered(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        var id = Guid.CreateVersion7();
        await harness.RunStageAsync("check", id);
        extension.Calls.Clear();

        await harness.RunStageAsync("check", id);

        Assert.Equal(["pre-run"], extension.Calls);
    }

    [Fact]
    public async Task Section4_60_a_transient_failure_keeps_the_run_running_and_the_redelivery_resumes_it()
    {
        var (harness, extension) = Ordered(new FlakyProvider(AgentStageHarness.ValidReport, failures: 1));
        using var _ = harness;
        extension.KeepRunningOnTransient = true;
        var id = Guid.CreateVersion7();

        await Throws(() => harness.RunStageAsync("check", id));
        var first = extension.Calls.ToList();
        extension.Calls.Clear();
        await harness.RunStageAsync("check", id);

        Assert.Equal(
            ["pre-run", "budget:checker", "describe", "started", "commit:Started:None", "pre-render", "pre-call", "options", "commit:Calling:None", "call-failed:PROVIDER_ERROR", "commit:KeptRunning:None"],
            first);
        Assert.Equal(
            ["pre-run", "started:resumed", "pre-render", "pre-call", "options", "commit:Calling:None", "tokens:checker", "commit:Answered:Valid", "transform", "should-store", "created:report:1", "stored:report"],
            extension.Calls);
    }

    [Fact]
    public async Task Section4_60_a_permanent_failure_fires_the_failed_hook_and_commits_the_failed_run()
    {
        var (harness, extension) = Ordered(new FlakyProvider(AgentStageHarness.ValidReport, failures: 1, transient: false, status: 400));
        using var _ = harness;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", Guid.CreateVersion7()));

        Assert.Equal((false, EngineErrorCodes.ProviderError), (ex.Transient, ex.ErrorCode));
        Assert.Equal(
            ["pre-run", "budget:checker", "describe", "started", "commit:Started:None", "pre-render", "pre-call", "options", "commit:Calling:None", "call-failed:PROVIDER_ERROR", "failed:PROVIDER_ERROR", "commit:Failed:None"],
            extension.Calls);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }
}
