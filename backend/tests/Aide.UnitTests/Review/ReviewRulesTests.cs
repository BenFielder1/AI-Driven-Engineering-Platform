using Aide.Domain.Review;

namespace Aide.UnitTests.Review;

/// <summary>§4.5 (all six worked examples), §4.7, FR-38, FR-39, D2.</summary>
public class ReviewDecisionRulesTests
{
    private static ReviewDecisionResult Decide(decimal score, DriftLevel drift, int cycle, decimal? previous = null) =>
        ReviewDecisionRules.Decide(new ReviewDecisionInput(score, 0.90m, drift, cycle, 3, PreviousScore: previous));

    [Fact]
    public void S4_5_Example1_Score093_LowDrift_Cycle1_ReleaseEligible() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.ReleaseEligible, null), Decide(0.93m, DriftLevel.Low, 1));

    [Fact]
    public void FR39_Score082_Cycle1_ReviewRequired() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.ReviewRequired, null), Decide(0.82m, DriftLevel.Low, 1));

    [Fact]
    public void S4_5_Example3_Score082_Cycle3_EscalationMaxCycles() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.MaxCycles), Decide(0.82m, DriftLevel.Low, 3));

    [Fact]
    public void S4_5_Example4_Score084Then083_Cycle2_EscalationNoImprovement() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.NoImprovement), Decide(0.83m, DriftLevel.Low, 2, 0.84m));

    [Fact]
    public void S4_5_Example5_Score095_SevereDrift_Escalation() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.SevereDrift), Decide(0.95m, DriftLevel.Severe, 1));

    [Fact]
    public void S4_5_Example6_Score070_NoDrift_Escalation() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.LowScore), Decide(0.70m, DriftLevel.None, 1));

    [Fact]
    public void S4_5_Row1_score_exactly_075_is_not_low() =>
        Assert.Equal(ReviewDecision.ReviewRequired, Decide(0.75m, DriftLevel.Moderate, 1).Decision);

    [Fact]
    public void S4_5_Row2_score_equal_to_threshold_is_release_eligible() =>
        Assert.Equal(ReviewDecision.ReleaseEligible, Decide(0.90m, DriftLevel.Moderate, 3).Decision);

    [Fact]
    public void S4_5_Row4_equal_score_counts_as_no_improvement() =>
        Assert.Equal(EscalationReason.NoImprovement, Decide(0.84m, DriftLevel.Low, 2, 0.84m).Reason);

    [Fact]
    public void S4_5_Row4_improvement_in_cycle2_is_review_required() =>
        Assert.Equal(ReviewDecision.ReviewRequired, Decide(0.86m, DriftLevel.Low, 2, 0.84m).Decision);

    [Fact]
    public void S4_5_Row1_low_score_wins_over_max_cycles() =>
        Assert.Equal(EscalationReason.LowScore, Decide(0.60m, DriftLevel.Severe, 3, 0.80m).Reason);

    [Fact]
    public void FR14_Continue_raises_the_cycle_limit_so_cycle3_can_be_review_required()
    {
        var extra = ReviewCycleRules.ExtraAfterContinue(0);
        var result = ReviewDecisionRules.Decide(new ReviewDecisionInput(0.85m, 0.90m, DriftLevel.Low, 3, 3, extra, 0.82m));

        Assert.Equal(1, extra);
        Assert.Equal(4, ReviewCycleRules.EffectiveMaxCycles(3, extra));
        Assert.Equal(ReviewDecision.ReviewRequired, result.Decision);
    }

    [Fact]
    public void FR14_After_continue_the_extra_cycle_also_hits_max_cycles()
    {
        var result = ReviewDecisionRules.Decide(new ReviewDecisionInput(0.86m, 0.90m, DriftLevel.Low, 4, 3, 1, 0.85m));

        Assert.Equal(EscalationReason.MaxCycles, result.Reason);
    }

    [Fact]
    public void S4_8_Review_cycle_counter_starts_at_1_and_rollback_resets_it_to_0()
    {
        Assert.Equal(1, ReviewCycleRules.NextCycle(0));
        Assert.Equal(3, ReviewCycleRules.NextCycle(2));
        Assert.Equal(0, ReviewCycleRules.CycleAfterRollback);
    }
}

public class ComplianceScoringTests
{
    private static Dictionary<ReviewCategory, decimal> All(decimal score) =>
        Enum.GetValues<ReviewCategory>().ToDictionary(c => c, _ => score);

    [Fact]
    public void S4_7_Weights_sum_to_one_and_match_the_spec()
    {
        Assert.Equal(1.00m, ComplianceScoring.Weights.Values.Sum());
        Assert.Equal(0.30m, ComplianceScoring.Weights[ReviewCategory.FeatureCoverage]);
        Assert.Equal(0.20m, ComplianceScoring.Weights[ReviewCategory.ApiContracts]);
        Assert.Equal(0.15m, ComplianceScoring.Weights[ReviewCategory.DataModel]);
        Assert.Equal(0.15m, ComplianceScoring.Weights[ReviewCategory.Architecture]);
        Assert.Equal(0.10m, ComplianceScoring.Weights[ReviewCategory.Security]);
        Assert.Equal(0.10m, ComplianceScoring.Weights[ReviewCategory.Testing]);
    }

