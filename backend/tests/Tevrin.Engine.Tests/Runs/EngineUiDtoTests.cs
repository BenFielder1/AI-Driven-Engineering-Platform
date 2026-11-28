using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-362–FR-364 (M72): the pure DTO reads the engine UI renders: gate payloads, constructs and inputs.</summary>
public class EngineUiDtoTests
{
    private static readonly Guid V1 = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid V2 = Guid.Parse("0190a000-0000-7000-8000-000000000002");
    private static readonly Guid Run = Guid.Parse("0190a000-0000-7000-8000-0000000000aa");

    private static (string, int)? Versions(Guid id) => id == V1 ? ("headline", 2) : id == V2 ? ("headline", 3) : null;

    [Fact]
    public void FR364_a_variant_select_payload_lists_labels_with_keys_and_versions_and_the_choice()
    {
        var stored = VariantRules.WithChoice(VariantRules.GatePayload("headline", [new VariantRef("v1", V1), new VariantRef("v2", V2)]), "v2");

        var payload = PipelineGatePayloads.Read(EngineGates.VariantSelect, stored, Versions);

        Assert.NotNull(payload);
        Assert.Equal((PipelineGatePayloadDto.VariantSelectKind, "headline", "v2"), (payload.Kind, payload.Stage, payload.Choice));
        Assert.Equal([new PipelineGateVariantDto("v1", V1, "headline", 2), new PipelineGateVariantDto("v2", V2, "headline", 3)], payload.Variants);
        Assert.Equal([V1, V2], PipelineGatePayloads.VariantArtifactIds(EngineGates.VariantSelect, stored));
        Assert.Empty(PipelineGatePayloads.VariantArtifactIds(EngineGates.ToolApproval, stored));
    }

    [Fact]
    public void FR364_a_tool_call_payload_shows_tool_tier_redacted_arguments_and_reason_but_not_its_claim()
    {
        const string Stored = """{"tool":"oncall.restart-pod","tier":"destructive","arguments":"{\"pod\":\"api-0\"}","reason":"Out of memory","agentRunId":"0190a000-0000-7000-8000-0000000000aa","ranAt":"2030-12-02T09:00:00+00:00"}""";

        foreach (var type in new[] { EngineGates.ToolApproval, EngineGates.ExternalAction })
        {
            var payload = PipelineGatePayloads.Read(type, Stored, Versions);
            Assert.NotNull(payload);
            Assert.Equal(
                (PipelineGatePayloadDto.ToolCallKind, "oncall.restart-pod", "destructive", """{"pod":"api-0"}""", "Out of memory", (Guid?)Run),
                (payload.Kind, payload.Tool, payload.Tier, payload.Arguments, payload.Reason, payload.AgentRunId));
        }
    }

    [Fact]
    public void FR364_an_external_handler_payload_is_the_exact_stored_text()
    {
        var stored = ExternalActionRules.WithSentAt(
            ExternalActionRules.GatePayload("cv.submit", "submit", Guid.CreateVersion7(), Guid.CreateVersion7(), "Dear hiring manager,\n…"),
            DateTimeOffset.UnixEpoch);

        var payload = PipelineGatePayloads.Read(EngineGates.ExternalAction, stored, Versions);

        Assert.NotNull(payload);
        Assert.Equal(
            (PipelineGatePayloadDto.ExternalHandlerKind, "cv.submit", "submit", "Dear hiring manager,\n…", (string?)null),
            (payload.Kind, payload.Handler, payload.Stage, payload.Payload, payload.Tool));
    }

    [Theory]
    [InlineData("plan_approval", """{"tool":"x"}""")]
    [InlineData("tool_approval", null)]
    [InlineData("tool_approval", "not json")]
    [InlineData("tool_approval", "[1]")]
    [InlineData("external_action", """{"handler":"h"}""")]
    public void FR364_gates_without_a_readable_payload_of_their_kind_have_none(string type, string? stored) =>
        Assert.Null(PipelineGatePayloads.Read(type, stored, Versions));

