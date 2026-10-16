using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Execution;
using Aide.Domain.Review;
using Aide.Domain.Specs;
using Aide.Infrastructure.Agents;

namespace Aide.UnitTests.Specs;

/// <summary>
/// FR-75, §4.13, FR-70, FR-76: the Fake agents' replies pass the v2 validation in every spec mode, so Fake + Local flows
/// (v1 E2E, two-version and import E2E) still run end to end.
/// </summary>
public class FakeIterationResponderTests
{
    private const string ProjectRequest = "<project_request>\n# Demo: Todo API\n\n## Summary\nAdd CSV export.\n</project_request>";

    private static LlmRequest Request(AgentKind agent, string user) => new()
    {
        Model = "fake-1",
        System = "system",
        Messages = [new LlmMessage(LlmRole.User, user)],
        Agent = agent,
    };

    private static string Spec(string mode, string? baseline = null) =>
        new FakeSpecGeneratorResponder().Respond(Request(
            AgentKind.SpecGenerator,
            $"{ProjectRequest}\n\n<spec_mode>\n{mode}\n</spec_mode>\n\n<baseline_specification>\n{baseline}\n</baseline_specification>"));

    [Fact]
    public void FR75_The_Fake_first_version_spec_has_an_id_and_a_criterion_for_every_requirement()
    {
        var spec = Spec(SpecValidationContext.FirstVersionMode);

        var result = SpecValidator.Validate(spec, SpecValidationContext.FirstVersion);

        Assert.True(result.IsValid, result.Error?.Message);
        Assert.Equal(["FR-1", "FR-2", "FR-3", "FR-4"], result.Scan.Ids);
        Assert.DoesNotContain(SpecSections.ChangesInThisVersion, spec, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_13_The_Fake_new_version_spec_is_cumulative_and_tagged_against_its_baseline()
    {
        var baseline = Spec(SpecValidationContext.FirstVersionMode);
        var context = SpecValidationContext.ForVersion(isIteration: true, baseline);

        var spec = Spec(context.Mode, baseline);

        var result = SpecValidator.Validate(spec, context);
        Assert.True(result.IsValid, result.Error?.Message);
        Assert.Equal(["FR-5"], result.Scan.NewOrChangedIds);
        Assert.All(result.Requirements.Where(r => r.Id != "FR-5"), r => Assert.Equal(RequirementTag.Unchanged, r.Tag));
        Assert.Contains("## Changes in this version", spec, StringComparison.Ordinal);

        // A second iteration numbers its new requirement after every earlier one.
        var next = Spec(SpecValidationContext.NewVersionMode, spec);
        Assert.Equal(["FR-6"], RequirementParser.Parse(next).NewOrChangedIds);
    }

    [Fact]
    public void S4_13_The_Fake_import_first_version_spec_reverse_engineers_unchanged_behaviour()
    {
        var context = SpecValidationContext.ForVersion(isIteration: true, baselineSpecification: null);
        Assert.Equal(SpecValidationContext.ImportFirstVersionMode, context.Mode);

        var result = SpecValidator.Validate(Spec(context.Mode), context);

        Assert.True(result.IsValid, result.Error?.Message);
        Assert.Contains(result.Requirements, r => r.Tag == RequirementTag.Unchanged);
    }

    [Fact]
    public void S4_13_The_spec_context_follows_the_version()
    {
        Assert.Same(SpecValidationContext.FirstVersion, SpecValidationContext.ForVersion(isIteration: false, null));
        Assert.True(SpecValidationContext.ForVersion(true, null).IsFirstImportVersion);
        var newVersion = SpecValidationContext.ForVersion(true, "## 2. Functional Requirements\n- **FR-7** X. Given a, when b, then c.");
        Assert.Equal(SpecValidationContext.NewVersionMode, newVersion.Mode);
        Assert.Equal(["FR-7"], newVersion.BaselineRequirementIds);
    }

    [Fact]
    public void FR76_The_Fake_plan_covers_every_new_or_changed_requirement_of_an_iteration()
    {
        var baseline = Spec(SpecValidationContext.FirstVersionMode);
        var spec = Spec(SpecValidationContext.NewVersionMode, baseline);

        var reply = new FakePlanGeneratorResponder().Respond(Request(AgentKind.PlanGenerator, $"{ProjectRequest}\n\n<technical_specification>\n{spec}\n</technical_specification>"));
        var plan = PlanDocument.ParseAgentOutput(reply, maxFilesPerStep: 3);

        Assert.True(plan.IsSuccess, plan.Error?.Message);
        Assert.All(plan.Value, s => Assert.Equal(PlanAgents.Generalist, s.Agent));
        Assert.Contains(plan.Value, s => s.Covers.Contains("FR-5"));
        Assert.Null(PlanScope.Check(plan.Value, RequirementParser.Parse(spec).Requirements));
    }

    [Fact]
    public void FR76_The_Fake_first_version_plan_covers_the_four_requirements()
    {
        var spec = Spec(SpecValidationContext.FirstVersionMode);

        var reply = new FakePlanGeneratorResponder().Respond(Request(AgentKind.PlanGenerator, $"{ProjectRequest}\n\n<technical_specification>\n{spec}\n</technical_specification>"));

        var covered = PlanDocument.ParseAgentOutput(reply, maxFilesPerStep: 3).Value.SelectMany(s => s.Covers).Distinct().Order().ToList();
        Assert.Equal(["FR-1", "FR-2", "FR-3", "FR-4"], covered);
    }

    [Fact]
    public void FR70_The_Fake_codebase_summary_has_the_eight_sections()
    {
        var summary = new FakeRepositoryAnalystResponder().Respond(Request(
            AgentKind.RepositoryAnalyst,
            "<project_name>\nLegacy\n</project_name>\n\n<version_request>\nAdd CSV export.\n</version_request>\n\n<base>\nv2.3.0 (abc)\n</base>"));

        Assert.True(CodebaseSummaryRules.Check(summary).IsSuccess);
        Assert.Contains("v2.3.0", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_13_The_Fake_review_reports_its_scripted_regressions()
    {
        var reply = new FakeCodeReviewResponder(FakeCodeReviewScript.Default with { Regressions = [("FR-2", "Listing is broken.")] })
            .Respond(Request(AgentKind.CodeReview, "<review_cycle>\n1\n</review_cycle>\n\n<code>\n- src/a.js\n</code>"));

        var parsed = CodeReviewOutput.Parse(reply, 5);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        var regression = Assert.Single(parsed.Value.Regressions);
        Assert.Equal("FR-2", regression.RequirementId);
        Assert.Equal(ReviewCategory.FeatureCoverage, regression.Category);
    }
}
