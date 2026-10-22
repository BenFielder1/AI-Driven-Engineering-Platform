using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.UnitTests.Review;

/// <summary>FR-85: check runs and commit statuses as a deterministic review input.</summary>
public class ExternalCheckRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly CheckObservation Pending = new("ci / test", CheckRunStatus.InProgress, null, true, null);

    private static readonly CheckObservation Failed = new("ci / test", CheckRunStatus.Completed, CheckRunConclusion.Failure, true, "https://github.com/acme/widgets/runs/1");

    [Fact]
    public void FR85_Given_a_failing_required_ci_test_check_then_a_major_testing_finding_applies_minus_0_10()
    {
        var finding = Assert.Single(ExternalCheckRules.Findings([Failed, Failed with { Name = "lint", Required = false }]));

        Assert.Equal(
            new ReviewFinding(ReviewCategory.Testing, Severity.Major, "required check ci / test failed", null, FindingSource.Deterministic, "ci / test: https://github.com/acme/widgets/runs/1"),
            finding);
        Assert.Equal(0.10m, ComplianceScoring.Penalty(finding.Severity));
    }

    [Fact]
    public void FR85_Passing_skipped_or_pending_required_checks_are_not_findings() =>
        Assert.Empty(ExternalCheckRules.Findings([Pending, Failed with { Conclusion = CheckRunConclusion.Success }, Failed with { Conclusion = CheckRunConclusion.Skipped }]));

    [Fact]
    public void FR85_Pending_checks_start_a_wait_rechecked_every_30_seconds()
    {
        var first = ExternalCheckRules.Decide([Pending], null, Now);
        var later = ExternalCheckRules.Decide([Pending], Now, Now.AddMinutes(14));

        Assert.Equal((true, Now.AddSeconds(30), Now), (first.Wait, first.RecheckAt, first.WaitingSince));
        Assert.Equal((true, Now.AddMinutes(14).AddSeconds(30), Now), (later.Wait, later.RecheckAt, later.WaitingSince));
    }

    [Fact]
    public void FR85_After_15_minutes_review_proceeds_with_a_timeout_warning()
    {
        var decision = ExternalCheckRules.Decide([Pending], Now, Now.AddMinutes(15));

        Assert.Equal((false, 900, true), (decision.Wait, decision.WaitedSeconds, decision.TimedOut));
    }

    [Fact]
    public void FR85_Without_pending_checks_review_proceeds_and_records_the_wait_only_if_it_waited()
    {
        Assert.Equal(ChecksWaitDecision.Proceed(null, false), ExternalCheckRules.Decide([Failed], null, Now));
        Assert.Equal(ChecksWaitDecision.Proceed(90, false), ExternalCheckRules.Decide([Failed], Now, Now.AddSeconds(90)));
        Assert.Equal(ChecksWaitDecision.Proceed(null, false), ExternalCheckRules.Decide([], null, Now));
    }
}
