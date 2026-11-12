using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Review;

namespace Aide.Domain.Deployments;

/// <summary>
/// FR-89: the deterministic checks that a Vercel project's repository stops Vercel's automatic deploys of Tevrin branches
/// (Tevrin deploys them explicitly). The repository's <c>vercel.json</c> (in the Vercel project's root directory) must set
/// <c>git.deploymentEnabled["&lt;prefix&gt;**"]</c> to <c>false</c> for every prefix the project uses (§4.36, FR-233:
/// <c>tevrin/</c>, plus <c>aide/</c> for projects with an <c>aide/</c> branch); production deploys of the default branch stay on.
/// <list type="bullet">
/// <item><see cref="PlanWarning"/>: a plan of a stack that deploys to Vercel (FR-129: the composition's Vercel host, formerly a
/// preset's deploy target), on a base without <c>vercel.json</c>, where no step declares it, gets a
/// <c>MISSING_VERCEL_CONFIG</c> warning (never blocking). Unknown base files: no warning.</item>
/// <item><see cref="ReviewFindings"/>: in review, for a project with Vercel deployment configured, a missing or wrong
/// <c>vercel.json</c> on the branch is a major <c>architecture</c> finding (deterministic, so it carries the §4.7 penalty).</item>
/// </list>
/// </summary>
public static class VercelConfigRules
{
    /// <summary>The Vercel project configuration file.</summary>
    public const string FileName = "vercel.json";

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
    /// The <c>MISSING_VERCEL_CONFIG</c> warning, or <c>null</c>: only when the stack deploys to Vercel
    /// (<paramref name="deploysToVercel"/>), the base files are known (<paramref name="baseFiles"/>; empty for an empty
    /// repository) and hold no <c>vercel.json</c> in the Vercel root directory (<paramref name="rootDirectory"/>; <c>null</c>:
    /// the repository root), and no step declares one there.
    /// </summary>
    public static ValidationWarning? PlanWarning(IReadOnlyList<PlanStep> steps, bool deploysToVercel, IReadOnlySet<string>? baseFiles, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var path = ConfigPath(rootDirectory);
        if (!deploysToVercel || baseFiles is null || baseFiles.Any(f => IsConfigPath(f, path)))
        {
            return null;
        }

        if (steps.Any(s => s.Files.Any(f => IsConfigPath(f.Path, path))))
        {
            return null;
        }

        return new ValidationWarning(
            ErrorCodes.MissingVercelConfig,
            $"No step declares {path}, and the repository has none: Vercel would deploy every Tevrin branch on its own.",
            [$"Add {path} to a step (the scaffold step for a new repository) with git.deploymentEnabled {BranchControlRules.VercelSuggestion()}."]);
    }

    /// <summary>
    /// The review findings for the branch's <c>vercel.json</c> at <paramref name="path"/>: <paramref name="content"/> is its
    /// content, or <c>null</c> when the file is missing. Empty when it blocks every prefix in <paramref name="requiredPrefixes"/>
    /// (§4.36, FR-233; <c>null</c>: <see cref="BranchControlRules.TevrinOnly"/>). The message names the missing pattern(s).
    /// </summary>
    public static IReadOnlyList<ReviewFinding> ReviewFindings(string path, string? content, IReadOnlyList<string>? requiredPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var required = requiredPrefixes ?? BranchControlRules.TevrinOnly;
        if (content is null)
        {
            return [Finding($"{path} is missing: add it with git.deploymentEnabled {BranchControlRules.VercelSuggestion()} so Vercel doesn't deploy Tevrin branches on its own (FR-89).", path)];
        }

        var missing = BranchControlRules.VercelMissing(content, required);
        return missing.Count == 0
            ? []
            : [Finding($"{path} doesn't stop Vercel deploying {BranchControlRules.Names(missing)} on its own: set git.deploymentEnabled {BranchControlRules.Names(missing, quoted: true)} to false, with no other true rule matching. Tevrin deploys its branches explicitly (FR-89).", path)];
    }

    private static ReviewFinding Finding(string message, string path) => new(ReviewCategory.Architecture, Severity.Major, message, path);
}
