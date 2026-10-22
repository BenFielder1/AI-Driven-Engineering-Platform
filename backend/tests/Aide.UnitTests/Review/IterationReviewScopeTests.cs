using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Review;
using Aide.Domain.Specs;

namespace Aide.UnitTests.Review;

/// <summary>§4.13 review scope (D-V2-4): new/changed coverage, regressions on unchanged requirements.</summary>
public class IterationReviewScopeTests
{
    private static readonly string[] Categories = ["feature_coverage", "api_contracts", "data_model", "architecture", "security", "testing"];

    private static readonly IReadOnlyList<SpecRequirement> Tagged =
    [
        Requirement("FR-1", RequirementTag.Unchanged, "Sign in"),
        Requirement("FR-2", RequirementTag.Changed, "Export"),
        Requirement("FR-3", RequirementTag.New, "Import"),
        Requirement("FR-4", RequirementTag.Removed, "Fax"),
    ];

    private static SpecRequirement Requirement(string id, RequirementTag tag, string title) =>
        new(id, int.Parse(id[3..], System.Globalization.CultureInfo.InvariantCulture), title, [tag], true, 1);

    private static CodeReviewResult Parse(object? regressions = null, object[]? missing = null, object[]? drift = null, decimal score = 0.95m) =>
        CodeReviewOutput.Parse(
            JsonSerializer.Serialize(new
            {
                categoryScores = Categories.Select(c => new { category = c, score }),
                violations = Array.Empty<object>(),
                missingFeatures = missing ?? [],
                driftAreas = drift ?? [],
                regressions,
                summary = "ok",
            }),
            maxFilesPerStep: 20).Value;

    private static ReviewAssessmentInput Input(CodeReviewResult agent) => new(agent, [], 0.90m, 1, 3, 0, null);

    [Fact]
    public void S4_13_A_broken_unchanged_requirement_is_a_regression_with_an_agent_violation_and_a_major_drift_area()
    {
        var agent = Parse(regressions: new object[]
        {
            new { requirementId = "fr-1", category = "security", description = "Sign-in no longer checks the password.", evidence = "src/auth.ts:12" },
        });

        var result = IterationReviewScope.Assess(Input(agent), Tagged);

        var regression = Assert.Single(result.Regressions);
        Assert.Equal(new RegressionEntry("FR-1", ReviewCategory.Security, "Sign-in no longer checks the password.", "src/auth.ts:12"), regression);
        var violation = Assert.Single(result.Assessment.Violations);
        Assert.Equal((ReviewCategory.Security, Severity.Major, FindingSource.Agent), (violation.Category, violation.Severity, violation.Source));
        Assert.Equal("Regression of FR-1 (Sign in): Sign-in no longer checks the password.", violation.Message);
        var area = Assert.Single(result.ScopedAgent.DriftAreas);
        Assert.Equal(("Regression: FR-1", Severity.Major), (area.Area, area.Severity));

        // No deterministic penalty: the category keeps the agent's score; drift is low (one major area = 2).
        var security = result.Assessment.Categories.Single(c => c.Category == ReviewCategory.Security);
        Assert.Equal((0.95m, 0m), (security.Score, security.Penalty));
        Assert.Equal(0.95m, result.Assessment.Overall);
        Assert.Equal(DriftLevel.Low, result.Assessment.Drift);
        Assert.Equal(ReviewDecision.ReleaseEligible, result.Assessment.Decision.Decision);
    }

    [Fact]
    public void S4_13_Feature_coverage_counts_only_new_and_changed_missing_features()
    {
        var agent = Parse(missing:
        [
            new { id = "FR-1", title = "Sign in", reason = "the form is gone" },
            new { id = "FR-2", title = "Export", reason = "no headers" },
            new { id = "FR-4", title = "Fax", reason = "not found" },
            "A general gap",
        ]);

        var scoped = IterationReviewScope.Scope(agent, Tagged);

        Assert.Equal(["FR-2 Export: no headers", "A general gap"], scoped.Agent.MissingFeatures);
        var regression = Assert.Single(scoped.Regressions);
        Assert.Equal(("FR-1", ReviewCategory.FeatureCoverage, "FR-1 Sign in: the form is gone"), (regression.RequirementId, regression.Category, regression.Description));
    }

    [Fact]
    public void S4_13_Reported_regressions_of_new_or_changed_ids_are_missing_features_and_others_are_dropped()
    {
        var agent = Parse(regressions: new object[]
        {
            new { requirementId = "FR-3", description = "Import fails on empty files." },
            new { requirementId = "FR-4", description = "Fax is gone." },
            new { requirementId = "FR-99", description = "Unknown." },
            new { requirementId = "FR-1", description = "Broken." },
            new { requirementId = "FR-1", description = "Broken again." },
        });

        var scoped = IterationReviewScope.Scope(agent, Tagged);

        Assert.Equal(["FR-3: Import fails on empty files."], scoped.Agent.MissingFeatures);
        Assert.Equal(["FR-1"], scoped.Regressions.Select(r => r.RequirementId));
        Assert.Equal(ReviewCategory.FeatureCoverage, scoped.Regressions[0].Category);
        Assert.Equal(scoped.Regressions, scoped.Agent.Regressions);
    }

    [Fact]
    public void S4_13_Outside_iterations_the_reply_is_unchanged_and_there_are_no_regressions()
    {
        var agent = Parse(regressions: new object[] { new { requirementId = "FR-1", description = "Broken." } }, missing: [new { id = "FR-1", reason = "x" }]);
        IReadOnlyList<SpecRequirement> untagged = [new("FR-1", 1, "Sign in", [], true, 1)];

        foreach (var requirements in new[] { null, untagged })
        {
            var result = IterationReviewScope.Assess(Input(agent), requirements);
            Assert.Empty(result.Regressions);
            Assert.Equal(agent.MissingFeatures, result.ScopedAgent.MissingFeatures);
            Assert.Empty(result.Assessment.Violations);
        }
    }

    [Fact]
    public void S4_13_Two_regressions_reach_moderate_drift_with_the_weights_and_levels_unchanged()
    {
        IReadOnlyList<SpecRequirement> requirements = [.. Tagged, Requirement("FR-5", RequirementTag.Unchanged, "Profile")];
        var agent = Parse(
            regressions: new object[]
            {
                new { requirementId = "FR-1", description = "a" },
                new { requirementId = "FR-5", description = "b" },
            },
            score: 0.80m);

        var result = IterationReviewScope.Assess(Input(agent), requirements);

        Assert.Equal(2, result.Regressions.Count);
        Assert.Equal(DriftLevel.Moderate, result.Assessment.Drift); // 2 + 2 = 4
        Assert.Equal(0.80m, result.Assessment.Overall);
        Assert.Equal(ReviewDecision.ReviewRequired, result.Assessment.Decision.Decision);
    }

    [Fact]
    public void S4_13_A_regression_without_an_id_or_with_an_unknown_category_is_INVALID_OUTPUT()
    {
        var reply = JsonSerializer.Serialize(new
        {
            categoryScores = Categories.Select(c => new { category = c, score = 0.9m }),
            regressions = new object[]
            {
                new { requirementId = "login", description = "x" },
                new { requirementId = "FR-1", category = "style", description = "x" },
            },
        });

        var result = CodeReviewOutput.Parse(reply, 20);

        Assert.Equal(ErrorCodes.InvalidOutput, result.Error!.Code);
        Assert.Equal(2, result.Error.Details.Count);
    }
}
