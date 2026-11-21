using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-311–FR-317 (M62c): an attempt's host observer, the host's preparation just before the call, the store override, the
/// failure's exception, and inputs and context the host's prompt values make unnecessary, on the fixture pipeline.
/// </summary>
public class AgentStageHostScopeTests
{
    [Fact]
    public async Task FR311_the_observer_sees_a_completed_attempt_and_is_disposed()
    {
        var extension = new ScopeExtension();
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], extension: extension);

        await harness.RunStageAsync("draft");

        Assert.Equal(["completed:True", "disposed"], extension.Observed);
    }

    [Fact]
    public async Task FR311_FR315_the_observer_sees_a_failed_attempt_and_the_failure_carries_the_redacted_exception()
    {
        var extension = new ScopeExtension();
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(new StreamingProvider("unused", new InvalidOperationException("boom with sk-test")), "sk-test"), extension: extension);

        await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal(["failed:JobFailedException", "disposed"], extension.Observed);
        Assert.Equal("InvalidOperationException", extension.CallFailure!.ExceptionType);
        Assert.Contains("boom", extension.CallFailure.ExceptionDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test", extension.CallFailure.ExceptionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR312_the_host_prepares_the_call_after_the_counted_run_is_saved()
    {
        var provider = new FlakyProvider(AgentStageHarness.ValidDraft);
        var extension = new ScopeExtension { Separate = true };
        using var harness = new AgentStageHarness([], resolver: new KeyedResolver(provider, "sk-test"), extension: extension);
        extension.Saves = () => harness.Store.Saves;

        await harness.RunStageAsync("draft");

        Assert.Equal((1, true), (extension.AttemptsWhenPrepared, extension.SavesWhenPrepared >= 2));
        Assert.Equal((int?)77, Assert.Single(provider.Requests).MaxTokens);
    }

    [Fact]
    public async Task FR313_a_store_override_is_the_attempts_artifact_and_the_engine_stores_nothing()
    {
        var hostArtifact = new Artifact { Type = "draft", Content = "stored by the host" };
        var extension = new ScopeExtension { Override = new AgentStoreOverride(hostArtifact) };
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], extension: extension);

        var result = await harness.RunStageAsync("draft");

        Assert.Same(hostArtifact, result.Last.Artifact);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR317_inputs_and_context_the_host_supplies_are_not_read_or_listed()
    {
        var notes = new NotesContextProvider();
        using var harness = new AgentStageHarness([new ScriptedResponder("summariser", "# Summary")], contextProviders: [notes]);
        Locked(harness, "draft", AgentStageHarness.ValidDraft);
        Locked(harness, "report", AgentStageHarness.ValidReport);
        var request = new AgentStageRequest(harness.Run.Id, "summarise", JobTriggers.StageEntered)
        {
            PromptValues = new Dictionary<string, string?> { ["DRAFT"] = "host draft" },
        };

        await harness.Coordinator.RunAsync(request, TestContext.Current.CancellationToken);

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal(["report"], run.InputArtifacts.Select(a => a.Type));
        Assert.Contains("DRAFT", Assert.Single(notes.Requests).SuppliedPlaceholders);
        Assert.Contains("<draft>host draft</draft>", run.Input, StringComparison.Ordinal);
    }

    private static void Locked(AgentStageHarness harness, string type, string content) =>
        harness.Store.Artifacts.Add(new Artifact
        {
            ProjectId = harness.Run.ProjectId,
            ReleaseVersionId = harness.Run.SubjectId!.Value,
            Type = type,
            Version = 1,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
        });

    private sealed class ScopeExtension : AgentStageExtension
    {
        public List<string> Observed { get; } = [];

        public bool Separate { get; init; }

        public AgentStoreOverride? Override { get; init; }

        public Func<int> Saves { get; set; } = () => 0;

        public int AttemptsWhenPrepared { get; private set; }

        public int SavesWhenPrepared { get; private set; }

        public AgentStageFailure? CallFailure { get; private set; }

        public override string PipelineId => "fixture-review";

        public override AgentRunPolicy Policy => Separate ? AgentRunPolicy.Separate : AgentRunPolicy.Default;

        public override IAgentAttemptObserver? ObserveAttempt(AgentStageRequest request) => new Observer(Observed);

        public override Task<AgentCallOptions> PrepareCallAsync(AgentAttempt attempt, AgentCallOptions options, CancellationToken cancellationToken)
        {
            AttemptsWhenPrepared = attempt.AgentRun!.Attempts;
            SavesWhenPrepared = Saves();
            return Task.FromResult(options with { MaxTokens = 77 });
        }

        public override Task<AgentStoreOverride?> StoreOverrideAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) =>
            Task.FromResult(Override);

        public override Task<AgentRunFailureDecision> OnCallFailedAsync(AgentAttempt attempt, AgentStageFailure failure, int? statusCode, CancellationToken cancellationToken)
        {
            CallFailure = failure;
            return base.OnCallFailedAsync(attempt, failure, statusCode, cancellationToken);
        }
    }

    private sealed class Observer(List<string> observed) : IAgentAttemptObserver
    {
        public void Completed(AgentStageOutcome outcome) => observed.Add($"completed:{outcome.Valid}");

        public void Failed(Exception exception) => observed.Add($"failed:{exception.GetType().Name}");

        public void Dispose() => observed.Add("disposed");
    }
}
