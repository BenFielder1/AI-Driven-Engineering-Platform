using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Outcomes;

namespace Tevrin.Engine.Tests.Outcomes;

/// <summary>FR-353, §4.74: outcome score validation, the Brier score and the prompt-comparison means.</summary>
public class OutcomeScoringTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly OutcomeTarget RunTarget = new(Guid.CreateVersion7(), null, null, null);

    private static OutcomeScoreInput Brier(double? prediction, double? value, OutcomeTarget? target = null) =>
        new(target ?? RunTarget, "won", OutcomeScoreKind.ProbabilityBrier, value, prediction, Observed, "crm");

    [Theory]
    [InlineData(0.7, 1, 0.09)]
    [InlineData(0.2, 1, 0.64)]
    [InlineData(0.2, 0, 0.04)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 1, 1)]
    public void FR353_brier_score_is_the_squared_difference_of_prediction_and_outcome(double prediction, double outcome, double expected)
    {
        Assert.Equal(expected, OutcomeScoring.Brier(prediction, outcome), 10);

        var valid = OutcomeScoring.Validate(Brier(prediction, outcome));

        Assert.True(valid.IsSuccess);
        Assert.Equal(expected, valid.Value.Score, 10);
        Assert.Equal(prediction, valid.Value.Prediction);
        Assert.Equal(outcome, valid.Value.Value);
    }

    [Fact]
    public void FR353_spec_examples_give_0_09_and_0_64()
    {
        Assert.Equal(0.09, OutcomeScoring.Validate(Brier(0.7, 1)).Value.Score, 10);
        Assert.Equal(0.64, OutcomeScoring.Validate(Brier(0.2, 1)).Value.Score, 10);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void FR353_brier_prediction_outside_0_to_1_is_invalid(double prediction)
    {
        var invalid = OutcomeScoring.Validate(Brier(prediction, 1));

        Assert.True(invalid.IsFailure);
        Assert.Equal(EngineErrorCodes.Validation, invalid.Error!.Code);
        Assert.Equal("prediction", invalid.Error.Field);
    }

    [Fact]
    public void FR353_brier_needs_a_prediction()
    {
        var invalid = OutcomeScoring.Validate(Brier(null, 1));

        Assert.Equal("prediction", invalid.Error!.Field);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(-1)]
    public void FR353_brier_value_must_be_0_or_1(double value)
    {
        var invalid = OutcomeScoring.Validate(Brier(0.5, value));

        Assert.Equal("value", invalid.Error!.Field);
    }

    [Fact]
    public void FR353_number_score_is_the_value_as_given()
    {
        var valid = OutcomeScoring.Validate(new OutcomeScoreInput(RunTarget, " revenue ", OutcomeScoreKind.Number, 1234.5, null, Observed, " erp "));

        Assert.True(valid.IsSuccess);
        Assert.Equal(1234.5, valid.Value.Score);
        Assert.Equal("revenue", valid.Value.Metric);
        Assert.Equal("erp", valid.Value.Source);
        Assert.Null(valid.Value.Prediction);
    }

    [Fact]
    public void FR353_number_score_rejects_a_prediction()
    {
        var invalid = OutcomeScoring.Validate(new OutcomeScoreInput(RunTarget, "m", OutcomeScoreKind.Number, 1, 0.5, Observed, "s"));

        Assert.Equal("prediction", invalid.Error!.Field);
    }

    [Fact]
    public void FR353_artifact_target_with_version_and_variant_is_valid()
    {
        Assert.True(OutcomeScoring.Validate(Brier(0.5, 0, new OutcomeTarget(null, "summary", 2, "v1"))).IsSuccess);
        Assert.True(OutcomeScoring.Validate(Brier(0.5, 0, new OutcomeTarget(null, "summary", 1, null))).IsSuccess);
    }

    [Fact]
    public void FR353_target_must_be_exactly_one_of_agent_run_or_artifact()
    {
        Assert.Equal("target", OutcomeScoring.Validate(Brier(0.5, 1, new OutcomeTarget(null, null, null, null))).Error!.Field);
        Assert.Equal("target", OutcomeScoring.Validate(Brier(0.5, 1, new OutcomeTarget(Guid.CreateVersion7(), "summary", 1, null))).Error!.Field);
        Assert.Equal("target", OutcomeScoring.Validate(Brier(0.5, 1) with { Target = null }).Error!.Field);
    }

    [Fact]
    public void FR353_artifact_target_needs_a_key_and_a_version_of_1_or_more()
    {
        Assert.Equal("target.artifactKey", OutcomeScoring.Validate(Brier(0.5, 1, new OutcomeTarget(null, " ", 1, null))).Error!.Field);
        Assert.Equal("target.version", OutcomeScoring.Validate(Brier(0.5, 1, new OutcomeTarget(null, "summary", 0, null))).Error!.Field);
        Assert.Equal("target.version", OutcomeScoring.Validate(Brier(0.5, 1, new OutcomeTarget(null, "summary", null, null))).Error!.Field);
    }

    [Fact]
    public void FR353_metric_source_kind_value_and_observed_at_are_required_with_every_problem_listed()
    {
        var invalid = OutcomeScoring.Validate(new OutcomeScoreInput(RunTarget, " ", null, null, null, null, ""));

        Assert.Equal(EngineErrorCodes.Validation, invalid.Error!.Code);
        Assert.Equal("metric", invalid.Error.Field);
        Assert.Equal(5, invalid.Error.Details.Count);
    }

    [Fact]
    public void FR353_prompt_means_are_plain_means_over_scored_targets_per_metric()
    {
        var means = OutcomeScoring.Means(
        [
            new AttributedOutcomeScore("writer", "2", "won", "a", 0.09),
            new AttributedOutcomeScore("writer", "2", "won", "b", 0.64),

            // A target scored twice on a metric counts once, with the mean of its scores.
            new AttributedOutcomeScore("writer", "2", "won", "c", 0.0),
            new AttributedOutcomeScore("writer", "2", "won", "c", 0.5),
            new AttributedOutcomeScore("writer", "2", "revenue", "a", 10),
            new AttributedOutcomeScore("writer", "1", "won", "d", 1),
        ]);

        Assert.Equal(
            [("writer", "1", "won", 1), ("writer", "2", "revenue", 1), ("writer", "2", "won", 3)],
            means.Select(m => (m.Agent, m.PromptVersion, m.Metric, m.Count)));
        Assert.Equal((0.09 + 0.64 + 0.25) / 3, means.Single(m => m.PromptVersion == "2" && m.Metric == "won").MeanScore, 10);
        Assert.Equal(10, means.Single(m => m.Metric == "revenue").MeanScore);
    }

    [Fact]
    public void FR353_no_scores_give_no_means() => Assert.Empty(OutcomeScoring.Means([]));
}
