using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Domain.Review;

namespace Aide.Domain.Deployments;

/// <summary>
/// FR-89: the deterministic checks that a Vercel project's repository stops Vercel's automatic deploys of AIDE branches
/// (AIDE deploys them explicitly). The repository's <c>vercel.json</c> (in the Vercel project's root directory) must set
/// <c>git.deploymentEnabled["aide/**"]</c> to <c>false</c>; production deploys of the default branch stay on.
/// <list type="bullet">
/// <item><see cref="PlanWarning"/>: a plan for a preset that deploys to Vercel, on a base without <c>vercel.json</c>, where no
/// step declares it, gets a <c>MISSING_VERCEL_CONFIG</c> warning (never blocking). Unknown base files: no warning.</item>
/// <item><see cref="ReviewFindings"/>: in review, for a project with Vercel deployment configured, a missing or wrong
/// <c>vercel.json</c> on the branch is a major <c>architecture</c> finding (deterministic, so it carries the §4.7 penalty).</item>
/// </list>
/// </summary>
public static class VercelConfigRules
{
    /// <summary>The Vercel project configuration file.</summary>
    public const string FileName = "vercel.json";

    /// <summary>The <c>git.deploymentEnabled</c> key that matches AIDE's working branches (<c>aide/&lt;slug&gt;-…</c>).</summary>
    public const string AideBranches = "aide/**";

    /// <summary>Whether <paramref name="preset"/> lists <c>vercel</c> among its deploy targets.</summary>
    public static bool DeploysToVercel(Preset? preset) =>
        preset?.DeployTargets.Contains("vercel", StringComparer.OrdinalIgnoreCase) ?? false;

    /// <summary>The repository path of <c>vercel.json</c> for a Vercel project rooted at <paramref name="rootDirectory"/> (<c>.</c>: the repository root).</summary>
    public static string ConfigPath(string? rootDirectory)
    {
        var root = PathRules.Normalize(rootDirectory ?? string.Empty).Trim('/');
        return root is "" or "." ? FileName : $"{root}/{FileName}";
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the config file at <paramref name="configPath"/> (<see cref="ConfigPath"/>): the one
    /// comparison the plan warning and the review finding share. Git paths and Vercel's lookup are case-sensitive, so ordinal.
    /// </summary>
    public static bool IsConfigPath(string path, string configPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(configPath);
        return string.Equals(PathRules.Normalize(path), configPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>MISSING_VERCEL_CONFIG</c> warning, or <c>null</c>: only when the preset deploys to Vercel, the base files are
    /// known (<paramref name="baseFiles"/>; empty for an empty repository) and hold no <c>vercel.json</c> in the Vercel root
    /// directory (<paramref name="rootDirectory"/>; <c>null</c>: the repository root), and no step declares one there.
    /// </summary>
    public static ValidationWarning? PlanWarning(IReadOnlyList<PlanStep> steps, Preset? preset, IReadOnlySet<string>? baseFiles, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var path = ConfigPath(rootDirectory);
        if (!DeploysToVercel(preset) || baseFiles is null || baseFiles.Any(f => IsConfigPath(f, path)))
        {
            return null;
        }

        if (steps.Any(s => s.Files.Any(f => IsConfigPath(f.Path, path))))
        {
            return null;
        }

        return new ValidationWarning(
            ErrorCodes.MissingVercelConfig,
            $"No step declares {path}, and the repository has none: Vercel would deploy every AIDE branch on its own.",
            [$"Add {path} to a step (the scaffold step for a new repository) with git.deploymentEnabled {{ \"{AideBranches}\": false }}."]);
    }

    /// <summary>
    /// The review findings for the branch's <c>vercel.json</c> at <paramref name="path"/>: <paramref name="content"/> is its
    /// content, or <c>null</c> when the file is missing. Empty when it sets <c>git.deploymentEnabled["aide/**"]</c> to <c>false</c>.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> ReviewFindings(string path, string? content)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (content is null)
        {
            return [Finding($"{path} is missing: add it with git.deploymentEnabled {{ \"{AideBranches}\": false }} so Vercel doesn't deploy AIDE branches on its own (FR-89).", path)];
        }

        return DisablesAideBranches(content)
            ? []
            : [Finding($"{path} doesn't set git.deploymentEnabled[\"{AideBranches}\"] to false, so Vercel deploys AIDE branches on its own; AIDE deploys them explicitly (FR-89).", path)];
    }

    /// <summary>Whether the JSON <paramref name="content"/> sets <c>git.deploymentEnabled["aide/**"]</c> to <c>false</c> (unreadable JSON: no).</summary>
    public static bool DisablesAideBranches(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object
                && git.TryGetProperty("deploymentEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.Object
                && enabled.TryGetProperty(AideBranches, out var aide) && aide.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReviewFinding Finding(string message, string path) => new(ReviewCategory.Architecture, Severity.Major, message, path);
}
