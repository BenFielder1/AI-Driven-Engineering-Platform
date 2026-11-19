using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-276 acceptance, the §4.49 worked example, and the §4.51 SDLC sketch.</summary>
public class DefinitionValidatorAcceptanceTests
{
    [Fact]
    public void FR276_worked_example_reports_both_errors_together()
    {
        // §4.49: loop: { to: draft } on stage check, no max_iterations, and validate: [{ guard: fixture.missing }].
        var yaml = Edit("fixture.report-consistent", "fixture.missing");
        yaml = Edit("    output: report", "    output: report\n    loop: { to: draft }", yaml);

        Assert.Equal(
            [(DefinitionErrorCodes.UncappedLoop, "stages[1].loop"), (DefinitionErrorCodes.UnknownGuard, "stages[1].validate[0].guard")],
            Errors(yaml).Order());
    }

    [Fact]
    public void FR276_an_unregistered_guard_on_the_plan_stage_is_reported_at_its_path()
    {
        var yaml = Edit("  - id: check", "  - id: plan");
        yaml = Edit("fixture.report-consistent", "velocity.load-rules", yaml);
        var error = Assert.Single(PipelineDefinitionLoader.ParseAndValidate(yaml, Catalog).Errors);
        Assert.Equal((DefinitionErrorCodes.UnknownGuard, "stages[1].validate[0].guard"), (error.Code, error.Path));
        Assert.Contains("velocity.load-rules", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR276_a_loop_without_max_iterations_is_uncapped() =>
        Assert.Contains((DefinitionErrorCodes.UncappedLoop, "stages[1].loop"), Errors(Edit("    output: report", "    output: report\n    loop: { to: draft, on: redo }")));

    [Fact]
    public void FR276_a_stage_nothing_reaches_is_unreachable() =>
        Assert.Contains((DefinitionErrorCodes.UnreachableStage, "stages[1]"), Errors(Edit("    output: draft", "    output: draft\n    on_success: summarise")));

    [Fact]
    public void FR276_three_problems_are_reported_in_one_result()
    {
        var yaml = Edit("fixture.report-consistent", "velocity.load-rules");
        yaml = Edit("    output: report", "    output: report\n    loop: { to: draft }", yaml);
        yaml = Edit("    output: draft", "    output: draft\n    on_success: summarise", yaml);

        Assert.Equal(
            [
                (DefinitionErrorCodes.UncappedLoop, "stages[1].loop"),
                (DefinitionErrorCodes.UnknownGuard, "stages[1].validate[0].guard"),
                (DefinitionErrorCodes.UnreachableStage, "stages[1]"),
            ],
            Errors(yaml).Order());
    }

    [Fact]
    public void FR276_parse_and_reference_errors_are_reported_together()
    {
        var yaml = Edit("format: json", "format: xml");
        yaml = Edit("tools: [fixture.lookup]", "tools: [fixture.search]", yaml);
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "artifacts.draft.format"), (DefinitionErrorCodes.UnknownTool, "stages[0].agent.tools[0]")],
            Errors(yaml));
    }

    [Fact]
    public void FR275_FR281_FR285_FR286_the_sdlc_sketch_parses_and_validates_when_its_names_are_registered()
    {
        var catalog = new PackCatalog
        {
            Guards =
            [
                "sdlc.leaving-analysis", "sdlc.plan-valid", "sdlc.plan-has-steps", "sdlc.working-copy-prepared",
                "sdlc.plan-steps-completed", "sdlc.release-eligible", "sdlc.review-cycle-ready",
            ],
            JobTypes = ["stack_analysis", "idea_refinement", "spec_generation", "plan_generation", "execution", "review"],
            Settings = ["review_cycles"],
            ProducedArtifacts = ["source_items", "codebase_summary"],
        };

        var result = PipelineDefinitionLoader.ParseAndValidate(Fixtures.Read("sdlc-sketch.pipeline.yaml"), catalog);

        Assert.Empty(result.Errors);
        var sdlc = result.Definition!;
        Assert.Equal(["refinement", "analysis"], sdlc.Start);
        Assert.Equal(["analysis", "refinement", "specification", "planning", "execution", "review", "release"], sdlc.Stages.Select(s => s.Id));
        Assert.Equal("refinement", sdlc.SuccessorOf("analysis"));
        Assert.Equal(StageKind.Manual, sdlc.FindStage("execution")!.Kind);
        Assert.Equal("execution", sdlc.FindStage("review")!.Loop!.To);
        Assert.Equal([new GuardRequirement("sdlc.review-cycle-ready")], sdlc.FindStage("review")!.Loop!.Requires);
        Assert.Equal(["releasing"], sdlc.StatusChanges.Single(c => c.Name == "release_awaiting_merge").From);
        Assert.Equal(GenericRunStatus.Completed, sdlc.GenericStatusOf("released"));
        Assert.Equal([new ExclusiveJob("execution", ConcurrencyScope.Project)], sdlc.Concurrency.Exclusive);

        // Without the pack's registrations every reference is reported, all at once.
        var unregistered = PipelineDefinitionLoader.ParseAndValidate(Fixtures.Read("sdlc-sketch.pipeline.yaml"), PackCatalog.Empty).Errors;
        Assert.Contains(unregistered, e => e.Code == DefinitionErrorCodes.UnknownGuard);
        Assert.Contains(unregistered, e => e.Code == DefinitionErrorCodes.UnknownJobType);
        Assert.Contains(unregistered, e => e.Code == DefinitionErrorCodes.UnknownSetting);
        Assert.Contains(unregistered, e => e.Code == DefinitionErrorCodes.ArtifactNoProducer);
    }
}