    [Fact]
    public void FR363_the_definition_describes_parallel_branches_and_variants()
    {
        var parallel = PipelineDtoMapper.ToDefinitionDto(Fixtures.Load("fixture-parallel.pipeline.yaml"), "h", "fixture");
        var group = parallel.Stages.Single(s => s.Id == "analysts");
        Assert.Equal(["0", "1"], group.Branches!.Select(b => b.Id));
        Assert.Equal(["bull", "bull_review"], group.Branches![0].Stages.Select(s => s.Id));

        var variants = PipelineDtoMapper.ToDefinitionDto(Fixtures.Load("fixture-variants.pipeline.yaml"), "h", "fixture");
        Assert.Equal(["v1", "v2", "v3"], variants.Stages.Single(s => s.Id == "headline").Variants!.Labels);
        Assert.Null(variants.Stages.Single(s => s.Id == "headline").Variants!.SelectAgent);
        Assert.Equal("tagline-judge", variants.Stages.Single(s => s.Id == "tagline").Variants!.SelectAgent);
    }

    [Fact]
    public void FR362_input_artifacts_carry_their_pack_schema_and_run_control_and_modes_are_shown()
    {
        var definition = Fixtures.Load("fixture-scheduled.pipeline.yaml");

        var dto = PipelineDtoMapper.ToDefinitionDto(
            definition, "h", "fixture", RunControl.Engine, name => name == "brief.schema.json" ? """{"type":"object","required":["topic"]}""" : null);

        var brief = dto.Artifacts.Single(a => a.Key == "brief");
        Assert.True(brief.Input);
        Assert.Equal("topic", brief.Schema!.Value.GetProperty("required")[0].GetString());
        Assert.Equal(RunControl.Engine, dto.RunControl);
        Assert.Equal(ApprovalMode.Advisory, dto.DefaultApprovalMode);
        Assert.Equal([new PipelineTriggerDefinitionDto(TriggerKind.Schedule, null)], dto.Triggers);

        // No schema lookup (or a schema that doesn't parse) leaves it out.
        Assert.Null(PipelineDtoMapper.ToDefinitionDto(definition, "h", "fixture").Artifacts.Single(a => a.Key == "brief").Schema);
        Assert.Null(PipelineDtoMapper.ToDefinitionDto(definition, "h", "fixture", RunControl.Engine, _ => "{").Artifacts.Single(a => a.Key == "brief").Schema);
    }

    [Fact]
    public void FR363_a_stage_entry_carries_its_branch_variant_item_parent_followup_and_veto_findings()
    {
        var parent = Guid.CreateVersion7();
        var item = TimelineItems.Stage(new Domain.Entities.StageExecution
        {
            StageId = "score",
            Branch = "1",
            Variant = "v2",
            ItemIndex = 7,
            ParentExecutionId = parent,
            FollowUp = "followup:1d",
            VetoFindings = """["flow.no-claims: an unsupported claim"]""",
        });

        Assert.Equal(("1", "v2", (int?)7, (Guid?)parent, "followup:1d"), (item.Branch, item.Variant, item.ItemIndex, item.ParentExecutionId, item.FollowUp));
        Assert.Equal(["flow.no-claims: an unsupported claim"], item.VetoFindings);
        Assert.Null(TimelineItems.VetoFindingsOf(null));
        Assert.Null(TimelineItems.VetoFindingsOf("not json"));
        Assert.Null(TimelineItems.VetoFindingsOf("{}"));
    }

    [Fact]
    public void FR363_an_agent_run_entry_carries_its_item_and_conversation_turn()
    {
        var item = TimelineItems.AgentRun(new Domain.Entities.AgentRun { Agent = "support-sim", ItemIndex = 3, TurnIndex = 2, TurnRole = "agent" }, replay: false);

        Assert.Equal(((int?)3, (int?)2, "agent"), (item.ItemIndex, item.TurnIndex, item.TurnRole));
    }
}