    [Fact]
    public void FR38_Overall_is_the_weighted_sum_rounded_to_3dp()
    {
        var scores = new Dictionary<ReviewCategory, decimal>
        {
            [ReviewCategory.FeatureCoverage] = 0.9m,
            [ReviewCategory.ApiContracts] = 0.85m,
            [ReviewCategory.DataModel] = 0.8m,
            [ReviewCategory.Architecture] = 0.7m,
            [ReviewCategory.Security] = 1.0m,
            [ReviewCategory.Testing] = 0.6555m,
        };

        // 0.27 + 0.17 + 0.12 + 0.105 + 0.10 + 0.06555 = 0.83055 → 0.831
        var result = ComplianceScoring.Score(scores, []);

        Assert.Equal(0.831m, result.Overall);
    }

    [Theory]
    [InlineData(Severity.Critical, 0.20)]
    [InlineData(Severity.Major, 0.10)]
    [InlineData(Severity.Minor, 0.03)]
    public void S4_7_Fixed_penalties_per_severity(Severity severity, double penalty) =>
        Assert.Equal((decimal)penalty, ComplianceScoring.Penalty(severity));

    [Fact]
    public void S4_7_Penalties_apply_to_their_category_and_stack()
    {
        var findings = new[]
        {
            new ReviewFinding(ReviewCategory.Security, Severity.Critical, "secret"),
            new ReviewFinding(ReviewCategory.Security, Severity.Minor, "weak"),
            new ReviewFinding(ReviewCategory.ApiContracts, Severity.Major, "missing endpoint"),
        };

        var result = ComplianceScoring.Score(All(0.9m), findings);

        Assert.Equal(0.67m, result.CategoryScores[ReviewCategory.Security]);
        Assert.Equal(0.80m, result.CategoryScores[ReviewCategory.ApiContracts]);
        Assert.Equal(0.9m, result.CategoryScores[ReviewCategory.Testing]);

        // 0.9 − 0.023 (security) − 0.02 (api) = 0.857
        Assert.Equal(0.857m, result.Overall);
    }

    [Fact]
    public void S4_7_Category_score_is_floored_at_zero()
    {
        var findings = Enumerable.Repeat(new ReviewFinding(ReviewCategory.Testing, Severity.Critical, "x"), 10);

        var result = ComplianceScoring.Score(All(0.5m), findings);

        Assert.Equal(0m, result.CategoryScores[ReviewCategory.Testing]);
        Assert.Equal(0.45m, result.Overall);
    }

    [Fact]
    public void Agent_scores_are_clamped_to_0_1()
    {
        var scores = All(1.4m);
        scores[ReviewCategory.Security] = -0.5m;

        var result = ComplianceScoring.Score(scores, []);

        Assert.Equal(0.9m, result.Overall);
    }

    [Fact]
    public void Missing_category_score_is_rejected()
    {
        var scores = All(0.9m);
        scores.Remove(ReviewCategory.Testing);

        Assert.Throws<ArgumentException>(() => ComplianceScoring.Score(scores, []));
    }
}

public class DriftRulesTests
{
    [Theory]
    [InlineData(new Severity[0], DriftLevel.None)]
    [InlineData(new[] { Severity.Minor }, DriftLevel.Low)]
    [InlineData(new[] { Severity.Critical }, DriftLevel.Low)]
    [InlineData(new[] { Severity.Critical, Severity.Minor }, DriftLevel.Moderate)]
    [InlineData(new[] { Severity.Critical, Severity.Critical, Severity.Major }, DriftLevel.Moderate)]
    [InlineData(new[] { Severity.Critical, Severity.Critical, Severity.Critical }, DriftLevel.Severe)]
    [InlineData(new[] { Severity.Major, Severity.Major, Severity.Major, Severity.Major, Severity.Minor }, DriftLevel.Severe)]
    public void S4_7_Drift_level_from_weighted_areas(Severity[] areas, DriftLevel expected) =>
        Assert.Equal(expected, DriftRules.Level(areas));

    [Theory]
    [InlineData(Severity.Critical, 3)]
    [InlineData(Severity.Major, 2)]
    [InlineData(Severity.Minor, 1)]
    public void S4_7_Drift_weights(Severity severity, int weight) =>
        Assert.Equal(weight, DriftRules.Weight(severity));
}

public class DeterministicChecksTests
{
    [Fact]
    public void S4_7_Secret_in_code_is_a_critical_security_finding()
    {
        var findings = DeterministicChecks.SecretScan([new SourceFile("src/config.ts", "const key = 'AKIAIOSFODNN7EXAMPLE';")]);

        var finding = Assert.Single(findings);
        Assert.Equal(ReviewCategory.Security, finding.Category);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal("src/config.ts", finding.Path);
        Assert.DoesNotContain("AKIA", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_7_Dependencies_outside_the_allowlist_are_architecture_findings()
    {
        var findings = DeterministicChecks.DependencyAllowlist(["react", "zod"], ["React", "zod", "lodash", "lodash"]);

        var finding = Assert.Single(findings);
        Assert.Equal(ReviewCategory.Architecture, finding.Category);
        Assert.Contains("lodash", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_7_Every_spec_endpoint_must_appear_in_the_code()
    {
        SourceFile[] code =
        [
            new("api/projects.cs", "app.MapGet(\"/api/projects/{id:guid}\", Get);"),
            new("api/health.py", "@app.get('/api/health')"),
        ];

        var findings = DeterministicChecks.EndpointCoverage(["/api/projects/:id", "/api/health", "/api/users/:id/role"], code);

        var finding = Assert.Single(findings);
        Assert.Equal(ReviewCategory.ApiContracts, finding.Category);
        Assert.Equal(Severity.Major, finding.Severity);
        Assert.Contains("/api/users/:id/role", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_match_is_literal_outside_parameters() =>
        Assert.NotEmpty(DeterministicChecks.EndpointCoverage(["/api/a.b"], [new SourceFile("x", "/api/aXb")]));
}
