using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-310: host run ids and redelivery.</summary>
public class AgentStageRunIdTests
{
    private const string Invalid = """{"consistent":false,"findings":["weekly volume +25% > 10%"]}""";

    [Fact]
    public async Task FR310_the_agent_run_takes_the_callers_id()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", AgentStageHarness.ValidReport)]);
        var id = Guid.CreateVersion7();

        var result = await harness.RunStageAsync("check", id);

        Assert.Equal(id, Assert.Single(harness.Store.AgentRuns).Id);
        Assert.Equal(id, result.Last.Artifact!.AgentRunId);
    }

    [Fact]
    public async Task FR310_a_redelivered_succeeded_run_returns_its_stored_output_with_no_call_no_run_and_no_artifact()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        var id = Guid.CreateVersion7();
        var first = await harness.RunStageAsync("check", id);
        var hooks = harness.Hooks.Calls.Count;

        var again = await harness.RunStageAsync("check", id);

        Assert.Single(checker.Prompts);
        Assert.Single(harness.Store.AgentRuns);
        var artifact = Assert.Single(harness.Store.Artifacts);
        Assert.Same(artifact, again.Last.Artifact);
        Assert.Equal((1, ArtifactStatus.Draft), (artifact.Version, artifact.Status));
        Assert.True(again.Last.Redelivered);
        Assert.False(first.Last.Redelivered);
        Assert.Equal(hooks, harness.Hooks.Calls.Count);
    }

    [Fact]
    public async Task FR310_a_succeeded_run_whose_artifact_was_never_stored_stores_it_once_without_a_call()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        var id = Guid.CreateVersion7();
        await harness.RunStageAsync("check", id);
        harness.Store.Artifacts.Clear(); // the job stopped between the run's commit and the store

        var again = await harness.RunStageAsync("check", id);

        Assert.Single(checker.Prompts);
        Assert.Equal(id, Assert.Single(harness.Store.Artifacts).AgentRunId);
        Assert.Equal(AgentStageEnd.Stored, again.End);
    }

    [Fact]
    public async Task FR310_a_redelivered_stage_with_a_retry_finds_every_attempts_run_again()
    {
        var checker = new ScriptedResponder("checker", Invalid, AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        var id = Guid.CreateVersion7();
        await harness.RunStageAsync("check", id);

        var again = await harness.RunStageAsync("check", id);

        Assert.Equal(2, checker.Prompts.Count);
        Assert.Equal([id, AgentStageRunIds.ForAttempt(id, 2)!.Value], harness.Store.AgentRuns.Select(r => r.Id));
        Assert.Equal((AgentStageEnd.Stored, 2), (again.End, again.Attempts.Count));
        Assert.All(again.Attempts, a => Assert.True(a.Redelivered));
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public void FR310_attempt_ids_derive_from_the_callers_id_and_are_new_without_one()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id, AgentStageRunIds.ForAttempt(id, 1));
        Assert.Equal(AgentRunRules.RunId(id, "attempt-2"), AgentStageRunIds.ForAttempt(id, 2));
        Assert.Null(AgentStageRunIds.ForAttempt(null, 2));
    }

    [Theory]
    [InlineData("CREDENTIAL_INVALID: The key was rejected.", "CREDENTIAL_INVALID")]
    [InlineData("something odd", EngineErrorCodes.ProviderError)]
    public async Task FR310_a_failed_stored_run_fails_the_job_with_its_stored_code_and_no_call(string error, string code)
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        var id = Guid.CreateVersion7();
        harness.Store.AgentRuns.Add(new AgentRun { Id = id, ProjectId = harness.Run.ProjectId, PipelineRunId = harness.Run.Id, Stage = "check", Status = AgentRunStatus.Failed, Error = error });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("check", id));

        Assert.Equal((false, code), (ex.Transient, ex.ErrorCode));
        Assert.Empty(checker.Prompts);
    }

    [Fact]
    public async Task FR310_a_run_left_running_by_a_lost_lease_is_called_again_on_the_same_row()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        var id = Guid.CreateVersion7();
        var left = new AgentRun
        {
            Id = id,
            ProjectId = harness.Run.ProjectId,
            PipelineRunId = harness.Run.Id,
            Agent = "checker",
            Stage = "check",
            Provider = LlmProvider.Fake,
            Model = "fake-model",
            Status = AgentRunStatus.Running,
            Attempts = 1,
            InputTokens = 7,
            Error = "PROVIDER_ERROR: HTTP 503",
        };
        harness.Store.AgentRuns.Add(left);

        await harness.RunStageAsync("check", id);

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Same(left, run);
        Assert.Equal((AgentRunStatus.Succeeded, 2, (string?)null), (run.Status, run.Attempts, run.Error));
        Assert.True(run.InputTokens > 7);
        Assert.DoesNotContain("budget:checker", harness.Hooks.Calls);
    }
}
