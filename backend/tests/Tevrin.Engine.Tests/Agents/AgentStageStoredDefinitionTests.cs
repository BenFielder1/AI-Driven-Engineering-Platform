using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-277, D-0.7.0-37 (M62c): an agent stage runs on the run's pinned definition as a lifecycle move resolves it
/// (<see cref="PipelineDefinitionResolver"/>): registered, or stored and checked against its hash with every guard, handler
/// and tool it names registered; otherwise the job fails permanently with <c>DEFINITION_UNAVAILABLE</c>.
/// </summary>
public class AgentStageStoredDefinitionTests
{
    private const string StoredHash = "sha256:stored-only-fixture";

    /// <summary>Pins the harness's run to a stored-only copy of the fixture definition, edited by <paramref name="edit"/>.</summary>
    private static void PinToStored(AgentStageHarness harness, Func<Domain.Pipelines.PipelineDefinition, Domain.Pipelines.PipelineDefinition> edit)
    {
        Assert.True(harness.Registry.TryGet("fixture-review", out var loaded));
        harness.Services.GetRequiredService<PipelineDefinitionCache>().Add(StoredHash, new StoredPipelineDefinition(loaded.PackId, edit(loaded.Definition!)));
        var run = harness.Run;
        harness.Store.Runs.Remove(run);
        harness.Store.Runs.Add(new Domain.Entities.PipelineRun
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PipelineId = run.PipelineId,
            DefinitionHash = StoredHash,
            SubjectId = run.SubjectId,
            CurrentStage = run.CurrentStage,
            PackStatus = run.PackStatus,
            ApprovalMode = run.ApprovalMode,
        });
    }

    [Fact]
    public async Task FR277_a_stored_only_definition_whose_names_are_all_registered_runs_on_the_engine()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)]);
        PinToStored(harness, d => d with { Version = 99 });

        var result = await harness.RunStageAsync("draft");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR277_a_stored_only_definition_naming_an_unregistered_tool_fails_with_DEFINITION_UNAVAILABLE()
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        using var harness = new AgentStageHarness([drafter]);
        PinToStored(harness, d => d with
        {
            Stages = [.. d.Stages.Select(s => s.Id == "draft" ? s with { Agent = s.Agent! with { Tools = ["fixture.missing-tool"] } } : s)],
        });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((EngineErrorCodes.DefinitionUnavailable, false), (ex.ErrorCode, ex.Transient));
        Assert.Empty(drafter.Prompts);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR277_a_stored_only_definition_naming_an_unregistered_guard_fails_with_DEFINITION_UNAVAILABLE()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("drafter", AgentStageHarness.ValidDraft)]);
        PinToStored(harness, d => d with
        {
            Stages = [.. d.Stages.Select(s => s.Id == "draft" ? s with { Validate = [new Domain.Pipelines.StageValidation("fixture.missing-guard")] } : s)],
        });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal(EngineErrorCodes.DefinitionUnavailable, ex.ErrorCode);
    }

    [Fact]
    public async Task D_0_7_0_44_FR277_a_stored_only_definition_whose_binding_names_an_unregistered_guard_fails_with_DEFINITION_UNAVAILABLE()
    {
        var drafter = new ScriptedResponder("drafter", AgentStageHarness.ValidDraft);
        using var harness = new AgentStageHarness([drafter]);
        PinToStored(harness, d => d with
        {
            Stages = [.. d.Stages.Select(s => s.Id == "check"
                ? s with
                {
                    Agents = [new Domain.Pipelines.AgentBindingDefinition("auditor", "stage.run", null, s.Inputs, null, []) { Validate = [new Domain.Pipelines.StageValidation("fixture.missing-guard")] }],
                }
                : s)],
        });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunStageAsync("draft"));

        Assert.Equal((EngineErrorCodes.DefinitionUnavailable, false), (ex.ErrorCode, ex.Transient));
        Assert.Empty(drafter.Prompts);
        Assert.Empty(harness.Store.AgentRuns);
    }
}
