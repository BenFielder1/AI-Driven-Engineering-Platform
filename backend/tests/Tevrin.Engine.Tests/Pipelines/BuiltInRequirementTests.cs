using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-282, FR-283, §4.52: the built-in requirement and entry-gate messages keep 0.6.1's wording (FR-290).</summary>
public class BuiltInRequirementTests
{
    private static readonly PipelineDefinition Definition = Fixtures.Load("fixture-review.pipeline.yaml") with
    {
        Artifacts =
        [
            new ArtifactDefinition("spec", "Technical specification", ArtifactFormat.Markdown, null, ArtifactApproval.Required, CascadeMode.Downstream),
            new ArtifactDefinition("plan", "Implementation plan", ArtifactFormat.Markdown, null, ArtifactApproval.Required, CascadeMode.Downstream),
            new ArtifactDefinition("lower", "already lower case", ArtifactFormat.Markdown, null, ArtifactApproval.Required, CascadeMode.Downstream),
        ],
    };

    [Theory]
    [InlineData("Technical specification", "The technical specification is not approved.")]
    [InlineData("Project request", "The project request is not approved.")]
    [InlineData("Implementation plan", "The implementation plan is not approved.")]
    [InlineData("already lower case", "The already lower case is not approved.")]
    public void FR282_artifact_locked_message_names_the_title_with_its_first_letter_lower_cased(string title, string expected) =>
        Assert.Equal(expected, BuiltInRequirements.ArtifactNotLocked(title));

    [Fact]
    public void FR282_no_pending_gates_message_lists_the_gate_keys_comma_separated() =>
        Assert.Equal("Other gates are pending: escalation, plan_approval.", BuiltInRequirements.OtherGatesPending(["escalation", "plan_approval"]));

    [Fact]
    public void FR283_entry_gate_message_names_the_gate_key() =>
        Assert.Equal("The pre_release gate is not approved.", BuiltInRequirements.GateNotApproved("pre_release"));

    [Fact]
    public void FR282_artifact_locked_is_met_by_a_locked_key_and_otherwise_names_the_artifact_title()
    {
        var requirement = new ArtifactLockedRequirement("plan");

        Assert.Null(BuiltInRequirements.Unmet(Definition, requirement, ["plan"], []));
        Assert.Equal("The implementation plan is not approved.", BuiltInRequirements.Unmet(Definition, requirement, ["spec"], []));
    }

    [Fact]
    public void FR282_no_pending_gates_ignores_the_excepted_types_and_lists_the_others_once_in_order()
    {
        var requirement = new NoPendingGatesRequirement(["pre_release"]);

        Assert.Null(BuiltInRequirements.Unmet(Definition, requirement, [], []));
        Assert.Null(BuiltInRequirements.Unmet(Definition, requirement, [], ["pre_release", "pre_release"]));
        Assert.Equal(
            "Other gates are pending: escalation, spec_approval.",
            BuiltInRequirements.Unmet(Definition, requirement, [], ["escalation", "pre_release", "spec_approval", "escalation"]));
    }

    [Fact]
    public void FR282_a_guard_requirement_is_not_built_in() =>
        Assert.Throws<ArgumentException>(() => BuiltInRequirements.Unmet(Definition, new GuardRequirement("fixture.x"), [], []));

    [Fact]
    public void FR283_the_entry_gate_is_required_only_in_its_modes()
    {
        var stage = new StageDefinition
        {
            Id = "planning",
            Gate = new StageGate("spec_approval", [ApprovalMode.Strict, ApprovalMode.Advisory], "spec", true),
        };

        Assert.Equal("spec_approval", BuiltInRequirements.RequiredGate(stage, ApprovalMode.Strict));
        Assert.Equal("spec_approval", BuiltInRequirements.RequiredGate(stage, ApprovalMode.Advisory));
        Assert.Null(BuiltInRequirements.RequiredGate(stage, ApprovalMode.Autonomous));
        Assert.Null(BuiltInRequirements.RequiredGate(stage with { Gate = null }, ApprovalMode.Strict));
    }
}
