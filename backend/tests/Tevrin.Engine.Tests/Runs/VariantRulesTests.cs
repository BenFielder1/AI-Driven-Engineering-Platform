using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-349, §4.67: the variant labels, choice validation, selector answers, gate payload and promotion choice,
/// on <c>fixture-variants</c>.</summary>
public class VariantRulesTests
{
    private static PipelineDefinition Variants
    {
        get
        {
            var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Variants)], [], Array.Empty<JobHandlerRegistration>());
            Assert.True(registry.TryGet(FlowFixturePack.Variants, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    private static StageDefinition Headline => Variants.FindStage("headline")!;

    private static StageDefinition Tagline => Variants.FindStage("tagline")!;

    private static readonly Guid V1 = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid V2 = Guid.Parse("0197a000-0000-7000-8000-000000000002");
    private static readonly Guid V3 = Guid.Parse("0197a000-0000-7000-8000-000000000003");

    private static VariantRef[] Refs => [new("v1", V1), new("v2", V2), new("v3", V3)];

    [Fact]
    public void FR349_a_variants_stage_runs_v1_to_vn_of_its_own_agent_as_fan_out_branches()
    {
        Assert.Equal(["v1", "v2", "v3"], VariantRules.Labels(Headline));
        Assert.Equal(["v1", "v2"], VariantRules.Labels(Tagline));
        var branches = FanOutRules.Branches(Headline);
        Assert.Equal(["v1", "v2", "v3"], branches.Select(b => b.Label));
        Assert.All(branches, b => Assert.Null(b.AgentKey));
    }

    [Fact]
    public void FR349_a_variant_branch_execution_is_in_the_stages_fan_out_and_joins_when_every_variant_completed()
    {
        StageExecution Branch(string label, string? outcome) => new()
        {
            StageId = "headline",
            Branch = label,
            Variant = label,
            ExitedAt = outcome is null ? null : DateTimeOffset.UnixEpoch,
            Outcome = outcome,
        };

        Assert.True(FanOutRules.InFanOut(Headline, Branch("v2", null)));
        Assert.False(FanOutRules.InFanOut(Headline, Branch("v4", null)));
        Assert.False(FanOutRules.Joined(Headline, [Branch("v1", BranchOutcomes.Completed), Branch("v2", BranchOutcomes.Completed), Branch("v3", null)]));
        Assert.True(FanOutRules.Joined(Headline, VariantRules.Labels(Headline).Select(l => Branch(l, BranchOutcomes.Completed))));
    }

    [Fact]
    public void FR349_a_plain_stage_has_no_variants()
    {
        var plain = Variants.Stages.Single(s => s.Id == "headline") with { Variants = null };
        Assert.Empty(VariantRules.Labels(plain));
        Assert.False(VariantRules.IsValidChoice(plain, "v1"));
    }

    [Theory]
    [InlineData("v1", true)]
    [InlineData("v3", true)]
    [InlineData("v4", false)]
    [InlineData("v0", false)]
    [InlineData("V2", false)]
    [InlineData(" v2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FR349_VARIANT_CHOICE_INVALID_a_choice_must_be_one_of_v1_to_vn(string? choice, bool valid) =>
        Assert.Equal(valid, VariantRules.IsValidChoice(Headline, choice));

    [Fact]
    public void FR349_the_chosen_variant_is_promoted_and_a_choice_outside_the_stage_is_none()
    {
        Assert.Equal(new VariantRef("v2", V2), VariantRules.Chosen(Headline, Refs, "v2"));
        Assert.Null(VariantRules.Chosen(Headline, Refs, "v4"));

        // Tagline has two variants: v3 isn't one of them, even if such a row were offered.
        Assert.Null(VariantRules.Chosen(Tagline, Refs, "v3"));

        // A valid label whose variant wasn't stored can't be promoted.
        Assert.Null(VariantRules.Chosen(Headline, [new("v1", V1)], "v2"));
    }

    [Theory]
    [InlineData("{\"choice\": \"v2\", \"reason\": \"Shorter.\"}", "v2", "Shorter.")]
    [InlineData("```json\n{ \"choice\": \"v1\" }\n```", "v1", null)]
    [InlineData("{\"choice\": \" v2 \", \"reason\": 3}", "v2", null)]
    public void FR349_a_selector_answers_choice_and_reason(string output, string choice, string? reason) =>
        Assert.Equal(new VariantSelection(choice, reason), VariantRules.ReadSelection(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v2")]
    [InlineData("{\"pick\": \"v2\"}")]
    [InlineData("{\"choice\": 2}")]
    [InlineData("{not json}")]
    [InlineData("[\"v2\"]")]
    public void FR349_an_unreadable_selector_answer_is_none(string? output) =>
        Assert.Null(VariantRules.ReadSelection(output));

    [Fact]
    public void FR349_section_5_the_variant_select_payload_holds_labels_and_artifact_ids_never_the_content()
    {
        var payload = VariantRules.GatePayload("headline", Refs);

        Assert.Equal(
            $"{{\"stage\":\"headline\",\"variants\":[{{\"label\":\"v1\",\"artifactId\":\"{V1}\"}},{{\"label\":\"v2\",\"artifactId\":\"{V2}\"}},{{\"label\":\"v3\",\"artifactId\":\"{V3}\"}}]}}",
            payload);
        Assert.Equal(["v1", "v2", "v3"], VariantRules.PayloadLabels(payload));
        Assert.Null(VariantRules.PayloadChoice(payload));
    }

    [Fact]
    public void FR349_an_approved_gate_records_its_choice_on_the_payload()
    {
        var payload = VariantRules.WithChoice(VariantRules.GatePayload("headline", Refs), "v2");

        Assert.Equal("v2", VariantRules.PayloadChoice(payload));
        Assert.Equal(["v1", "v2", "v3"], VariantRules.PayloadLabels(payload));
        Assert.Empty(VariantRules.PayloadLabels(null));
        Assert.Empty(VariantRules.PayloadLabels("not json"));
        Assert.Null(VariantRules.PayloadChoice("[]"));
    }

    [Fact]
    public void FR349_a_selector_runs_under_its_own_run_key() =>
        Assert.Equal("select-tagline-judge", VariantRules.SelectRunKey("tagline-judge"));

    [Fact]
    public void FR349_the_variant_label_is_the_VARIANT_placeholder() =>
        Assert.Equal("VARIANT", VariantRules.LabelPlaceholder);

    [Fact]
    public void FR349_VARIANT_CHOICE_INVALID_is_an_engine_error_code() =>
        Assert.Equal("VARIANT_CHOICE_INVALID", EngineErrorCodes.VariantChoiceInvalid);

    [Fact]
    public void FR349_an_approve_records_the_decisions_choice() =>
        Assert.Equal("v2", new EngineGateDecision(null, null) { Choice = "v2" }.Choice);
}
