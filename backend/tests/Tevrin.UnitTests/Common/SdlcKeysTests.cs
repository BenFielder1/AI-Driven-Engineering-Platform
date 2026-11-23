using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Common;

/// <summary>
/// FR-269–FR-271, FR-274: the string keys that replaced the <c>AgentKind</c>, <c>ArtifactType</c> and <c>GateType</c>
/// enums are their 0.6.1 wire and stored values (snake_case of the member name), and <c>All</c> keeps the enum's
/// declaration order, so stored rows, API values and every ordering that followed the enum are unchanged.
/// </summary>
public class SdlcKeysTests
{
    [Fact]
    public void FR269_agent_keys_are_the_0_6_1_AgentKind_wire_values_in_enum_order()
    {
        Assert.Equal(
            ["idea_refinement", "spec_generator", "plan_generator", "code_generator", "code_review", "repository_analyst", "stack_analyst", "build_fix"],
            SdlcAgents.All);
        Assert.Equal(
            [SdlcAgents.IdeaRefinement, SdlcAgents.SpecGenerator, SdlcAgents.PlanGenerator, SdlcAgents.CodeGenerator, SdlcAgents.CodeReview, SdlcAgents.RepositoryAnalyst, SdlcAgents.StackAnalyst, SdlcAgents.BuildFix],
            SdlcAgents.All);
    }

    [Fact]
    public void FR270_artifact_keys_are_the_0_6_1_ArtifactType_wire_values_in_enum_order()
    {
        Assert.Equal(
            ["project_request", "technical_specification", "implementation_plan", "optimization_plan", "codebase_summary", "source_items", "stack_profile"],
            SdlcArtifacts.All);
        Assert.Equal(
            [SdlcArtifacts.ProjectRequest, SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan, SdlcArtifacts.CodebaseSummary, SdlcArtifacts.SourceItems, SdlcArtifacts.StackProfile],
            SdlcArtifacts.All);
    }

    [Fact]
    public void FR271_gate_keys_are_the_0_6_1_GateType_wire_values_in_enum_order_with_escalation_from_the_engine()
    {
        Assert.Equal("escalation", EngineGates.Escalation);
        Assert.Equal(["spec_approval", "plan_approval", "pre_release", "escalation"], SdlcGates.All);
        Assert.Equal([SdlcGates.SpecApproval, SdlcGates.PlanApproval, SdlcGates.PreRelease, EngineGates.Escalation], SdlcGates.All);
    }

    [Fact]
    public void FR269_FR270_FR271_keys_are_snake_case_and_their_pascal_names_are_the_old_member_names()
    {
        Assert.All(SdlcAgents.All.Concat(SdlcArtifacts.All).Concat(SdlcGates.All), key => Assert.Matches("^[a-z]+(_[a-z]+)*$", key));
        Assert.Equal(
            ["IdeaRefinement", "SpecGenerator", "PlanGenerator", "CodeGenerator", "CodeReview", "RepositoryAnalyst", "StackAnalyst", "BuildFix"],
            SdlcAgents.All.Select(KeyNames.ToPascalCase));
        Assert.Equal(
            ["ProjectRequest", "TechnicalSpecification", "ImplementationPlan", "OptimizationPlan", "CodebaseSummary", "SourceItems", "StackProfile"],
            SdlcArtifacts.All.Select(KeyNames.ToPascalCase));
        Assert.Equal(["SpecApproval", "PlanApproval", "PreRelease", "Escalation"], SdlcGates.All.Select(KeyNames.ToPascalCase));
    }

    [Theory]
    [InlineData("implementation_plan", true)]
    [InlineData("Implementation_Plan", false)]
    [InlineData("ImplementationPlan", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FR270_wire_values_parse_ordinally_as_the_enum_did(string? value, bool known)
    {
        Assert.Equal(known, SdlcArtifacts.IsKnown(value));
        Assert.Equal(known, SdlcArtifacts.TryParse(value, out _));
    }

    [Fact]
    public void FR269_FR270_FR271_parsing_an_unknown_value_throws_as_the_enum_did()
    {
        Assert.Throws<FormatException>(() => SdlcAgents.Parse("reviewer"));
        Assert.Throws<FormatException>(() => SdlcArtifacts.Parse("spec"));
        Assert.Throws<FormatException>(() => SdlcGates.Parse("approval"));
        Assert.Equal(SdlcGates.PreRelease, SdlcGates.Parse("pre_release"));
    }

    [Fact]
    public void FR270_order_is_the_enum_order_not_alphabetical()
    {
        // In-memory sorts that followed the enum's numeric values keep that order (e.g. the artifact list).
        Assert.Equal(SdlcArtifacts.All, SdlcArtifacts.All.Reverse().OrderBy(SdlcArtifacts.Order));
        Assert.Equal(-1, SdlcArtifacts.Order("unknown"));
        Assert.Equal(3, SdlcGates.Order(EngineGates.Escalation));
        Assert.Equal(7, SdlcAgents.Order(SdlcAgents.BuildFix));
    }
}
