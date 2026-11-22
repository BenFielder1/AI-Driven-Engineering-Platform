using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-311–FR-316: the host hooks of the agent stage runner, on the fixture pipeline with the Fake provider.</summary>
public class AgentStageHostHookTests
{
    private const string Invalid = """{"consistent":false,"findings":["weekly volume +25% > 10%"]}""";

    private static (AgentStageHarness Harness, RecordingExtension Extension) With(ILlmProvider provider, bool separate = false)
    {
        var extension = new RecordingExtension([], separate);
        return (new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"), extension: extension), extension);
    }

    [Fact]
    public async Task FR311_started_and_answered_hooks_fire_and_the_budget_refusal_is_saved()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport), separate: true);
        using var _ = harness;
        await harness.RunStageAsync("check");
        harness.Hooks.Refuse = "Budget used up.";
        var saves = harness.Store.Saves;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check"));

        Assert.Equal(EngineErrorCodes.BudgetExceeded, ex.ErrorCode);
        Assert.Equal(["started", "commit:Answered:Valid", "budget-exceeded"], extension.Calls.Where(c => c is "started" or "commit:Answered:Valid" or "budget-exceeded"));
        Assert.True(harness.Store.Saves > saves);
    }

    [Fact]
    public async Task FR311_FR26_a_tool_loop_the_host_stops_between_turns_fails_the_run_with_BUDGET_EXCEEDED()
    {
        var extension = new RecordingExtension([]) { StopToolTurn = "Budget used up mid-loop." };
        var script = new FakeToolScript("drafter", [new FakeToolCall("lookup", """{"q":"pace"}""")]);
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], scripts: [script], extension: extension);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((false, EngineErrorCodes.BudgetExceeded, "Budget used up mid-loop."), (ex.Transient, ex.ErrorCode, ex.Message));
        Assert.Contains("budget-exceeded:in-run", extension.Calls);
        Assert.Contains("failed:BUDGET_EXCEEDED", extension.Calls);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR312_the_hosts_token_options_reach_the_request_and_the_stored_output()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, extension) = With(provider);
        using var _ = harness;
        extension.Options = new AgentCallOptions(MaxTokens: 1234, ContextTokens: 200_000);

        await harness.RunStageAsync("check");

        var request = Assert.Single(provider.Requests);
        Assert.Equal((1234, 200_000), (request.MaxTokens, request.ContextTokens));
        Assert.Contains("\"maxOutputTokens\":1234", Assert.Single(harness.Store.AgentRuns).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR312_without_host_options_the_request_and_output_are_unchanged()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, _) = With(provider);
        using var __ = harness;

        await harness.RunStageAsync("check");

        Assert.Equal(((int?)null, (int?)null), (provider.Requests[0].MaxTokens, provider.Requests[0].ContextTokens));
        Assert.DoesNotContain("maxOutputTokens", harness.Store.AgentRuns[0].Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR312_the_run_records_the_hosts_metadata_and_full_input_list()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        var revised = new InputArtifactRef(Guid.CreateVersion7(), "report", 3);
        var stepId = Guid.CreateVersion7();
        extension.Metadata = new AgentRunMetadata { CatalogVersion = "2026.11", Packs = "[]", AgentKey = "specialist", StepId = stepId, InputArtifacts = [revised] };

        await harness.RunStageAsync("check");

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal(("2026.11", "[]", "specialist", (Guid?)stepId), (run.CatalogVersion, run.Packs, run.AgentKey, run.StepId));
        Assert.Equal([revised], run.InputArtifacts);
    }

    [Fact]
    public async Task FR313_the_content_transform_turns_the_valid_output_into_the_stored_content()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        extension.Transform = content => $"# Report\n\n{content}";

        await harness.RunStageAsync("check");

        var artifact = Assert.Single(harness.Store.Artifacts);
        Assert.Equal($"# Report\n\n{AgentStageHarness.ValidReport}", artifact.Content);
        Assert.Equal(ContentHash.Compute(artifact.Content), artifact.ContentHash);
    }

    [Fact]
    public async Task FR314_the_corrective_input_hook_builds_the_retrys_prompt_values_and_feedback()
    {
        var provider = new SequenceProvider(Invalid, AgentStageHarness.ValidReport);
        var (harness, extension) = With(provider);
        using var _ = harness;
        extension.Retry = (current, invalid) => current with
        {
            Feedback = $"Fix it: {invalid.Findings}",
            PromptValues = new Dictionary<string, string?> { ["DRAFT"] = $"PREVIOUS {AgentStage(invalid)}" },
            Trigger = JobTriggers.ValidationFailed,
        };

        await harness.RunStageAsync("check");

        var retry = provider.Requests[1].Messages[^1].Content;
        Assert.Contains($"<draft>PREVIOUS {Invalid}</draft>", retry, StringComparison.Ordinal);
        Assert.Contains("Fix it: weekly volume +25% > 10%", retry, StringComparison.Ordinal);
        Assert.Equal("Fix it: weekly volume +25% > 10%", harness.Store.AgentRuns[1].UserFeedback);
    }

    private static string AgentStage(AgentStageOutcome invalid) => AgentStageRunner.ReadOutput(invalid.AgentRun.Output)!.Text;

    [Fact]
    public async Task FR314_the_host_names_the_failure_when_attempts_run_out()
    {
        var extension = new RecordingExtension([]) { Exhausted = new AgentStageFailure("HOST_INVALID", "Still invalid.") };
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("checker", Invalid)],
            yaml => yaml.Replace("then: escalate", "then: fail", StringComparison.Ordinal).Replace("max_attempts: 3", "max_attempts: 2", StringComparison.Ordinal),
            extension: extension);

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Failed, result.End);
        Assert.Equal([null, extension.Exhausted], extension.AnsweredFailures);
        Assert.DoesNotContain(extension.Calls, c => c.StartsWith("failed:", StringComparison.Ordinal));
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], harness.Store.AgentRuns.Select(r => r.Status));
        var ex = Assert.Throws<JobFailedException>(result.ThrowIfFailed);
        Assert.Equal((false, "HOST_INVALID", "Still invalid."), (ex.Transient, ex.ErrorCode, ex.Message));
    }

    [Fact]
    public async Task FR315_a_separate_commit_saves_the_answered_run_before_the_artifact_is_added()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport), separate: true);
        using var _ = harness;

        await harness.RunStageAsync("check");

        Assert.True(extension.Calls.IndexOf("commit:Answered:Valid") < extension.Calls.IndexOf("transform"));
        Assert.Equal(
            [(AgentRunCommitPoint.Started, AgentRunVerdict.None, AgentRunStatus.Running), (AgentRunCommitPoint.Calling, AgentRunVerdict.None, AgentRunStatus.Running), (AgentRunCommitPoint.Answered, AgentRunVerdict.Valid, AgentRunStatus.Succeeded)],
            extension.Commits);
    }

    [Fact]
    public async Task FR315_a_transient_failure_the_host_keeps_running_is_rethrown_transient_with_the_run_running()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport, failures: 1), separate: true);
        using var _ = harness;
        extension.KeepRunningOnTransient = true;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", Guid.CreateVersion7()));

        Assert.True(ex.Transient);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.StartsWith("PROVIDER_ERROR: ", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(extension.Calls, c => c.StartsWith("failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR315_the_host_can_rewrite_a_provider_failure_into_a_permanent_one()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport, failures: 1, transient: false, status: 401), separate: true);
        using var _ = harness;
        extension.RewriteFailure = new AgentStageFailure("HOST_KEY_REJECTED", "The key was rejected.");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check"));

        Assert.Equal((false, "HOST_KEY_REJECTED"), (ex.Transient, ex.ErrorCode));
        Assert.Equal("HOST_KEY_REJECTED: The key was rejected.", Assert.Single(harness.Store.AgentRuns).Error);
    }

    [Fact]
    public async Task FR315_with_a_separate_commit_a_failure_before_the_call_fails_the_stored_run()
    {
        var (harness, extension) = With(new FlakyProvider("x"), separate: true);
        using var _ = harness;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("summarise"));

        Assert.Equal(EngineErrorCodes.MissingInput, ex.ErrorCode);
        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.StartsWith("MISSING_INPUT: ", run.Error, StringComparison.Ordinal);
        Assert.Contains("failed:MISSING_INPUT", extension.Calls);
    }

    [Fact]
    public async Task FR316_a_pre_run_refusal_fails_the_job_with_its_code_and_makes_no_run_or_call()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, extension) = With(provider);
        using var _ = harness;
        extension.RefuseBeforeRun = new AgentStageFailure("HOST_DISABLED", "Turned off.");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check"));

        Assert.Equal((false, "HOST_DISABLED", "Turned off."), (ex.Transient, ex.ErrorCode, ex.Message));
        Assert.Empty(provider.Requests);
        Assert.Empty(harness.Store.AgentRuns);
        Assert.DoesNotContain("budget:checker", harness.Hooks.Calls);
    }

    [Fact]
    public async Task FR316_a_pre_call_refusal_fails_the_run_without_calling_the_model()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, extension) = With(provider, separate: true);
        using var _ = harness;
        extension.RefuseBeforeCall = new AgentStageFailure("HOST_TOO_LARGE", "Too large.");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check"));

        Assert.Equal("HOST_TOO_LARGE", ex.ErrorCode);
        Assert.Empty(provider.Requests);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
        Assert.Contains("failed:HOST_TOO_LARGE", extension.Calls);
    }

    [Fact]
    public async Task FR316_the_post_store_hook_sees_the_stored_artifact_before_it_is_saved()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;

        await harness.RunStageAsync("check");

        Assert.Equal("stored:report", extension.Calls[^1]);
        Assert.Contains("created:report:1", harness.Hooks.Calls);
    }

    [Fact]
    public async Task FR310_FR316_an_extension_of_another_pipeline_is_not_used()
    {
        var other = new OtherPipelineExtension();
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", AgentStageHarness.ValidReport)], extension: other);

        await harness.RunStageAsync("check");

        Assert.Equal(0, other.Calls);
        Assert.Equal(ArtifactStatus.Draft, Assert.Single(harness.Store.Artifacts).Status);
    }

    private sealed class OtherPipelineExtension : AgentStageExtension
    {
        public int Calls { get; private set; }

        public override string PipelineId => "other-pipeline";

        public override Task<AgentStageFailure?> CheckBeforeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<AgentStageFailure?>(new AgentStageFailure("NO", "no"));
        }
    }
}
