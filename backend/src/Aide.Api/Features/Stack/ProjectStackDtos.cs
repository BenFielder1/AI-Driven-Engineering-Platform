using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Api.Features.Stack;

// ---------- GET / PUT /api/projects/:id/stack ----------

/// <summary>
/// Settings → Stack (FR-117, FR-119): the stack of the project's current release version.
/// <list type="bullet">
/// <item><c>composition</c>: the pinned normalised composition with its derived fields (§4.18), <c>null</c> for "No stack
/// (general)" and, for a version migrated from a retired v2 preset, that preset as history (<c>legacyPreset</c>). <c>catalogVersion</c>/<c>catalogHash</c> are
/// what it was pinned against; <c>currentCatalogVersion</c>/<c>currentCatalogHash</c> the catalog shipped now (a new
/// version re-pins to it).</item>
/// <item><c>locked</c> with <c>lockedReason</c> (<c>plan_locked</c>, <c>not_in_development</c>): <c>PUT</c> is
/// <c>STACK_LOCKED</c> then.</item>
/// <item><c>stackChange</c>: the layers that differ from the stack the version's base code was built with, and whether
/// that is a stack change (the base commit has code: an iteration or an import), for the stack-change warning.</item>
/// <item><c>repoVcs</c>: the VCS option fixed by the project's repository host (R5; <c>null</c>: a Local repository, any
/// VCS).</item>
/// </list>
/// </summary>
public sealed record ProjectStackDto(
    Guid ReleaseVersionId,
    string Version,
    ReleaseVersionStatus VersionStatus,
    StackCompositionDto? Composition,
    string? CatalogVersion,
    string? CatalogHash,
    string CurrentCatalogVersion,
    string CurrentCatalogHash,
    bool Locked,
    StackLockReason? LockedReason,
    StackChangeDto StackChange,
    LegacyPresetDto? LegacyPreset,
    string? RepoVcs);

/// <summary>
/// FR-117: <c>layersChanged</c> (catalog layers: <c>frontend</c>, <c>backend</c>, <c>data</c>, <c>vcs</c>, <c>deploy</c>)
/// against the stack the version's base code was built with; <c>isStackChange</c> when <c>baseHasCode</c> and a layer
/// changed (the spec then needs a "Stack Migration" section).
/// </summary>
public sealed record StackChangeDto(bool BaseHasCode, IReadOnlyList<string> LayersChanged, bool IsStackChange);

/// <summary>
/// FR-129: the retired v2 preset a migrated version was pinned to, kept as history: its id, name (the id when unknown),
/// version and <c>label</c>, "Preset nextjs-supabase@1.0.1 (retired, migrated)".
/// </summary>
public sealed record LegacyPresetDto(string PresetId, string PresetName, string? PresetVersion, string Label);

// ---------- POST /api/stack/detect ----------

/// <summary>FR-118: the repository to detect the stack of.</summary>
public sealed record DetectStackRequest
{
    [Required]
    public required DetectStackGitRequest Git { get; init; }
}

/// <summary>
/// FR-118: where the repository is. <c>provider</c>: <c>github_app</c> (<c>repo</c> = <c>owner/name</c>, read with the
/// caller's GitHub connection: 409 <c>GIT_NOT_CONNECTED</c> without one), <c>github_pat</c> (<c>repo</c> = <c>owner/name</c>
/// or <c>https://github.com/owner/name</c>, with the optional <c>token</c>; public repositories need none) or <c>local</c>
/// (<c>repo</c> = the directory under <c>Git:Local:ImportRoot</c>, Development and E2E only). <c>ref</c> is the branch, tag
/// or commit to read (default: the default branch). <c>instanceId</c> is for GitLab instances (not used by these providers:
/// 400 <c>VALIDATION</c> when sent).
/// </summary>
public sealed record DetectStackGitRequest
{
    [Required]
    public required GitProvider Provider { get; init; }

    public Guid? InstanceId { get; init; }

    [Required]
    [StringLength(Projects.ProjectLimits.UrlMaxLength, MinimumLength = 1)]
    public required string Repo { get; init; }

    [MaxLength(Domain.Projects.ImportRules.TagMaxLength)]
    public string? Ref { get; init; }

    /// <summary><c>github_pat</c> only: the GitHub token to read a private repository. Used for this request only, never stored.</summary>
    [MaxLength(Projects.ProjectLimits.SecretMaxLength)]
    public string? Token { get; init; }
}

/// <summary>
/// FR-118: the detected stack, to pre-fill the wizard's Stack step. <c>suggestions</c> are the detected choices (one per
/// choice id: <c>frontend</c>, <c>backend</c>, <c>data</c>, <c>vcs</c> — fixed to the repo's host —, <c>frontendHost</c>,
/// <c>backendHost</c>, <c>databaseHost</c>) with the layer path, the data mode and the evidence; <c>conflicts</c> are
/// choices left empty because matches disagree, with every candidate. Choices in neither list weren't detected.
/// <c>ref</c> and <c>commit</c> are what was read; <c>truncated</c>: the repository has more files than were listed.
/// </summary>
public sealed record StackDetectionDto(
    string Ref,
    string Commit,
    IReadOnlyList<StackDetectedChoiceDto> Suggestions,
    IReadOnlyList<StackDetectionConflictDto> Conflicts,
    bool Truncated);

/// <summary>A detected choice: the option id, the layer path (frontend, backend), the data mode and the evidence (e.g. "`next` 16.1 in `package.json`").</summary>
public sealed record StackDetectedChoiceDto(string Choice, string Option, string? Path, string? Mode, IReadOnlyList<string> Evidence);

/// <summary>A choice whose matches disagree: left empty, with each candidate.</summary>
public sealed record StackDetectionConflictDto(string Choice, IReadOnlyList<StackDetectionCandidateDto> Candidates);

public sealed record StackDetectionCandidateDto(string Option, string? Path, IReadOnlyList<string> Evidence);
