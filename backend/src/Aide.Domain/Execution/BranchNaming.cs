using Aide.Domain.Releases;

namespace Aide.Domain.Execution;

/// <summary>
/// FR-232, §4.35: Tevrin's branch prefixes and the choice of a project's working branch. New branches start with
/// <see cref="Prefix"/> (<c>tevrin/</c>); a branch already recorded keeps its name, so projects started before 0.6.1 keep
/// their <see cref="LegacyPrefix"/> (<c>aide/</c>) branches (D-0.6.1-2).
/// </summary>
public static class BranchNaming
{
    /// <summary>The prefix of every new project and release-version branch (FR-232).</summary>
    public const string Prefix = "tevrin/";

    /// <summary>The prefix of branches recorded before 0.6.1 (0.1.0 FR-34, 0.2.0 FR-60), kept for those projects.</summary>
    public const string LegacyPrefix = "aide/";

    /// <summary>
    /// §4.35, in order: (1) the release version's recorded branch (<paramref name="recordedVersionBranch"/>); (2) else, for a
    /// project without release versions (<paramref name="hasReleaseVersion"/> false), the branch of its most recent recorded
    /// execution step (<paramref name="latestStepBranch"/>); (3) else the new name: <c>tevrin/&lt;slug&gt;/v&lt;version&gt;</c>
    /// (<see cref="ReleaseVersionRules.BranchName"/>) with a <paramref name="version"/>, <c>tevrin/&lt;slug&gt;-&lt;short-id&gt;</c>
    /// (<see cref="ExecutionRules.BranchName"/>) without. Blank recorded names count as none.
    /// </summary>
    public static string Choose(string? recordedVersionBranch, bool hasReleaseVersion, string? latestStepBranch, string slug, Guid projectId, SemVer? version)
    {
        ArgumentNullException.ThrowIfNull(slug);
        if (!string.IsNullOrWhiteSpace(recordedVersionBranch))
        {
            return recordedVersionBranch;
        }

        if (!hasReleaseVersion && !string.IsNullOrWhiteSpace(latestStepBranch))
        {
            return latestStepBranch;
        }

        return version is null ? ExecutionRules.BranchName(slug, projectId) : ReleaseVersionRules.BranchName(slug, projectId, version);
    }

    /// <summary>Whether <paramref name="branch"/> is a pre-0.6.1 <c>aide/</c> branch (§4.36).</summary>
    public static bool IsLegacy(string? branch) =>
        branch is not null && branch.StartsWith(LegacyPrefix, StringComparison.Ordinal);
}
