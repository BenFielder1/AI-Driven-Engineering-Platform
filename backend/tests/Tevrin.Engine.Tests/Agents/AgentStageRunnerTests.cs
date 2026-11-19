using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-295: the engine's agent stage runner on the fixture pipeline, with the Fake provider and no SDLC code.</summary>
public class AgentStageRunnerTests
{
    [Fact]
    public async Task FR295_the_fixture_runs_its_agent_stages_and_stores_artifacts_and_agent_runs()
    {
        using var harness = new AgentStageHarness(
        [
            new ScriptedResponder("drafter", "```json\n" + AgentStageHarness.ValidDraft + "\n```"),
            new ScriptedResponder("checker", AgentStageHarness.ValidReport),
            new ScriptedResponder("summariser", "# Summary\n\nAll good."),
        ]);

        var draft = await harness.RunStageAsync("draft");
        var check = await harness.RunStageAsync("check");
        harness.Lock("report");
        var summary = await harness.RunStageAsync("summarise");

        Assert.All([draft, check, summary], r => Assert.Equal(AgentStageEnd.Stored, r.End));
        var store = harness.Store;
        Assert.Equal(["draft", "report", "summary"], store.Artifacts.Select(a => a.Type));
        var stored = store.Artifacts[0];
        Assert.Equal((1, (int?)null, ArtifactStatus.Locked), (stored.Version, stored.ParentVersion, stored.Status));
        Assert.Equal(AgentStageHarness.ValidDraft, stored.Content);
        Assert.Equal(ContentHash.Compute(AgentStageHarness.ValidDraft), stored.ContentHash);
        Assert.Equal(ArtifactStatus.Draft, store.Artifacts[2].Status);
        Assert.All(store.Artifacts, a => Assert.Equal((harness.Run.Id, harness.Run.SubjectId), (a.PipelineRunId, (Guid?)a.ReleaseVersionId)));

        Assert.Equal(3, store.AgentRuns.Count);
        Assert.Equal(["drafter", "checker", "summariser"], store.AgentRuns.Select(r => r.Agent));
        Assert.Equal(["draft", "check", "summarise"], store.AgentRuns.Select(r => r.Stage));
        Assert.Equal(["d1", "c1", "s1"], store.AgentRuns.Select(r => r.PromptVersion));
        Assert.All(store.AgentRuns, r =>
        {
            Assert.Equal(AgentRunStatus.Succeeded, r.Status);
            Assert.Equal(harness.Run.Id, r.PipelineRunId);
            Assert.Equal(LlmProvider.Fake, r.Provider);
            Assert.True(r.InputTokens > 0 && r.OutputTokens > 0);
            Assert.StartsWith("[system]\n", r.Input, StringComparison.Ordinal);
            Assert.NotNull(r.CompletedAt);
        });
        Assert.Equal(store.AgentRuns[0].Id, stored.AgentRunId);
        Assert.Equal([new InputArtifactRef(stored.Id, "draft", 1)], store.AgentRuns[1].InputArtifacts);
        Assert.Contains(AgentStageHarness.ValidDraft, store.AgentRuns[1].Input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR295_a_new_output_is_the_next_version_and_supersedes_the_open_draft()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", AgentStageHarness.ValidReport)]);

        await harness.RunStageAsync("check");
        await harness.RunStageAsync("check");

        var reports = harness.Store.Artifacts;
        Assert.Equal([(1, (int?)null, ArtifactStatus.Superseded), (2, 1, ArtifactStatus.Draft)], reports.Select(a => (a.Version, a.ParentVersion, a.Status)));
    }

    [Fact]
    public async Task FR295_D_0_7_0_27_a_missing_input_is_absent_not_an_error()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Contains("<draft></draft>", Assert.Single(checker.Prompts), StringComparison.Ordinal);
        Assert.Empty(harness.Store.AgentRuns[0].InputArtifacts);
    }

    [Fact]
    public async Task FR295_a_required_placeholder_without_its_input_fails_the_job_with_MISSING_INPUT()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("summariser", "x")]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("summarise"));

        Assert.Equal((false, EngineErrorCodes.MissingInput), (ex.Transient, ex.ErrorCode));
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR295_context_provider_content_reaches_the_prompt_for_its_pipeline_only()
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        var notes = new NotesContextProvider();
        var other = new NotesContextProvider("other-pipeline");
        using var harness = new AgentStageHarness([drafter], contextProviders: [notes, other]);

        await harness.RunStageAsync("draft");

        Assert.Contains($"<notes>{NotesContextProvider.Notes}</notes>", Assert.Single(drafter.Prompts), StringComparison.Ordinal);
        var request = Assert.Single(notes.Requests);
        Assert.Equal((harness.Run.Id, "fixture-review", "draft", "drafter"), (request.RunId, request.PipelineId, request.StageId, request.AgentKey));
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task FR295_FR280_the_stage_tools_are_resolved_by_name_and_called()
    {
        var script = new FakeToolScript("drafter", [new FakeToolCall("lookup", """{"q":"pace"}""")]);
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)], scripts: [script]);

        var result = await harness.RunStageAsync("draft");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        var calls = harness.Store.AgentRuns[0].ToolCalls;
        Assert.Contains("\"tool\":\"lookup\"", calls, StringComparison.Ordinal);
        Assert.Contains("\"isError\":false", calls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR295_FR296_schema_errors_name_the_path()
    {
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("checker", """{"consistent":"yes","findings":[1]}""")],
            yaml => yaml.Replace("then: escalate", "then: fail", StringComparison.Ordinal).Replace("max_attempts: 3", "max_attempts: 1", StringComparison.Ordinal));

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Failed, result.End);
        Assert.Contains("/consistent: ", result.Findings, StringComparison.Ordinal);
        Assert.Contains("/findings/0: ", result.Findings, StringComparison.Ordinal);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR295_output_that_is_not_json_is_a_schema_finding_at_the_root()
    {
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("checker", "not json")],
            yaml => yaml.Replace("max_attempts: 3", "max_attempts: 1", StringComparison.Ordinal));

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Escalated, result.End);
        Assert.StartsWith("/: The output is not valid JSON", result.Findings, StringComparison.Ordinal);
    }
}
