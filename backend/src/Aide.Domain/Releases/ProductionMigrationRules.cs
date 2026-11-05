using Aide.Domain.Entities;
using Aide.Domain.GitHosts;

namespace Aide.Domain.Releases;

/// <summary>What the merge poll does about the production migrations of the PR head it sees (§4.21, FR-153).</summary>
public enum PreMergeMigrationStep
{
    /// <summary>Nothing: the head is migrated, a run is still going, or its failure was already reported.</summary>
    None = 0,

    /// <summary>Run the migrations on the head, once merge readiness passes.</summary>
    Run = 1,

    /// <summary>A run on the head crashed: close it as interrupted, then run again once merge readiness passes.</summary>
    RunAfterInterruption = 2,
}

/// <summary>The newest pre-merge production run of a release attempt.</summary>
public sealed record PreMergeRun(string GitSha, MigrationRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// §4.21, FR-153: when Supabase BaaS production migrations run relative to merge readiness, and how a merge is compared with
/// what was migrated. Pure.
/// </summary>
public static class ProductionMigrationRules
{
    /// <summary>How old a still-running run must be to count as crashed (the rollout job's limit too).</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// §4.21: why merge readiness hasn't passed for the production migrations; <c>null</c> when it has. <c>merge_conflict</c>
    /// comes first, then <c>pr_not_open</c>, then the first other reason. Three reasons don't hold migrations back, because
    /// they don't say the PR can't merge: <c>manual_strategy</c> is the strategy itself, and <c>no_permission</c> and
    /// <c>merge_method_not_allowed</c> are about Tevrin's own token and merge method, while a person can still merge. With
    /// <paramref name="reviewUnknown"/> (see <see cref="ReviewUnknown"/>) <c>review_required</c> is unknown readiness, which
    /// doesn't block either.
    /// </summary>
    public static MergeReadinessReasonCode? BlockingReason(IEnumerable<MergeReadinessReasonCode> reasons, bool reviewUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var blocking = reasons
            .Where(code => code is not (MergeReadinessReasonCode.ManualStrategy or MergeReadinessReasonCode.NoPermission or MergeReadinessReasonCode.MergeMethodNotAllowed))
            .Where(code => !(reviewUnknown && code == MergeReadinessReasonCode.ReviewRequired))
            .ToList();
        return blocking.Contains(MergeReadinessReasonCode.MergeConflict) ? MergeReadinessReasonCode.MergeConflict
            : blocking.Contains(MergeReadinessReasonCode.PrNotOpen) ? MergeReadinessReasonCode.PrNotOpen
            : blocking.Count > 0 ? blocking[0]
            : null;
    }

    /// <summary>
    /// FR-84: GitHub without GraphQL for the token reports <c>review_required</c> conservatively, because it can't see the
    /// review decision (both GraphQL values are unknown). That is unknown readiness, not a missing review. GitLab and
    /// Bitbucket read approvals directly, so their <c>review_required</c> is always real.
    /// </summary>
    public static bool ReviewUnknown(GitProvider provider, string? mergeStateStatus, string? reviewDecision) =>
        provider is GitProvider.GithubPat or GitProvider.GithubApp && mergeStateStatus is null && reviewDecision is null;

    /// <summary>
    /// §4.19, §4.21: GitLab's transient <c>detailed_merge_status</c> (reported as <c>checks_pending</c> after Tevrin's re-reads)
    /// isn't a reason the merge request can't merge: readiness is unknown, as when it can't be read. Only for this gate: what
    /// people see of readiness stays "still checking".
    /// </summary>
    public static bool ReadinessUnknown(GitProvider provider, string? hostStatus) =>
        provider == GitProvider.Gitlab && GitLabMergeReadiness.IsTransient(hostStatus);

    /// <summary>
    /// The merge poll's step for <paramref name="head"/>, given the attempt's newest pre-merge run. No run, or a run on
    /// another commit: run. Succeeded on this head: nothing. Failed on this head: Owners were told, so it runs again only
    /// when the database became usable after the failure (<paramref name="databaseUsableSinceFailure"/>, e.g. restored) or
    /// the head moves, never on every poll. Still running: nothing until <see cref="InterruptedAfter"/>.
    /// </summary>
    public static PreMergeMigrationStep NextStep(PreMergeRun? last, string head, bool databaseUsableSinceFailure, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(head);
        if (last is null || !string.Equals(last.GitSha, head, StringComparison.OrdinalIgnoreCase))
        {
            return PreMergeMigrationStep.Run;
        }

        return last.Status switch
        {
            MigrationRunStatus.Failed when databaseUsableSinceFailure => PreMergeMigrationStep.Run,
            MigrationRunStatus.Running when now - last.StartedAt > InterruptedAfter => PreMergeMigrationStep.RunAfterInterruption,
            _ => PreMergeMigrationStep.None,
        };
    }

    /// <summary>
    /// §4.21: a merge reached the default branch on a head no pre-merge run succeeded on. <paramref name="mergedHead"/> is
    /// <c>null</c> when the host can't say which head it merged: only a version with no migrated head at all then counts.
    /// Commits are compared by prefix, because Bitbucket reports short hashes.
    /// </summary>
    public static bool MergedUnmigrated(string? mergedHead, IReadOnlyCollection<string> migratedHeads)
    {
        ArgumentNullException.ThrowIfNull(migratedHeads);
        return mergedHead is null
            ? migratedHeads.Count == 0
            : !migratedHeads.Any(migrated => BitbucketProjectRules.SameCommit(migrated, mergedHead));
    }
}
