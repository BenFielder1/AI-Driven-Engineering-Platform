using System.Globalization;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>A Netlify deploy-creation decision (§4.20): go ahead now, or wait until <see cref="RetryAt"/>.</summary>
public sealed record NetlifyDeployLimitDecision(bool Allowed, DateTimeOffset? RetryAt);

/// <summary>
/// §4.20 Netlify: deploy states, the deploy-creation limit (3 a minute and 100 a day per account, enforced before calling),
/// env contexts per AIDE environment, the provider deployment id (the deploy id, plus the commit and branch a preview must
/// build), plan-limit errors and the <c>netlify.toml</c> <c>[build] ignore</c> rule. Pure.
/// </summary>
public static class NetlifyRules
{
    public const int DeploysPerMinute = 3;

    public const int DeploysPerDay = 100;

    /// <summary>The build title AIDE sets (<c>aide:&lt;deployment id&gt;</c>), so a retry finds what an earlier attempt made.</summary>
    public const string TitlePrefix = "aide:";

    /// <summary>The Netlify GitHub App install page (FR-144 <c>PLATFORM_GIT_NOT_CONNECTED</c> link).</summary>
    public const string GitHubInstallUrl = "https://github.com/apps/netlify/installations/new";

    /// <summary>
    /// §4.20 state mapping: <c>new</c>, <c>pending_review</c>, <c>accepted</c>, <c>enqueued</c> → queued; <c>building</c>,
    /// <c>uploading</c>, <c>uploaded</c>, <c>preparing</c>, <c>prepared</c>, <c>processing</c>, <c>processed</c>,
    /// <c>retrying</c> → building; <c>ready</c> → ready; <c>error</c>, <c>rejected</c> → error. Netlify has no canceled
    /// state (a cancelled deploy reports <c>error</c>); anything unknown is building.
    /// </summary>
    public static DeploymentStatus StateOf(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "new" or "pending_review" or "accepted" or "enqueued" => DeploymentStatus.Queued,
        "ready" => DeploymentStatus.Ready,
        "error" or "rejected" => DeploymentStatus.Error,

        // §4.20: a build an ignore rule skipped (or one cancelled) never becomes ready: it ends, never stays Building.
        "skipped" or "canceled" or "cancelled" => DeploymentStatus.Error,
        _ => DeploymentStatus.Building,
    };

    /// <summary>The <c>id</c> placeholder of a preview whose build was handed to the site's build hook (§4.20 fallback).</summary>
    public const string HookPending = "hook";

    /// <summary>The attempt number of a preview built through the build hook (no further re-trigger after it).</summary>
    public const int HookAttempt = 3;

    /// <summary>The title of AIDE's preview build hook of a branch.</summary>
    public const string HookTitlePrefix = "aide previews ";

