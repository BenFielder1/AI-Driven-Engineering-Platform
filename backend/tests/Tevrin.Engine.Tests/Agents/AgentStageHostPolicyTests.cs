using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-310–FR-316: the host policies a 0.6.1-style host needs (attempts, cancellation, check order, output, veto).</summary>
public class AgentStageHostPolicyTests
{
    private const string Invalid = """{"consistent":false,"findings":["weekly volume +25% > 10%"]}""";

    private static string FailAfter(string yaml, int attempts) =>
        yaml.Replace("then: escalate", "then: fail", StringComparison.Ordinal).Replace("max_attempts: 3", $"max_attempts: {attempts}", StringComparison.Ordinal);

    private static (AgentStageHarness Harness, RecordingExtension Extension) With(ILlmProvider provider, bool separate = false, string? apiKey = "sk-test", Func<string, string>? yaml = null)
    {
        var extension = new RecordingExtension([], separate);
        return (new AgentStageHarness([], yaml, resolver: new KeyedResolver(provider, apiKey!), extension: extension), extension);
    }

    [Fact]
    public async Task FR314_FR310_a_then_fail_stage_stores_the_hosts_code_and_its_redelivery_fails_with_it_without_a_call()
    {
        var provider = new SequenceProvider(Invalid);
        var (harness, extension) = With(provider, separate: true, yaml: y => FailAfter(y, 2));
        using var _ = harness;
        extension.Exhausted = new AgentStageFailure("HOST_INVALID", "Still invalid.");
        extension.RejectedError = (findings, verdict, failure) => failure is null ? null : $"{failure.ErrorCode}: {failure.Message}";
        var id = Guid.CreateVersion7();

        var result = await harness.RunStageAsync("check", id);
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", id));

        Assert.Equal(AgentStageEnd.Failed, result.End);
        Assert.Equal([(AgentRunStatus.Succeeded, (string?)null), (AgentRunStatus.Failed, "HOST_INVALID: Still invalid.")], harness.Store.AgentRuns.Select(r => (r.Status, r.Error)));
        Assert.Equal((false, "HOST_INVALID"), (ex.Transient, ex.ErrorCode));
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task FR314_FR310_by_default_a_then_fail_runs_redelivery_fails_with_VALIDATION()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", Invalid)], y => FailAfter(y, 1));
        var id = Guid.CreateVersion7();
        await harness.RunStageAsync("check", id);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", id));

        Assert.Equal((false, EngineErrorCodes.Validation), (ex.Transient, ex.ErrorCode));
    }

