using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Triggers;

namespace Tevrin.Pack.Coach.Tests;

/// <summary>FR-338, FR-339: the coach pack registers <c>impulse-coach</c>, which loads and passes the §4.49 validator,
/// with its schemas, prompts and the Velocity guard name.</summary>
public class CoachDefinitionTests
{
    private static PipelineRegistry Registry() => new([new CoachPack()], [], Array.Empty<JobHandlerRegistration>());

    [Fact]
    public void FR339_impulse_coach_loads_and_passes_the_validator_engine_controlled()
    {
        var registry = Registry();

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(CoachPack.PipelineId, out var loaded));
        Assert.Equal(RunControl.Engine, registry.RunControlOf(CoachPack.PipelineId));
        Assert.Equal(["analyse", "plan", "review", "write"], loaded.Definition!.Stages.Select(s => s.Id));
    }

    [Fact]
    public void FR339_artifacts_are_json_with_schemas_the_profile_project_scope_and_the_log_the_input()
    {
        Assert.True(Registry().TryGet(CoachPack.PipelineId, out var loaded));
        var definition = loaded.Definition!;

        Assert.All(CoachArtifacts.All, key =>
        {
            var artifact = definition.FindArtifact(key)!;
            Assert.Equal(ArtifactFormat.Json, artifact.Format);
            Assert.Equal(CoachArtifacts.SchemaOf(key), artifact.Schema);
        });
        Assert.Equal(ArtifactScope.Project, definition.FindArtifact(CoachArtifacts.AthleteProfile)!.Scope);
        Assert.True(definition.FindArtifact(CoachArtifacts.SessionLog)!.Input);
    }

    [Fact]
    public void FR339_triggers_are_manual_webhook_with_the_session_log_and_schedule()
    {
        var registry = Registry();
        Assert.True(registry.TryGet(CoachPack.PipelineId, out var loaded));
        var definition = loaded.Definition!;

        Assert.Equal([TriggerKind.Manual, TriggerKind.Webhook, TriggerKind.Schedule], definition.DeclaredTriggers.Select(t => t.Kind));
        Assert.Equal((CoachArtifacts.SessionLog, TriggerOnActive.Skip), (definition.TriggerOf(TriggerKind.Webhook)!.Input, definition.TriggerOf(TriggerKind.Webhook)!.OnActive));
        Assert.True(TriggerChecks.Pipeline(registry, CoachPack.PipelineId, TriggerKind.Webhook).IsSuccess);
        Assert.True(TriggerChecks.Pipeline(registry, CoachPack.PipelineId, TriggerKind.Schedule).IsSuccess);
    }

    [Fact]
    public void FR339_stages_agents_guard_retry_policy_and_gate()
    {
        Assert.True(Registry().TryGet(CoachPack.PipelineId, out var loaded));
        var stages = loaded.Definition!.Stages.ToDictionary(s => s.Id);

        Assert.Equal((CoachAgents.PerformanceAnalyst, CoachArtifacts.Performance), (stages["analyse"].Agent!.Key, stages["analyse"].Output));
        Assert.Equal([CoachArtifacts.SessionLog, CoachArtifacts.AthleteProfile], stages["analyse"].Inputs);

        var plan = stages["plan"];
        Assert.Equal((CoachAgents.ProgrammePlanner, CoachArtifacts.PlanDraft), (plan.Agent!.Key, plan.Output));
        Assert.Equal([CoachArtifacts.Performance, CoachArtifacts.AthleteProfile], plan.Inputs);
        Assert.Equal([new StageValidation(CoachGuardNames.VelocityLoadRules)], plan.Validate);
        Assert.Equal(new OnInvalidPolicy(true, 3, InvalidOutcome.Escalate), plan.OnInvalid);

        var gate = stages["review"].Gate!;
        Assert.Equal((CoachGates.PlanApproval, CoachArtifacts.PlanDraft), (gate.Type, gate.Approves));
        Assert.Equal([ApprovalMode.Strict, ApprovalMode.Advisory], gate.Modes);

        Assert.Equal((CoachAgents.CoachVoice, CoachArtifacts.CoachMessage), (stages["write"].Agent!.Key, stages["write"].Output));
        Assert.Equal([CoachArtifacts.PlanDraft], stages["write"].Inputs);
    }

    [Fact]
    public void FR338_the_pack_registers_its_schemas_prompts_and_guard()
    {
        var pack = Assert.Single(Registry().Packs);

        Assert.Equal(CoachArtifacts.All.Select(CoachArtifacts.SchemaOf).Order(StringComparer.Ordinal), pack.Schemas.Keys.Order(StringComparer.Ordinal));
        Assert.All(pack.Schemas.Values, json => JsonDocument.Parse(json).Dispose());
        Assert.Equal(CoachPrompts.All.Order(StringComparer.Ordinal), pack.Prompts.Keys.Order(StringComparer.Ordinal));
        Assert.All(pack.Prompts, p => Assert.True(PromptTemplate.Parse(p.Key["coach.".Length..], p.Value).IsSuccess, p.Key));
        Assert.True(Registry().Registers(PipelineComponentKind.Guard, CoachGuardNames.VelocityLoadRules));
    }

    [Fact]
    public void FR339_each_agent_prompt_serves_its_stage()
    {
        var registry = Registry();
        Assert.True(registry.TryGet(CoachPack.PipelineId, out var loaded));
        var pack = Assert.Single(registry.Packs);

        Assert.All(loaded.Definition!.Stages.Where(s => s.Agent is not null), stage =>
            Assert.Null(AgentPromptPlaceholders.Problem(stage, pack.Prompts[stage.Agent!.Prompt!], [])));
    }
}
