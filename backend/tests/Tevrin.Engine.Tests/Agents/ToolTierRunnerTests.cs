using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-358 runtime (M71b) through the runner: the stage's tools are checked against the registry when the stage runs, not
/// only when the definition was validated; an external tool waits on its <c>external_action</c> gate (FR-360,
/// <see cref="ExternalToolRunnerTests"/>), and a destructive one on its <c>tool_approval</c> gate (FR-359,
/// <see cref="ToolApprovalRunnerTests"/>).
/// </summary>
public class ToolTierRunnerTests
{
    private const string DrafterLine = "    agent: { key: drafter, prompt: fixture.draft, tools: [fixture.lookup] }\n";

    private static string WithDrafter(string yaml, string tools, string? maxTier)
    {
        Assert.Contains(DrafterLine, yaml, StringComparison.Ordinal);
        var policy = maxTier is null ? string.Empty : $"    tool_policy: {{ max_tier: {maxTier} }}\n";
        return yaml.Replace(DrafterLine, $"    agent: {{ key: drafter, prompt: fixture.draft, tools: [{tools}] }}\n{policy}", StringComparison.Ordinal);
    }

    private static List<(string Tool, string? Tier, bool IsError)> Calls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
            e.GetProperty("isError").GetBoolean()))];
    }

    [Fact]
    public async Task FR358_the_runner_records_the_tier_of_the_stage_tool_it_called()
    {
        var script = new FakeToolScript("drafter", [new FakeToolCall("lookup", """{"q":"pace"}""")]);
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], scripts: [script]);

        await harness.RunStageAsync("draft");

        // fixture.lookup is registered without a tier, so it is write (D-0.8.0-28), under the default ceiling.
        Assert.Equal([("lookup", "write", false)], Calls(harness.Store.AgentRuns[0].ToolCalls));
    }

    [Fact]
    public async Task FR358_the_runner_refuses_unlisted_tools_and_never_runs_them()
    {
        var log = new ToolRunLog();
        var script = new FakeToolScript("drafter",
        [
            new FakeToolCall("scan", "{}"), new FakeToolCall("ghost", "{}"),
        ]);
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)],
            yaml => WithDrafter(yaml, $"{TierToolsPack.Scan}, {TierToolsPack.Wipe}, {TierToolsPack.Send}", "destructive"),
            scripts: [script],
            configure: s => TierToolsPack.AddTo(s, log));

        var result = await harness.RunStageAsync("draft");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(["scan"], log.Ran);
        // FR-360 (M71d): the external send is no longer refused here; it waits on an external_action gate
        // (ExternalToolRunnerTests).
        Assert.Equal(
            [("scan", "read", false), ("ghost", null, true)],
            Calls(harness.Store.AgentRuns[0].ToolCalls));
    }

    [Fact]
    public async Task FR358_a_pinned_run_does_not_offer_a_tool_its_pack_raised_above_the_ceiling_since()
    {
        var log = new ToolRunLog();
        var script = new FakeToolScript("drafter", [new FakeToolCall("peek", "{}")]);
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)],
            scripts: [script],
            configure: s => TierToolsPack.AddTo(s, log));

        // The run is pinned to a stored definition that listed tiers.peek under max_tier: read, as validated when the
        // tool was read; the pack registers it as write now. Stored definitions aren't re-validated for tiers. The read
        // scan tool keeps the tool loop open, so the model's call to peek reaches the executor.
        var yaml = WithDrafter(Fixtures.Read("fixture-review.pipeline.yaml"), $"{TierToolsPack.Scan}, {TierToolsPack.Peek}", "read");
        var pinned = PipelineDefinitionLoader.Parse(yaml).Definition!;
        var hash = DefinitionHash.Compute(pinned);
        harness.Services.GetRequiredService<PipelineDefinitionCache>().Add(hash, new StoredPipelineDefinition("fixture", pinned));
        harness.Store.Runs.Remove(harness.Run);
        var run = new PipelineRun
        {
            ProjectId = harness.Run.ProjectId,
            PipelineId = harness.Run.PipelineId,
            DefinitionHash = hash,
            SubjectId = harness.Run.SubjectId,
            CurrentStage = "draft",
            PackStatus = "drafting",
            ApprovalMode = harness.Run.ApprovalMode,
        };
        harness.Store.Runs.Add(run);

        var result = await harness.Coordinator.RunAsync(
            new AgentStageRequest(run.Id, "draft", JobTriggers.StageEntered), TestContext.Current.CancellationToken);

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Empty(log.Ran);
        Assert.Equal([("peek", "write", true)], Calls(Assert.Single(harness.Store.AgentRuns).ToolCalls));
    }
}
