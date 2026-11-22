using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// D-0.7.0-42, FR-295: <see cref="AgentStageRequest.AgentKey"/> runs one of a stage's <c>agents:</c> bindings through the
/// engine runner with its own prompt, inputs, output and tools; without it the stage's primary agent runs as before.
/// The fixture's check stage gains <c>reviewer</c> (no output) and <c>scribe</c> (writes the summary).
/// </summary>
public class AgentBindingRunnerTests
{
    private const string Bindings = """
          - id: check
            agents:
              - { key: reviewer, job_type: stage.run, prompt: fixture.check, inputs: [draft], tools: [fixture.lookup] }
              - { key: scribe, job_type: gate.timer, prompt: fixture.summarise, inputs: [draft, report], output: summary }

        """;

    private static string AddBindings(string yaml)
    {
        Assert.Contains("  - id: check\n", yaml, StringComparison.Ordinal);
        return yaml.Replace("  - id: check\n", Bindings, StringComparison.Ordinal);
    }

    private static AgentStageHarness Harness(IFakeAgentResponder[] responders, AgentStageExtension? extension = null) =>
        new(responders, AddBindings, extension: extension);

    private static Task<AgentStageResult> RunAsync(AgentStageHarness harness, string? agentKey) =>
        harness.Coordinator.RunAsync(
            new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered) { AgentKey = agentKey },
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(null)]
    [InlineData("checker")]
    public async Task D_0_7_0_42_no_agent_key_or_the_primary_key_runs_the_primary_agent(string? agentKey)
    {
        var reviewer = new ScriptedResponder("reviewer", "unused");
        using var harness = Harness([new ScriptedResponder("checker", AgentStageHarness.ValidReport), reviewer]);

        var result = await RunAsync(harness, agentKey);

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(("report", "checker", "c1"), (result.Last.Artifact!.Type, Assert.Single(harness.Store.AgentRuns).Agent, harness.Store.AgentRuns[0].PromptVersion));
        Assert.Empty(reviewer.Prompts);
    }

    [Fact]
    public async Task D_0_7_0_42_a_secondary_agent_runs_with_its_prompt_and_inputs_and_without_an_output_stores_nothing()
    {
        var reviewer = new ScriptedResponder("reviewer", "Looks fine, not JSON at all.");
        using var harness = Harness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft), reviewer]);
        await harness.RunStageAsync("draft");

        var result = await RunAsync(harness, "reviewer");

        // The stage's validate guard and report schema don't apply: the binding has no output.
        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Null(result.Last.Artifact);
        Assert.Equal(["draft"], harness.Store.Artifacts.Select(a => a.Type));
        Assert.Contains(AgentStageHarness.ValidDraft, Assert.Single(reviewer.Prompts), StringComparison.Ordinal);
        var run = harness.Store.AgentRuns[^1];
        Assert.Equal(("reviewer", "check", "c1", AgentRunStatus.Succeeded), (run.Agent, run.Stage, run.PromptVersion, run.Status));
        Assert.Equal("draft", Assert.Single(run.InputArtifacts).Type);
        Assert.Equal(("reviewer", EngineJobTypes.StageRun, (string?)null), (result.Last.Stage.Agent!.Key, result.Last.Stage.Agent.JobType, result.Last.Stage.Output));
        Assert.Equal(["fixture.lookup"], result.Last.Stage.Agent.Tools);
    }

    [Fact]
    public async Task D_0_7_0_42_a_secondary_agent_with_an_output_stores_its_artifact()
    {
        using var harness = Harness(
        [
            new ScriptedResponder("drafter", AgentStageHarness.ValidDraft),
            new ScriptedResponder("checker", AgentStageHarness.ValidReport),
            new ScriptedResponder("scribe", "# Summary\n\nBy the scribe."),
        ]);
        await harness.RunStageAsync("draft");
        await harness.RunStageAsync("check");
        harness.Lock("report");

        var result = await RunAsync(harness, "scribe");

        Assert.Equal(("summary", "# Summary\n\nBy the scribe."), (result.Last.Artifact!.Type, result.Last.Artifact.Content));
        var run = harness.Store.AgentRuns[^1];
        Assert.Equal(("scribe", "check", "s1"), (run.Agent, run.Stage, run.PromptVersion));
        Assert.Equal(run.Id, result.Last.Artifact.AgentRunId);
        Assert.Equal(["draft", "report"], run.InputArtifacts.Select(a => a.Type));
    }

    [Fact]
    public async Task D_0_7_0_42_an_agent_key_the_stage_does_not_bind_fails_with_VALIDATION_and_no_model_call()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = Harness([checker]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(harness, "drafter"));

        Assert.Equal((false, EngineErrorCodes.Validation), (ex.Transient, ex.ErrorCode));
        Assert.Contains("binds no agent 'drafter'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(checker.Prompts);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task D_0_7_0_42_FR313_the_store_override_applies_to_a_secondary_agent_without_an_output()
    {
        var hostArtifact = new Artifact { Type = "draft", Content = "stored by the host" };
        var extension = new OverrideExtension(new AgentStoreOverride(hostArtifact));
        using var harness = Harness([new ScriptedResponder("reviewer", "review text")], extension);

        var result = await RunAsync(harness, "reviewer");

        Assert.Same(hostArtifact, result.Last.Artifact);
        Assert.Equal(["review text"], extension.Contents);
        Assert.True(extension.SawSecondary);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task D_0_7_0_42_FR310_a_redelivered_secondary_without_an_output_calls_the_store_override_again()
    {
        var hostArtifact = new Artifact { Type = "draft", Content = "stored by the host" };
        var extension = new OverrideExtension(new AgentStoreOverride(hostArtifact));
        var reviewer = new ScriptedResponder("reviewer", "review text");
        using var harness = Harness([reviewer], extension);
        var agentRunId = Guid.CreateVersion7();
        var request = new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered) { AgentKey = "reviewer", AgentRunId = agentRunId };

        await harness.Coordinator.RunAsync(request, TestContext.Current.CancellationToken);
        var again = await harness.Coordinator.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(again.Last.Redelivered);
        Assert.Same(hostArtifact, again.Last.Artifact);
        Assert.Equal(["review text", "review text"], extension.Contents);
        Assert.Single(reviewer.Prompts);
        Assert.Equal(agentRunId, Assert.Single(harness.Store.AgentRuns).Id);
    }

    private sealed class OverrideExtension(AgentStoreOverride stored) : AgentStageExtension
    {
        public override string PipelineId => "fixture-review";

        public List<string> Contents { get; } = [];

        public bool SawSecondary { get; private set; }

        public override Task<AgentStoreOverride?> StoreOverrideAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
        {
            SawSecondary = attempt.IsSecondaryAgent;
            Contents.Add(content);
            return Task.FromResult<AgentStoreOverride?>(stored);
        }
    }
}
