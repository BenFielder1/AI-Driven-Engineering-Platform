using System.Text.RegularExpressions;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Presets;

/// <summary>The closed vocabularies of <c>preset.json</c>.</summary>
public static class PresetVocabulary
{
    /// <summary>FR-88: deployment targets a preset can support.</summary>
    public static IReadOnlyList<string> DeployTargets { get; } = ["vercel"];

    /// <summary>FR-88: the targets a required environment variable applies to (Vercel's environments).</summary>
    public static IReadOnlyList<string> EnvTargets { get; } = ["production", "preview", "development"];

    /// <summary>FR-77: <c>supabase/migrations/**</c> — every <c>create table</c> has a matching <c>enable row level security</c>.</summary>
    public const string RlsOnEveryTable = "rls_on_every_table";

    /// <summary>FR-77: no <c>SUPABASE_SERVICE_ROLE_KEY</c> or other non-<c>NEXT_PUBLIC_</c> secret env name in a <c>"use client"</c> file.</summary>
    public const string NoSecretEnvInClient = "no_secret_env_in_client";

    /// <summary>The deterministic preset rule ids <c>review.deterministic</c> may list (implemented by the review checks).</summary>
    public static IReadOnlyList<string> DeterministicRules { get; } = [RlsOnEveryTable, NoSecretEnvInClient];
}

/// <summary>FR-74 rules about choosing and pinning presets.</summary>
public static partial class PresetRules
{
    /// <summary>The longest preset id (the <c>preset_id</c> columns are 100 characters).</summary>
    public const int IdMaxLength = 100;

    /// <summary>The longest preset version (the <c>preset_version</c> columns are 50 characters).</summary>
    public const int VersionMaxLength = 50;

    /// <summary>The agent key a scaffold-first rule expects at step 1 unless <c>planRules.scaffolder</c> names another.</summary>
    public const string DefaultScaffolder = "project-scaffolder";

    /// <summary>Whether <paramref name="id"/> is a valid preset id or agent key: lowercase kebab-case (<c>nextjs-supabase</c>).</summary>
    public static bool IsKebabId(string? id) => id is { Length: > 0 and <= IdMaxLength } && KebabPattern().IsMatch(id);

    /// <summary>FR-74: what agent runs record: <c>&lt;id&gt;@&lt;version&gt;</c>.</summary>
    public static string RunLabel(string presetId, string presetVersion) => $"{presetId}@{presetVersion}";

    /// <summary>
    /// FR-74: whether a version's first implementation plan has been locked. A plan that was approved and later
    /// invalidated (or found corrupted) still counts: the preset shaped it. <paramref name="plans"/> are the version's
    /// <c>implementation_plan</c> artifacts (status and approval time).
    /// </summary>
    public static bool PlanWasLocked(IEnumerable<(ArtifactStatus Status, DateTimeOffset? ApprovedAt)> plans) =>
        plans.Any(p => p.Status == ArtifactStatus.Locked || p.ApprovedAt is not null);

    /// <summary>
    /// FR-74: whether an Owner may change the preset of the project's current version. Only a version in development
    /// whose first implementation plan isn't locked yet; otherwise <c>PRESET_LOCKED</c> (also when nothing is in
    /// development: the preset is picked again when starting a new version).
    /// </summary>
    public static Result CanChange(ReleaseVersionStatus? currentVersionStatus, bool planWasLocked)
    {
        if (currentVersionStatus != ReleaseVersionStatus.InDevelopment)
        {
            return Result.Failure(new DomainError(ErrorCodes.PresetLocked, "No version is in development. Pick the preset when you start the next version."));
        }

        return planWasLocked
            ? Result.Failure(new DomainError(ErrorCodes.PresetLocked, "The preset can't change once the version's implementation plan is locked. Start a new version to use another preset."))
            : Result.Success();
    }

    /// <summary>
    /// FR-74: the preset id a request names: blank → <c>null</c> (no preset), else trimmed. Whether it exists is the
    /// catalogue's business (<c>PRESET_NOT_FOUND</c>).
    /// </summary>
    public static string? NormaliseId(string? presetId) => string.IsNullOrWhiteSpace(presetId) ? null : presetId.Trim();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabPattern();
}