    [Fact]
    public async Task FR315_a_cancellation_after_the_answer_keeps_the_run_and_its_tokens_and_the_redelivery_makes_no_call()
    {
        using var cancel = new CancellationTokenSource();
        var provider = new FlakyProvider(AgentStageHarness.ValidReport, onAnswer: cancel.Cancel);
        var (harness, extension) = With(provider, separate: true);
        using var _ = harness;
        var transforms = 0;
        extension.Transform = content => ++transforms == 1 ? throw new OperationCanceledException(cancel.Token) : content;
        var id = Guid.CreateVersion7();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Coordinator.RunAsync(new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered) { AgentRunId = id }, cancel.Token));
        var run = Assert.Single(harness.Store.AgentRuns);
        var tokens = harness.Hooks.Tokens;
        await harness.RunStageAsync("check", id);

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(15, tokens);
        Assert.False(extension.CommitSawCancellation);
        Assert.Contains("commit:Answered:Valid", extension.Calls);
        Assert.Single(provider.Requests);
        Assert.Equal(id, Assert.Single(harness.Store.Artifacts).AgentRunId);
    }

    [Fact]
    public async Task FR310_a_new_run_starts_at_zero_attempts_and_counts_each_call()
    {
        var (refused, refusing) = With(new FlakyProvider(AgentStageHarness.ValidReport), separate: true);
        using var _ = refused;
        refusing.RefuseBeforeCall = new AgentStageFailure("HOST_REFUSED", "No.");
        var (called, __) = With(new FlakyProvider(AgentStageHarness.ValidReport), separate: true);
        using var ___ = called;

        await Assert.ThrowsAsync<JobFailedException>(() => refused.RunStageAsync("check"));
        await called.RunStageAsync("check");

        Assert.Equal(0, Assert.Single(refused.Store.AgentRuns).Attempts);
        Assert.Equal(1, Assert.Single(called.Store.AgentRuns).Attempts);
    }

    [Fact]
    public async Task FR316_a_disabled_provider_refusal_wins_over_the_missing_key()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport), separate: true, apiKey: null);
        using var _ = harness;
        extension.RefuseBeforeCall = new AgentStageFailure("HOST_PROVIDER_OFF", "Turned off.");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check"));

        Assert.Equal("HOST_PROVIDER_OFF", ex.ErrorCode);
        Assert.StartsWith("HOST_PROVIDER_OFF: ", Assert.Single(harness.Store.AgentRuns).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR316_a_pre_render_refusal_wins_over_a_missing_input()
    {
        var (harness, extension) = With(new FlakyProvider("x"), separate: true);
        using var _ = harness;
        extension.RefuseBeforeRender = new AgentStageFailure("HOST_TOO_LARGE", "Too large.");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("summarise"));

        Assert.Equal("HOST_TOO_LARGE", ex.ErrorCode);
        Assert.Contains("failed:HOST_TOO_LARGE", extension.Calls);
    }

    [Fact]
    public async Task FR312_host_warnings_and_its_serializer_shape_the_stored_output()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        extension.Options = new AgentCallOptions(MaxTokens: 50) { Warnings = ["summary_wait_timeout", " ", "summary_wait_timeout"] };
        AgentStageRunOutput? seen = null;
        extension.Serialize = output =>
        {
            seen = output;
            return "host-json";
        };

        await harness.RunStageAsync("check");

        Assert.Equal("host-json", Assert.Single(harness.Store.AgentRuns).Output);
        Assert.Equal(["summary_wait_timeout"], seen!.Warnings!);
        Assert.Equal(50, seen.MaxOutputTokens);
    }

    [Fact]
    public async Task FR312_FR314_the_host_sets_the_stored_user_feedback_and_can_drop_the_engine_heading()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var extension = new RecordingExtension([]) { Metadata = new AgentRunMetadata { SetsUserFeedback = true, UserFeedback = null } };
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"), extension: extension);

        await harness.Coordinator.RunAsync(
            new AgentStageRequest(harness.Run.Id, "check", JobTriggers.GateRejected, "Too vague") { AppendFeedback = false },
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(harness.Store.AgentRuns).UserFeedback);
        Assert.DoesNotContain(AgentStagePrompt.FeedbackHeading, provider.Requests[0].Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR316_a_store_veto_skips_the_artifact_silently()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport));
        using var _ = harness;
        extension.Veto = true;

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Null(result.Last.Artifact);
        Assert.Empty(harness.Store.Artifacts);
        Assert.Equal(AgentRunStatus.Succeeded, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR310_FR315_by_default_a_transient_failure_under_a_caller_id_stays_running_for_the_retry()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport, failures: 1);
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"));
        var id = Guid.CreateVersion7();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", id));
        var run = Assert.Single(harness.Store.AgentRuns);
        var status = run.Status;
        await harness.RunStageAsync("check", id);

        Assert.True(ex.Transient);
        Assert.Equal(AgentRunStatus.Running, status);
        Assert.Equal((AgentRunStatus.Succeeded, 2), (run.Status, run.Attempts));
    }

    [Fact]
    public async Task FR310_a_caller_id_of_another_pipeline_run_is_refused()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"));
        var id = Guid.CreateVersion7();
        harness.Store.AgentRuns.Add(new AgentRun { Id = id, ProjectId = harness.Run.ProjectId, PipelineRunId = Guid.CreateVersion7(), Stage = "check", Status = AgentRunStatus.Succeeded });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", id));

        Assert.Equal((false, EngineErrorCodes.Validation), (ex.Transient, ex.ErrorCode));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task FR311_under_the_default_policy_a_new_run_failing_before_the_call_fires_no_failed_hook_and_saves_nothing()
    {
        var (harness, extension) = With(new FlakyProvider("x"));
        using var _ = harness;

        await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("summarise"));

        Assert.DoesNotContain(extension.Calls, c => c.StartsWith("failed:", StringComparison.Ordinal));
        Assert.Equal(0, harness.Store.Saves);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR315_keeping_a_permanent_failure_running_is_refused()
    {
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport, failures: 1), separate: true);
        using var _ = harness;
        extension.ForceKeepRunning = true;
        extension.RewriteFailure = new AgentStageFailure("HOST_PERMANENT", "Permanent.", Transient: false);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", Guid.CreateVersion7()));

        Assert.False(ex.Transient);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
        Assert.Contains("failed:HOST_PERMANENT", extension.Calls);
    }

    [Fact]
    public async Task FR310_FR312_a_run_recorded_under_another_stage_is_still_redelivered()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport);
        var (harness, extension) = With(provider, separate: true);
        using var _ = harness;
        extension.Metadata = new AgentRunMetadata { Stage = "refinement" };
        var id = Guid.CreateVersion7();

        await harness.RunStageAsync("check", id);
        var again = await harness.RunStageAsync("check", id);

        Assert.Equal("refinement", Assert.Single(harness.Store.AgentRuns).Stage);
        Assert.True(again.Last.Redelivered);
        Assert.Single(provider.Requests);
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR310_FR315_by_default_a_transient_failure_on_the_queues_last_attempt_fails_the_run()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidReport, failures: 1);
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"));
        var request = new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered) { AgentRunId = Guid.CreateVersion7(), JobAttempt = 4 };

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.Coordinator.RunAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(EngineErrorCodes.ProviderError, ex.ErrorCode);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(harness.Store.AgentRuns).Status);
    }

    [Fact]
    public async Task FR315_under_the_default_policy_a_cancellation_after_the_answer_still_saves_the_run_and_its_artifact()
    {
        using var cancel = new CancellationTokenSource();
        var (harness, extension) = With(new FlakyProvider(AgentStageHarness.ValidReport, onAnswer: cancel.Cancel));
        using var _ = harness;
        extension.ObserveStoreCancellation = true;

        var result = await harness.Coordinator.RunAsync(new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered), cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Contains("transform", extension.Calls);
        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(AgentRunStatus.Succeeded, Assert.Single(harness.Store.AgentRuns).Status);
        Assert.Single(harness.Store.Artifacts);
        Assert.True(harness.Store.Saves > 0);
    }
}
