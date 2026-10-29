using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Releases;
using Aide.Infrastructure.Persistence.Conversion;

namespace Aide.Api.Features.Git;

/// <summary>FR-84: one reason AIDE can't merge the version's PR itself, with copy for the Release page and the gate.</summary>
public sealed record MergeReadinessReasonDto(MergeReadinessReasonCode Code, string Message);

/// <summary>
/// FR-84, FR-85: a check run or commit status on the PR head. <c>conclusion</c> is set once <c>status</c> is
/// <c>completed</c> (else <c>null</c>). <c>required</c> is true when the default branch's rules require it.
/// <c>detailsUrl</c> links the check on GitHub (or the CI provider), when known.
/// </summary>
public sealed record CheckRunDto(string Name, CheckRunStatus Status, CheckRunConclusion? Conclusion, bool Required, string? DetailsUrl);

/// <summary>
/// FR-84 merge readiness of the version's PR, as of <c>checkedAt</c>. <c>canMerge</c> is whether AIDE's token may merge it
/// now (repository permission and allowed merge methods, branch rules, the PR's <c>mergeStateStatus</c> and
/// <c>reviewDecision</c>, required checks). <c>willAutoMerge</c> is <c>canMerge</c> under the <c>aide_merges</c> strategy
/// ("AIDE will merge automatically"); otherwise "A merge on GitHub will be needed" with <c>reasons</c> (empty when
/// <c>canMerge</c>, except <c>manual_strategy</c>). <c>checks</c> are the PR head's check runs and commit statuses.
/// <c>mergeStateStatus</c> and <c>reviewDecision</c> are GitHub's GraphQL values verbatim (e.g. <c>CLEAN</c>,
/// <c>BLOCKED</c>; <c>APPROVED</c>, <c>REVIEW_REQUIRED</c>), <c>null</c> when unknown. For a GitLab project (FR-138)
/// <c>mergeStateStatus</c> is the merge request's <c>detailed_merge_status</c> verbatim (e.g. <c>mergeable</c>,
/// <c>not_approved</c>), <c>reviewDecision</c> is <c>null</c> and <c>checks</c> are the head's pipeline jobs and commit
/// statuses (FR-139). For a Local project
/// <c>canMerge</c> is true with no checks. The merge itself stays authoritative (FR-65).
/// </summary>
public sealed record MergeReadinessDto(
    bool CanMerge,
    bool WillAutoMerge,
    MergeStrategy MergeStrategy,
    IReadOnlyList<MergeReadinessReasonDto> Reasons,
    IReadOnlyList<CheckRunDto> Checks,
    string? MergeStateStatus,
    string? ReviewDecision,
    DateTimeOffset CheckedAt);

/// <summary>Reads merge-readiness reasons stored as JSON (<c>release.merge_reasons</c>, FR-65).</summary>
public static class MergeReasonsJson
{
    /// <summary>The stored reasons, or <c>null</c> when none were recorded or they can't be read.</summary>
    public static IReadOnlyList<MergeReadinessReasonDto>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return [.. AideJson.Deserialize<List<MergeReadinessReasonDto?>>(json).OfType<MergeReadinessReasonDto>()];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
