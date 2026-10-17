using System.ComponentModel.DataAnnotations;
using Aide.Domain.Presets;
using Aide.Domain.Review;

namespace Aide.Api.Features.Presets;

/// <summary>
/// A tech stack preset card (FR-74, §7.2 wizard Stack step and admin presets list): <c>stack</c> are the chips,
/// <c>deployTargets</c> the supported deployment targets (<c>vercel</c>). <c>contentHash</c> is the SHA-256 of the
/// preset's files (D-V2-12).
/// </summary>
public sealed record PresetSummaryDto(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Stack,
    IReadOnlyList<string> DeployTargets,
    string ContentHash);

/// <summary>A specialist agent of a preset (FR-76): the paths it owns (globs; <c>!</c> excludes) and its skills.</summary>
public sealed record PresetAgentDto(string Key, string Name, string Description, IReadOnlyList<string> Owns, IReadOnlyList<string> Skills);

/// <summary>An extra spec section the preset requires (FR-75) with its guidance.</summary>
public sealed record PresetSpecSectionDto(string Title, string Guidance);

/// <summary>A required environment variable (FR-88): <c>targets</c> ⊆ production/preview/development.</summary>
public sealed record PresetEnvVarDto(string Key, IReadOnlyList<string> Targets, bool Sensitive, string Description);

/// <summary>A review checklist item (FR-77), mapped to a v1 compliance category.</summary>
public sealed record PresetChecklistItemDto(ReviewCategory Category, string Item, Severity Severity);

/// <summary>The preset's review additions (FR-77): its checklist and deterministic rule ids.</summary>
public sealed record PresetReviewDto(IReadOnlyList<PresetChecklistItemDto> Checklist, IReadOnlyList<string> Deterministic);

/// <summary>
/// A protected path (§4.11): <c>existingOnBase</c> protects only files already on the version's base commit;
/// <c>except</c> globs stay writable.
/// </summary>
public sealed record PresetProtectedPathDto(string Glob, bool ExistingOnBase, IReadOnlyList<string> Except, string? Reason);

/// <summary>A preset in full (FR-74): the summary plus agents, spec sections, environment, review and protected paths. No prompt bodies.</summary>
public sealed record PresetDetailDto(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Stack,
    IReadOnlyList<string> DeployTargets,
    string ContentHash,
    IReadOnlyList<PresetAgentDto> Agents,
    IReadOnlyList<PresetSpecSectionDto> SpecSections,
    IReadOnlyList<PresetEnvVarDto> RequiredEnv,
    PresetReviewDto Review,
    IReadOnlyList<PresetProtectedPathDto> ProtectedPaths);

/// <summary>
/// FR-74: change the preset of the version in development (Owner), until its first implementation plan is locked.
/// <c>presetId</c> is a preset id from <c>GET /api/presets</c>, or <c>null</c> for no preset (general).
/// </summary>
public sealed record UpdateProjectPresetRequest
{
    [MaxLength(PresetRules.IdMaxLength)]
    public string? PresetId { get; init; }
}

internal static class PresetMapping
{
    public static PresetSummaryDto ToSummary(Preset p) =>
        new(p.Id, p.Name, p.Version, p.Description, p.Stack, p.DeployTargets, p.ContentHash);

    public static PresetDetailDto ToDetail(Preset p) => new(
        p.Id,
        p.Name,
        p.Version,
        p.Description,
        p.Stack,
        p.DeployTargets,
        p.ContentHash,
        [.. p.Agents.Select(a => new PresetAgentDto(a.Key, a.Name, a.Description, a.Owns.Patterns, a.Skills))],
        [.. p.SpecSections.Select(s => new PresetSpecSectionDto(s.Title, s.Guidance))],
        [.. p.RequiredEnv.Select(e => new PresetEnvVarDto(e.Key, e.Targets, e.Sensitive, e.Description))],
        new PresetReviewDto(
            [.. p.Review.Checklist.Select(c => new PresetChecklistItemDto(c.Category, c.Item, c.Severity))],
            p.Review.Deterministic),
        [.. p.ProtectedPaths.Select(pp => new PresetProtectedPathDto(pp.Glob.Pattern, pp.ExistingOnBase, [.. pp.Except.Select(e => e.Pattern)], pp.Reason))]);
}