    /// <summary>
    /// §4.20: whether Netlify skipped or cancelled a build (state <c>skipped</c> or <c>canceled</c>, or an error whose
    /// message says the build was cancelled or skipped, as Netlify reports a build an ignore rule stopped).
    /// </summary>
    public static bool IsSkipped(string? state, string? errorMessage)
    {
        var s = state?.Trim().ToLowerInvariant();
        if (s is "skipped" or "canceled" or "cancelled")
        {
            return true;
        }

        if (s != "error" || string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        var message = errorMessage.ToLowerInvariant();
        return message.Contains("canceled build", StringComparison.Ordinal)
            || message.Contains("cancelled build", StringComparison.Ordinal)
            || message.Contains("build skipped", StringComparison.Ordinal)
            || message.Contains("skipped build", StringComparison.Ordinal);
    }

    /// <summary>The deploy title AIDE gives a build-hook build of <paramref name="sha"/> (to find it again).</summary>
    public static string HookDeployTitle(string sha) => $"{TitlePrefix}hook:{sha}";

    /// <summary>
    /// §4.20 adoption: whether a deploy's <paramref name="commitRef"/> is <paramref name="sha"/>. Both must be known and at
    /// least 7 characters (an empty or very short ref never matches); either may be the other's abbreviation.
    /// </summary>
    public static bool SameCommit(string? commitRef, string? sha) =>
        commitRef is { Length: >= MinCommitLength } && sha is { Length: >= MinCommitLength }
        && (commitRef.StartsWith(sha, StringComparison.OrdinalIgnoreCase) || sha.StartsWith(commitRef, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shortest commit reference adoption accepts.</summary>
    public const int MinCommitLength = 7;

    /// <summary>
    /// §4.20: whether one more deploy may be created for an account that created <paramref name="recent"/> deploys (any
    /// order): at most 3 in the last minute and 100 in the last day. When not, the wait is until the oldest deploy of the
    /// full window leaves it.
    /// </summary>
    public static NetlifyDeployLimitDecision Decide(IReadOnlyCollection<DateTimeOffset> recent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var day = recent.Where(t => t > now - TimeSpan.FromDays(1) && t <= now).Order().ToList();
        var minute = day.Where(t => t > now - TimeSpan.FromMinutes(1)).ToList();
        DateTimeOffset? retry = null;
        if (minute.Count >= DeploysPerMinute)
        {
            retry = minute[minute.Count - DeploysPerMinute] + TimeSpan.FromMinutes(1);
        }

        if (day.Count >= DeploysPerDay)
        {
            var dayRetry = day[day.Count - DeploysPerDay] + TimeSpan.FromDays(1);
            retry = retry is { } r && r > dayRetry ? r : dayRetry;
        }

        return retry is null ? new NetlifyDeployLimitDecision(true, null) : new NetlifyDeployLimitDecision(false, retry);
    }

    /// <summary>
    /// FR-145, §4.20: the Netlify contexts of an AIDE environment: production → <c>production</c>; preview →
    /// <c>deploy-preview</c> and <c>branch-deploy</c> (API branch builds run as branch deploys).
    /// </summary>
    public static IReadOnlyList<string> ContextsOf(DeploymentEnvironment environment) =>
        environment == DeploymentEnvironment.Production ? ["production"] : ["deploy-preview", "branch-deploy"];

    /// <summary>AIDE's id of a synced (key, environment) on Netlify: <c>netlify:&lt;environment&gt;:&lt;key&gt;</c>.</summary>
    public static string EnvId(string key, DeploymentEnvironment environment) =>
        string.Create(CultureInfo.InvariantCulture, $"netlify:{environment.ToWire()}:{key}");

    /// <summary>The (key, environment) of an <see cref="EnvId"/>, or <c>null</c>.</summary>
    public static (string Key, DeploymentEnvironment Environment)? ParseEnvId(string? id)
    {
        var parts = id?.Split(':', 3);
        return parts is { Length: 3 } && parts[0] == "netlify" && EnumStrings.TryParse<DeploymentEnvironment>(parts[1], out var env) && parts[2].Length > 0
            ? (parts[2], env)
            : null;
    }

    /// <summary>
    /// The provider deployment id of a deploy AIDE asked for: the deploy id alone (production, adopted), or
    /// <c>&lt;deploy&gt;|&lt;sha&gt;|&lt;attempt&gt;|&lt;branch&gt;</c> for a preview, whose branch build must be of the PR
    /// head (Netlify builds the branch HEAD).
    /// </summary>
    public static string DeploymentId(string deployId, string? sha = null, int attempt = 1, string? branch = null) =>
        sha is null || branch is null ? deployId : string.Create(CultureInfo.InvariantCulture, $"{deployId}|{sha}|{attempt}|{branch}");

    /// <summary>The parts of a <see cref="DeploymentId"/>.</summary>
    public static (string DeployId, string? Sha, int Attempt, string? Branch) ParseDeploymentId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split('|', 4);
        return parts.Length == 4 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            ? (parts[0], parts[1], attempt, parts[3])
            : (id, null, 1, null);
    }

    /// <summary>
    /// §4.20: what to do with a preview branch build that reports <paramref name="commitRef"/>: <c>true</c> when it built
    /// another commit than <paramref name="expectedSha"/> (the branch moved, or the build started before the push arrived).
    /// Unknown commits (not reported yet) match.
    /// </summary>
    public static bool BuiltOtherCommit(string? commitRef, string? expectedSha) =>
        !string.IsNullOrEmpty(commitRef) && !string.IsNullOrEmpty(expectedSha)
        && !commitRef.StartsWith(expectedSha, StringComparison.OrdinalIgnoreCase) && !expectedSha.StartsWith(commitRef, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FR-144: whether a Netlify refusal is a plan restriction (e.g. a private organisation repository without Pro): a 402,
    /// or a refusal whose message names a plan or an upgrade.
    /// </summary>
    public static bool IsPlanLimit(int? status, string? message)
    {
        if (status == 402)
        {
            return true;
        }

        if (status is not (400 or 403 or 422))
        {
            return false;
        }

        var lower = message?.ToLowerInvariant() ?? string.Empty;
        return lower.Contains("upgrade", StringComparison.Ordinal) || lower.Contains(" plan", StringComparison.Ordinal)
            || (lower.Contains("private repositor", StringComparison.Ordinal) && lower.Contains("org", StringComparison.Ordinal));
    }

    /// <summary>
    /// §4.20 branch control: whether <c>netlify.toml</c> has a <c>[build]</c> <c>ignore</c> command that names AIDE's
    /// branches (<c>aide/</c>), so Netlify skips builds of <c>aide/*</c> it starts by itself.
    /// </summary>
    public static bool IgnoresAideBranches(string? toml)
    {
        if (string.IsNullOrEmpty(toml))
        {
            return false;
        }

        var inBuild = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inBuild = line.Replace(" ", string.Empty, StringComparison.Ordinal) == "[build]";
                continue;
            }

            if (!inBuild || !line.StartsWith("ignore", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && line[..eq].Trim() == "ignore" && line[(eq + 1)..].Contains("aide/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
