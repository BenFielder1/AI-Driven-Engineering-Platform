using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-139: GitLab and Bitbucket CI statuses as deterministic review findings.</summary>
public class CommitStatusFindingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<ReviewCategory, decimal> Perfect = ComplianceScoring.Weights.Keys.ToDictionary(c => c, _ => 1m);

    [Fact]
    public void FR139_GitLab_required_pipeline_job_failure_is_a_major_testing_finding_costing_0_10()
    {
        GitLabCommitStatus[] statuses =
        [
            new("test:backend", "failed", AllowFailure: false, "https://gitlab.example.com/acme/todo/-/jobs/42"),
            new("build", "success", AllowFailure: false, "https://gitlab.example.com/acme/todo/-/jobs/41"),
        ];

        var findings = CommitStatusFindings.Findings(CommitStatusFindings.FromGitLab(statuses, pipelinesMustSucceed: true), CommitStatusHost.Gitlab);

        var finding = Assert.Single(findings);
        Assert.Equal(ReviewCategory.Testing, finding.Category);
        Assert.Equal(Severity.Major, finding.Severity);
        Assert.Equal(FindingSource.Deterministic, finding.Source);
        Assert.Equal("required pipeline job test:backend failed", finding.Message);
        Assert.Equal("test:backend: https://gitlab.example.com/acme/todo/-/jobs/42", finding.Evidence);
        Assert.Equal(0.90m, ComplianceScoring.Score(Perfect, findings).CategoryScores[ReviewCategory.Testing]);
    }

    [Fact]
    public void FR139_GitLab_failures_not_required_for_merge_are_minor()
    {
        GitLabCommitStatus[] statuses =
        [
            new("lint", "failed", AllowFailure: true, null),
            new("test", "failed", AllowFailure: false, null),
        ];

        var whenRequired = CommitStatusFindings.Findings(CommitStatusFindings.FromGitLab(statuses, true), CommitStatusHost.Gitlab);
        var whenNot = CommitStatusFindings.Findings(CommitStatusFindings.FromGitLab(statuses, false), CommitStatusHost.Gitlab);

        Assert.Equal([Severity.Minor, Severity.Major], whenRequired.Select(f => f.Severity));
        Assert.Equal("pipeline job lint failed", whenRequired[0].Message);
        Assert.Equal("lint", whenRequired[0].Evidence);
        Assert.All(whenNot, f => Assert.Equal(Severity.Minor, f.Severity));
    }

    [Fact]
    public void FR139_GitLab_keeps_the_latest_status_per_job_name()
    {
        GitLabCommitStatus[] statuses = [new("test", "success", false, null), new("test", "failed", false, null)];

        var checks = CommitStatusFindings.FromGitLab(statuses, true);

        Assert.Single(checks);
        Assert.Empty(CommitStatusFindings.Findings(checks, CommitStatusHost.Gitlab));
    }

    [Fact]
    public void FR139_Bitbucket_failed_build_is_major_when_passing_builds_are_required()
    {
        BitbucketCommitStatus[] statuses = [new("Pipeline #7", "FAILED", "https://bitbucket.org/acme/todo/pipelines/7"), new("deploy", "STOPPED", null)];

        var required = CommitStatusFindings.Findings(CommitStatusFindings.FromBitbucket(statuses, requirePassingBuilds: true), CommitStatusHost.Bitbucket);
        var optional = CommitStatusFindings.Findings(CommitStatusFindings.FromBitbucket(statuses, requirePassingBuilds: false), CommitStatusHost.Bitbucket);

        Assert.Equal(["required build Pipeline #7 failed", "required build deploy failed"], required.Select(f => f.Message));
        Assert.All(required, f => Assert.Equal(Severity.Major, f.Severity));
        Assert.All(optional, f => Assert.Equal(Severity.Minor, f.Severity));
    }

    [Theory]
    [InlineData("SUCCESSFUL", CheckRunStatus.Completed, CheckRunConclusion.Success)]
    [InlineData("FAILED", CheckRunStatus.Completed, CheckRunConclusion.Failure)]
    [InlineData("STOPPED", CheckRunStatus.Completed, CheckRunConclusion.Cancelled)]
    [InlineData("INPROGRESS", CheckRunStatus.InProgress, null)]
    public void FR139_Bitbucket_states_map_to_checks(string state, CheckRunStatus status, CheckRunConclusion? conclusion) =>
        Assert.Equal((status, conclusion), CommitStatusFindings.BitbucketStatusOf(state));

    [Theory]
    [InlineData("success", CheckRunStatus.Completed, CheckRunConclusion.Success)]
    [InlineData("failed", CheckRunStatus.Completed, CheckRunConclusion.Failure)]
    [InlineData("canceled", CheckRunStatus.Completed, CheckRunConclusion.Cancelled)]
    [InlineData("skipped", CheckRunStatus.Completed, CheckRunConclusion.Skipped)]
    [InlineData("manual", CheckRunStatus.Completed, CheckRunConclusion.ActionRequired)]
    [InlineData("pending", CheckRunStatus.Queued, null)]
    [InlineData("running", CheckRunStatus.InProgress, null)]
    public void FR139_GitLab_statuses_map_to_checks(string state, CheckRunStatus status, CheckRunConclusion? conclusion) =>
        Assert.Equal((status, conclusion), GitLabMergeReadiness.StatusOf(state));

    [Fact]
    public void FR139_Pending_statuses_are_waited_for_up_to_15_minutes()
    {
        var checks = CommitStatusFindings.FromGitLab([new GitLabCommitStatus("test", "running", false, null)], true);

        var first = CommitStatusFindings.Wait(checks, null, Now);
        Assert.True(first.Wait);
        Assert.Equal(Now.AddSeconds(30), first.RecheckAt);

        var later = CommitStatusFindings.Wait(checks, Now, Now.AddMinutes(14));
        Assert.True(later.Wait);

        var timedOut = CommitStatusFindings.Wait(checks, Now, Now.AddMinutes(15));
        Assert.False(timedOut.Wait);
        Assert.True(timedOut.TimedOut);
    }
}
