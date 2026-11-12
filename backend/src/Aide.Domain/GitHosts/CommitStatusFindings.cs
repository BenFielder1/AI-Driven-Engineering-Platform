using System.Globalization;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.Domain.GitHosts;

/// <summary>Which host a commit status came from (FR-139); it names the finding ("pipeline job" or "build").</summary>
public enum CommitStatusHost
{
    Gitlab = 0,
    Bitbucket = 1,
}

/// <summary>
/// FR-139: a GitLab commit status or pipeline job on the head (<c>GET …/repository/commits/:sha/statuses</c>): <c>name</c>,
/// <c>status</c> (<c>pending</c>, <c>running</c>, <c>success</c>, <c>failed</c>, <c>canceled</c>, <c>skipped</c>, …),
/// <c>allow_failure</c> and <c>target_url</c>.
/// </summary>
public sealed record GitLabCommitStatus(string Name, string? Status, bool AllowFailure, string? TargetUrl, string? Id = null);

/// <summary>
/// FR-139: a Bitbucket commit status (<c>GET …/commit/{sha}/statuses</c>): <c>name</c> (or <c>key</c>), <c>state</c>
/// (<c>SUCCESSFUL</c>, <c>FAILED</c>, <c>INPROGRESS</c>, <c>STOPPED</c>) and <c>url</c>.
/// </summary>
public sealed record BitbucketCommitStatus(string Name, string? State, string? Url);

/// <summary>
/// FR-139 (amends FR-85): GitLab and Bitbucket CI statuses as a deterministic review input, in 0.2.0's check shape
/// (<see cref="CheckObservation"/>) and finding shape (<see cref="ReviewFinding"/>). A failed status the host requires for
/// merge (GitLab "pipelines must succeed" and the job not <c>allow_failure</c>; Bitbucket
/// <c>require_passing_builds_to_merge</c>) is a major <c>testing</c> finding (−0.10); other failures are minor. Pending
/// statuses are waited for up to 15 minutes, re-checked every 30 seconds (0.2.0 <see cref="ExternalCheckRules"/>). Pure.
/// </summary>
public static class CommitStatusFindings
{
    /// <summary>
    /// GitLab statuses as checks (the first status per name: GitLab lists the latest first), <c>required</c> when the
    /// project requires pipelines to succeed and the job may not fail.
    /// </summary>
    public static IReadOnlyList<CheckObservation> FromGitLab(IEnumerable<GitLabCommitStatus> statuses, bool pipelinesMustSucceed)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return Distinct(statuses.Select(s =>
        {
            var (status, conclusion) = GitLabMergeReadiness.StatusOf(s.Status);
            return new CheckObservation(s.Name, status, conclusion, pipelinesMustSucceed && !s.AllowFailure, s.TargetUrl) { RunId = s.Id };
        }));
    }

    /// <summary>Bitbucket statuses as checks, <c>required</c> when the branch requires passing builds.</summary>
    public static IReadOnlyList<CheckObservation> FromBitbucket(IEnumerable<BitbucketCommitStatus> statuses, bool requirePassingBuilds)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return Distinct(statuses.Select(s =>
        {
            var (status, conclusion) = BitbucketStatusOf(s.State);
            return new CheckObservation(s.Name, status, conclusion, requirePassingBuilds, s.Url);
        }));
    }

    /// <summary>A Bitbucket commit status state: <c>SUCCESSFUL</c>, <c>FAILED</c>, <c>STOPPED</c> (cancelled); anything
    /// else (<c>INPROGRESS</c>) is still running.</summary>
    public static (CheckRunStatus Status, CheckRunConclusion? Conclusion) BitbucketStatusOf(string? state) => state switch
    {
        "SUCCESSFUL" => (CheckRunStatus.Completed, CheckRunConclusion.Success),
        "FAILED" => (CheckRunStatus.Completed, CheckRunConclusion.Failure),
        "STOPPED" => (CheckRunStatus.Completed, CheckRunConclusion.Cancelled),
        _ => (CheckRunStatus.InProgress, null),
    };

    /// <summary>FR-139: wait for pending statuses up to 15 minutes (0.2.0 rule, <see cref="ExternalCheckRules.Decide"/>).</summary>
    public static ChecksWaitDecision Wait(IEnumerable<CheckObservation> checks, DateTimeOffset? waitingSince, DateTimeOffset now) =>
        ExternalCheckRules.Decide(checks, waitingSince, now);

    /// <summary>
    /// FR-139: one <c>testing</c> finding per failed status: major when required for merge ("required pipeline job
    /// test:backend failed"), else minor ("pipeline job lint failed"); the evidence is the name and the job or build URL.
    /// </summary>
    /// <remarks>
    /// FR-236, FR-237: a failed <b>required</b> status is a failure finding (<c>failure:check:&lt;name&gt;</c>) whose evidence
    /// is the §4.38 excerpt of the GitLab job trace or Bitbucket step log in <paramref name="excerpts"/> (by name), headed by
    /// the name and URL; without one, the name and URL as before. Other failures are unchanged.
    /// </remarks>
    public static IReadOnlyList<ReviewFinding> Findings(IEnumerable<CheckObservation> checks, CommitStatusHost host, IReadOnlyDictionary<string, FailureExcerptResult>? excerpts = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var noun = host == CommitStatusHost.Gitlab ? "pipeline job" : "build";
        return
        [
            .. checks.Where(MergeReadinessRules.IsFailing)
                .Select(c => c.Required
                    ? ExternalCheckRules.Failure(c, Severity.Major, string.Create(CultureInfo.InvariantCulture, $"required {noun} {c.Name} failed"), excerpts)
                    : new ReviewFinding(
                        ReviewCategory.Testing,
                        Severity.Minor,
                        string.Create(CultureInfo.InvariantCulture, $"{noun} {c.Name} failed"),
                        Source: FindingSource.Deterministic,
                        Evidence: ExternalCheckRules.Head(c))),
        ];
    }

    private static List<CheckObservation> Distinct(IEnumerable<CheckObservation> checks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. checks.Where(c => seen.Add(c.Name))];
    }
}
