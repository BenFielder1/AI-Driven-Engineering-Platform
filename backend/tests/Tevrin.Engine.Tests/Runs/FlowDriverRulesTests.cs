using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-344, FR-347, FR-348, §4.62 step 5, §4.66: the run driver's route, veto and revision-cap decisions, on
/// <c>fixture-editorial</c> and <c>fixture-veto</c>.</summary>
public class FlowDriverRulesTests
{
    private static PipelineDefinition Load(string pipelineId)
    {
        var registry = new PipelineRegistry([new FlowFixturePack(pipelineId)], [], Array.Empty<JobHandlerRegistration>());
        Assert.True(registry.TryGet(pipelineId, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
        return loaded.Definition!;
    }

    private static PipelineDefinition Editorial => Load(FlowFixturePack.Editorial);

    private static PipelineDefinition Veto => Load(FlowFixturePack.Veto);

    private static RunState At(PipelineDefinition definition, string stage, int checkpoint = 3) =>
        new(stage, definition.FindStage(stage)!.Status, checkpoint, new Dictionary<string, int>(StringComparer.Ordinal) { [stage] = 1 });

    [Fact]
    public void FR347_continue_proceeds_and_a_declared_route_goes_back()
    {
        var edit = Editorial.FindStage("edit")!;

        Assert.IsType<RouteChoice.Proceed>(RunDriverRules.Route(edit, "continue"));
        Assert.Equal(new RouteChoice.Back("research"), RunDriverRules.Route(edit, "research"));
        Assert.Equal(new RouteChoice.Back("write"), RunDriverRules.Route(edit, "write"));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("edit")]
    [InlineData("Research")]
    [InlineData(null)]
    public void FR347_a_route_that_is_not_continue_or_declared_is_invalid(string? route) =>
        Assert.IsType<RouteChoice.Invalid>(RunDriverRules.Route(Editorial.FindStage("edit")!, route));

    [Fact]
    public void FR347_a_stage_without_routes_always_proceeds() =>
        Assert.IsType<RouteChoice.Proceed>(RunDriverRules.Route(Editorial.FindStage("write")!, "research"));

    [Theory]
    [InlineData("{ \"route\": \"write\", \"text\": \"…\" }", "write")]
    [InlineData("```json\n{ \"route\": \"continue\" }\n```", "continue")]
    [InlineData("# Edited\n\nroute: research\n\nBody.", "research")]
    [InlineData("# Edited\n\nRoute: write", null)]
    [InlineData("# Edited\n\nRoute: research is where this should go.", null)]
    [InlineData("# Edited\n\n  route: write", null)]
    [InlineData("# Edited\n\nroute: write it again", null)]
    [InlineData("route: research\r\n\nBody.\n\nroute: write\r\n", "write")]
    [InlineData("# Edited\n\nNo route here.", null)]
    [InlineData("{ \"route\": 3 }", null)]
    [InlineData("", null)]
    public void FR347_the_route_is_read_from_json_or_a_route_line(string content, string? expected) =>
        Assert.Equal(expected, RunDriverRules.ReadRoute(content));

    [Theory]
    [InlineData("research", new[] { "draft", "edited" })]
    [InlineData("write", new[] { "edited" })]
    public void FR347_going_back_invalidates_what_depends_on_the_target_outputs(string target, string[] expected) =>
        Assert.Equal(expected, RunDriverRules.InvalidatedOnBack(Editorial, target));

    [Fact]
    public void FR344_a_veto_back_to_the_proposal_invalidates_the_position_and_order() =>
        Assert.Equal(["position", "order"], RunDriverRules.InvalidatedOnBack(Veto, "propose"));

    [Fact]
    public void FR347_FR348_the_editorial_route_back_reenters_research_as_a_counted_loop()
    {
        var definition = Editorial;
        var state = At(definition, "edit");

        var move = Assert.IsType<BackwardMove.Reenter>(RunDriverRules.Back(definition, state, "research", revisions: 0));

        Assert.Equal("research", move.State.Stage);
        Assert.Equal("researching", move.State.PackStatus);
        Assert.Equal(3, move.State.Checkpoint);
        Assert.Equal(1, move.State.IterationsOf("research"));
        Assert.Equal(1, move.Revisions);
    }

    [Fact]
    public void FR348_without_limits_every_backward_transition_is_allowed() =>
        Assert.Equal(new RevisionCheck.Allowed(51), RunDriverRules.CountRevision(Veto, 50));

    [Fact]
    public void FR348_the_section_4_66_example_refuses_the_third_route_and_escalates()
    {
        var definition = Editorial with { Limits = new RunLimits(2, InvalidOutcome.Escalate) };
        var edit = At(definition, "edit");

        var first = Assert.IsType<BackwardMove.Reenter>(RunDriverRules.Back(definition, edit, "research", 0));
        Assert.Equal(1, first.Revisions);
        var second = Assert.IsType<BackwardMove.Reenter>(RunDriverRules.Back(definition, edit, "write", first.Revisions));
        Assert.Equal(2, second.Revisions);
        var third = Assert.IsType<BackwardMove.Refused>(RunDriverRules.Back(definition, edit, "write", second.Revisions));
        Assert.Equal(InvalidOutcome.Escalate, third.Then);
    }

    [Fact]
    public void FR348_fixture_editorial_allows_three_revisions_then_escalates()
    {
        var definition = Editorial;

        Assert.Equal(new RevisionCheck.Allowed(3), RunDriverRules.CountRevision(definition, 2));
        Assert.Equal(InvalidOutcome.Escalate, Assert.IsType<RevisionCheck.Refused>(RunDriverRules.CountRevision(definition, 3)).Then);
    }

    [Fact]
    public void FR348_then_fail_refuses_with_fail()
    {
        var definition = Editorial with { Limits = new RunLimits(1, InvalidOutcome.Fail) };

        Assert.Equal(InvalidOutcome.Fail, Assert.IsType<BackwardMove.Refused>(RunDriverRules.Back(definition, At(definition, "edit"), "write", 1)).Then);
    }

    [Fact]
    public void FR348_on_reject_reentry_is_counted_against_the_cap()
    {
        var definition = Editorial with { Limits = new RunLimits(1, InvalidOutcome.Fail) };
        var reentered = At(definition, "write");

        Assert.Equal(new BackwardMove.Reenter(reentered, 1), RunDriverRules.Revise(definition, reentered, 0));
        Assert.IsType<BackwardMove.Refused>(RunDriverRules.Revise(definition, reentered, 1));
    }

    [Fact]
    public void FR344_a_veto_to_routes_back_to_the_proposal()
    {
        var definition = Veto;

        var move = Assert.IsType<VetoNext.Move>(RunDriverRules.OnVetoed(definition, At(definition, "size"), 0));
        var reenter = Assert.IsType<BackwardMove.Reenter>(move.Back);
        Assert.Equal("propose", reenter.State.Stage);
        Assert.Equal(1, reenter.Revisions);
    }

    [Fact]
    public void FR344_FR348_a_veto_to_is_refused_by_the_revision_cap()
    {
        var definition = Veto with { Limits = new RunLimits(1, InvalidOutcome.Fail) };

        var move = Assert.IsType<VetoNext.Move>(RunDriverRules.OnVetoed(definition, At(definition, "size"), 1));
        Assert.Equal(InvalidOutcome.Fail, Assert.IsType<BackwardMove.Refused>(move.Back).Then);
    }

    [Fact]
    public void FR344_end_vetoed_ends_the_run_completed()
    {
        var veto = Veto;
        var definition = veto with
        {
            Stages = [.. veto.Stages.Select(s => s.Id == "size" ? s with { OnVeto = OnVetoDefinition.EndVetoed } : s)],
        };

        var end = Assert.IsType<VetoNext.EndVetoed>(RunDriverRules.OnVetoed(definition, At(definition, "size"), 0));
        Assert.Equal(RunDriverRules.CompletedStatus(definition), end.PackStatus);
        Assert.Equal("vetoed", RunDriverRules.VetoedOutcome);
    }

    [Theory]
    [InlineData("{ \"findings\": [\"Position exceeds 2% of equity.\"] }", 1)]
    [InlineData("{ \"findings\": [] }", 0)]
    [InlineData("{ \"approve\": true }", 0)]
    [InlineData("```json\n{ \"findings\": [\"a\", \"b\"] }\n```", 2)]
    public void FR344_a_veto_agent_answers_findings_or_approval(string content, int findings) =>
        Assert.Equal(findings, RunDriverRules.ReadVetoFindings(content)!.Count);

    [Theory]
    [InlineData("Looks fine to me.")]
    [InlineData("{ \"approve\": false }")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void FR344_an_unreadable_veto_answer_is_null(string content) =>
        Assert.Null(RunDriverRules.ReadVetoFindings(content));

    [Fact]
    public void FR344_a_missing_veto_guard_makes_the_definition_unrunnable()
    {
        var bare = new PipelineRegistry([new FixtureLinearPack()], [], Array.Empty<JobHandlerRegistration>());

        Assert.Contains($"guard:{FlowFixturePack.PositionLimitGuard}", PipelineDefinitionResolver.MissingNames(bare, Veto));
        var full = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Veto)], [], Array.Empty<JobHandlerRegistration>());
        Assert.Empty(PipelineDefinitionResolver.MissingNames(full, Veto));
    }

    [Fact]
    public void FR346_a_missing_disagreement_guard_makes_the_definition_unrunnable()
    {
        var bare = new PipelineRegistry([new FixtureLinearPack()], [], Array.Empty<JobHandlerRegistration>());

        Assert.Contains($"guard:{FlowFixturePack.ForecastSpreadGuard}", PipelineDefinitionResolver.MissingNames(bare, Load(FlowFixturePack.Forecast)));
    }

    [Fact]
    public void FR343_the_cascade_finds_branch_producers_as_their_group()
    {
        var parallel = Load(FlowFixturePack.Parallel);

        Assert.Equal("analysts", DefinitionCascade.ProducingStage("bull_risks", parallel));
        Assert.Contains("bull_risks", DefinitionCascade.Downstream("bull_case", parallel));
    }
}
