using System.Globalization;
using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Review;

namespace Aide.Domain.Deployments;

/// <summary>§4.15: deployment states (<c>queued → building → ready | error | canceled</c>) and Vercel's <c>readyState</c>.</summary>
public static class DeploymentStates
{
    /// <summary>A state that never changes again: <c>ready</c>, <c>error</c> or <c>canceled</c>.</summary>
    public static bool IsTerminal(DeploymentStatus status) =>
        status is DeploymentStatus.Ready or DeploymentStatus.Error or DeploymentStatus.Canceled;

    /// <summary>
    /// §4.15: Vercel's <c>readyState</c> (or <c>state</c>) as an AIDE state: <c>QUEUED</c> → queued; <c>INITIALIZING</c>,
    /// <c>BUILDING</c>, <c>DEPLOYING</c> and anything unknown → building; <c>READY</c> → ready; <c>ERROR</c> → error;
    /// <c>CANCELED</c> → canceled.
    /// </summary>
    public static DeploymentStatus FromReadyState(string? readyState) => readyState?.Trim().ToUpperInvariant() switch
    {
        "QUEUED" => DeploymentStatus.Queued,
        "READY" => DeploymentStatus.Ready,
        "ERROR" => DeploymentStatus.Error,
        "CANCELED" or "CANCELLED" => DeploymentStatus.Canceled,
        _ => DeploymentStatus.Building,
    };

    /// <summary>
    /// §4.15: allowed state changes. Forward only (<c>queued → building</c>, either to a terminal state); staying in the same
    /// state is allowed (a poll without news); terminal states never change.
    /// </summary>
    public static bool CanTransition(DeploymentStatus from, DeploymentStatus to)
    {
        if (from == to)
        {
            return !IsTerminal(from);
        }

        return from switch
        {
            DeploymentStatus.Queued => true,
            DeploymentStatus.Building => to != DeploymentStatus.Queued,
            _ => false,
        };
    }
}

/// <summary>What the deployment poll does next (§4.15).</summary>
public enum DeploymentPollDecisionKind
{
    /// <summary>Poll again at <see cref="DeploymentPollDecision.NextPollAt"/>.</summary>
    Wait = 0,

    /// <summary>The deployment reached a terminal state; polling ends.</summary>
    Finished = 1,

    /// <summary>The 30-minute ceiling passed: the deployment becomes <c>error</c> with <c>DEPLOYMENT_TIMEOUT</c>.</summary>
    TimedOut = 2,
}

/// <summary>The deployment poll's decision.</summary>
public sealed record DeploymentPollDecision(DeploymentPollDecisionKind Kind, DateTimeOffset? NextPollAt);

/// <summary>
/// §4.15, NFR-REL2: polling a deployment. Every 10 seconds for the first 2 minutes, then every 30 seconds, with a
/// 30-minute ceiling after the deployment was recorded (then <c>error</c> with <c>DEPLOYMENT_TIMEOUT</c>). Pure.
/// </summary>
public static class DeploymentPollRules
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan FastPhase = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When to poll next after a poll at <paramref name="now"/> of a deployment recorded at <paramref name="since"/>: 10
    /// seconds later within the first 2 minutes, else 30 seconds later, never after the ceiling (the last poll runs at it).
    /// </summary>
    public static DateTimeOffset NextPollAt(DateTimeOffset since, DateTimeOffset now) => NextPollAt(since, now, TimeSpan.Zero);

    /// <summary>As <see cref="NextPollAt(DateTimeOffset, DateTimeOffset)"/>, never sooner than <paramref name="minInterval"/> (§4.20 Railway: 20 s).</summary>
    public static DateTimeOffset NextPollAt(DateTimeOffset since, DateTimeOffset now, TimeSpan minInterval)
    {
        var elapsed = now - since;
        var interval = elapsed < FastPhase ? FastInterval : SlowInterval;
        var next = now + (interval < minInterval ? minInterval : interval);
        var deadline = since + Ceiling;
        return next > deadline ? (now >= deadline ? now : deadline) : next;
    }

    public static bool IsTimedOut(DateTimeOffset since, DateTimeOffset now) => now - since >= Ceiling;

    /// <summary>
    /// After a poll that saw <paramref name="observed"/>: a terminal state finishes (even at the ceiling), otherwise wait
    /// for the next poll unless the 30 minutes are over.
    /// </summary>
    public static DeploymentPollDecision Decide(DateTimeOffset since, DateTimeOffset now, DeploymentStatus observed) =>
        Decide(since, now, observed, TimeSpan.Zero);

    /// <summary>§4.20: the poll interval floor of a platform (Railway 20 s; others none).</summary>
    public static TimeSpan MinInterval(DeploymentProvider platform) =>
        platform == DeploymentProvider.Railway ? RailwayRules.MinPollInterval : TimeSpan.Zero;

    /// <summary>As <see cref="Decide(DateTimeOffset, DateTimeOffset, DeploymentStatus)"/>, polling no sooner than <paramref name="minInterval"/>.</summary>
    public static DeploymentPollDecision Decide(DateTimeOffset since, DateTimeOffset now, DeploymentStatus observed, TimeSpan minInterval)
    {
        if (DeploymentStates.IsTerminal(observed))
        {
            return new DeploymentPollDecision(DeploymentPollDecisionKind.Finished, null);
        }

        return IsTimedOut(since, now)
            ? new DeploymentPollDecision(DeploymentPollDecisionKind.TimedOut, null)
            : new DeploymentPollDecision(DeploymentPollDecisionKind.Wait, NextPollAt(since, now, minInterval));
    }

    /// <summary>The message recorded with <c>DEPLOYMENT_TIMEOUT</c>.</summary>
    public const string TimeoutMessage = "The deployment didn't finish within 30 minutes; AIDE stopped polling it.";

    /// <summary>The message recorded on an older preview whose polling a newer one replaced (§4.15).</summary>
    public const string SupersededMessage = "A newer preview of this branch replaced it; AIDE stopped polling this one.";
}

/// <summary>
/// FR-91, §4.15: the release's production deployment is the one Vercel auto-created for the merge commit (production
/// auto-deploys from the default branch stay on, FR-89) or, when Vercel hasn't made one shortly after the merge, one AIDE
/// creates. Pure.
/// </summary>
public static class ProductionDeploymentRules
{
    /// <summary>How long after recording the deployment AIDE looks for Vercel's own deployment of the SHA before creating one.</summary>
    public static readonly TimeSpan AdoptWindow = TimeSpan.FromSeconds(30);

    /// <summary>How often Vercel's deployments are looked at again during <see cref="AdoptWindow"/>.</summary>
    public static readonly TimeSpan AdoptRecheck = TimeSpan.FromSeconds(10);

    /// <summary>
    /// With no Vercel deployment of the SHA found at <paramref name="now"/>: wait (the time to look again) while the window is
    /// open, else <c>null</c> (create one).
    /// </summary>
    public static DateTimeOffset? LookAgainAt(DateTimeOffset recordedAt, DateTimeOffset now) => LookAgainAt(recordedAt, now, TimeSpan.Zero);

    /// <summary>
    /// As <see cref="LookAgainAt(DateTimeOffset, DateTimeOffset)"/>, never sooner than <paramref name="minInterval"/> (§4.20
    /// Railway: 20 s). A floor that overshoots the window's close looks once more after it (then creates), never sooner.
    /// </summary>
    public static DateTimeOffset? LookAgainAt(DateTimeOffset recordedAt, DateTimeOffset now, TimeSpan minInterval)
    {
        var closes = recordedAt + AdoptWindow;
        if (now >= closes)
        {
            return null;
        }

        var next = now + (AdoptRecheck < minInterval ? minInterval : AdoptRecheck);
        return next <= closes ? next : minInterval > AdoptRecheck ? next : closes;
    }

    /// <summary>
    /// Only the automatic deployment of a release adopts (redeploys always build anew), and only on a platform that deploys the default branch by itself (§4.20; FR-149): the Fake
    /// provider has no Git integration that deploys by itself.
    /// </summary>
    public static bool Adopts(DeploymentEnvironment target, DeploymentTrigger trigger, DeploymentProvider provider) =>
        target == DeploymentEnvironment.Production && trigger == DeploymentTrigger.Auto && DeploymentPlatforms.AutoDeploysDefaultBranch(provider);
}

/// <summary>Whether review waits for the preview, or goes on (and whether it gave up waiting).</summary>
public sealed record PreviewWaitDecision(bool Wait, DateTimeOffset? RecheckAt, DateTimeOffset? WaitingSince, int? WaitedSeconds, bool TimedOut);

/// <summary>
/// FR-90, §4.15 review waiting: review waits for the latest preview of the PR head to be <c>ready</c> or <c>error</c> (or
/// canceled) before scoring, for up to 20 minutes; without a preview (Vercel not configured) it doesn't wait. Waiting
/// counts toward nothing: the job re-queues itself, never a failed attempt. <c>ERROR</c> is a critical
/// <c>architecture</c> deterministic finding with the build-log tail as evidence. Pure.
/// </summary>
public static class PreviewReviewRules
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(20);

    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>The finding text (FR-90 criterion).</summary>
    public const string FailedMessage = "Vercel preview build failed";

    /// <summary>The longest build-log excerpt used as finding evidence (the end of the log).</summary>
    public const int EvidenceMaxLength = 4000;

    /// <summary>
    /// <paramref name="status"/> is the preview's state (<c>null</c>: no preview, no wait). Terminal: proceed
    /// (<c>waitedSeconds</c> = how long review waited, <c>null</c> when it didn't). Otherwise wait — from now when not
    /// waiting yet — until 20 minutes have passed since waiting started; then proceed with <c>timedOut</c>.
    /// </summary>
    public static PreviewWaitDecision Decide(DeploymentStatus? status, DateTimeOffset? waitingSince, DateTimeOffset now)
    {
        var since = waitingSince ?? now;
        var waited = waitingSince is null ? (int?)null : (int)Math.Max(0, Math.Round((now - since).TotalSeconds));
        if (status is not { } state || DeploymentStates.IsTerminal(state))
        {
            return new PreviewWaitDecision(false, null, null, status is null ? null : waited, false);
        }

        return now - since >= WaitLimit
            ? new PreviewWaitDecision(false, null, null, waited, true)
            : new PreviewWaitDecision(true, now + RecheckInterval, since, waited, false);
    }

    /// <summary>FR-90: a failed preview build is one critical <c>architecture</c> deterministic finding; otherwise none.</summary>
    public static IReadOnlyList<ReviewFinding> Findings(DeploymentStatus? status, string? buildLogTail, string? url)
    {
        if (status != DeploymentStatus.Error)
        {
            return [];
        }

        var evidence = string.IsNullOrWhiteSpace(buildLogTail)
            ? url ?? "The preview deployment failed without a build log."
            : buildLogTail.Length <= EvidenceMaxLength ? buildLogTail : buildLogTail[^EvidenceMaxLength..];
        return [new ReviewFinding(ReviewCategory.Architecture, Severity.Critical, FailedMessage, Source: FindingSource.Deterministic, Evidence: evidence)];
    }
}

/// <summary>A failure as a deployment platform described it (classified for the FR-144 error codes, §4.20).</summary>
public enum DeploymentFailure
{
    /// <summary>The platform couldn't be reached, timed out, or answered 5xx or something unreadable.</summary>
    Upstream = 0,

    /// <summary>The token was rejected (401, or 403 flagged <c>invalidToken</c>): the connection becomes <c>invalid</c>.</summary>
    CredentialRejected = 1,

    /// <summary>The token may not do this (another 403).</summary>
    Forbidden = 2,

    NotFound = 3,

    /// <summary>FR-144: the platform's Git integration can't access the repository (<c>PLATFORM_GIT_NOT_CONNECTED</c>).</summary>
    GitNotConnected = 4,

    /// <summary>FR-144: the plan doesn't allow it, e.g. Vercel Hobby with an organisation repository (<c>PLATFORM_PLAN_LIMIT</c>).</summary>
    PlanLimit = 5,

    /// <summary>The platform's rate limit; retry after its reset.</summary>
    RateLimited = 6,

    /// <summary>Any other refusal (400, 409, 422 …).</summary>
    Rejected = 7,

    /// <summary>FR-143: the credentials lack a permission the operation needs, e.g. the GitHub App's Pages permission
    /// (<c>PLATFORM_PERMISSION_MISSING</c>).</summary>
    PermissionMissing = 8,

    /// <summary>§4.20: AIDE can't call this provider for real yet (only its Fake mode works); <c>PLATFORM_ERROR</c>.</summary>
    NotSupported = 9,
}

/// <summary>
/// FR-87, FR-88: classifying Vercel's errors (<c>{"error": {"code", "message", "invalidToken"?, "link"?}}</c>). Vercel
/// doesn't document stable codes for "GitHub not connected" or plan limits, so the codes and messages it uses today are
/// matched; anything else keeps its HTTP meaning. Pure.
/// </summary>
public static class VercelErrorRules
{
    /// <summary>Vercel's GitHub App installation page (used when Vercel's error carries no link).</summary>
    public const string DefaultGitHubInstallUrl = "https://github.com/apps/vercel/installations/new";

    private static readonly string[] GitNotConnectedCodes =
    [
        "repo_not_found", "missing_repository", "git_repository_not_found", "github_app_not_installed", "missing_git_integration", "incorrect_git_source_info",
    ];

    private static readonly string[] GitNotConnectedPhrases =
    [
        "install the github integration", "github integration", "install the github app", "github app", "repository not found", "could not access the repository",
        "don't have access to the repository", "do not have access to the repository",
    ];

    public static DeploymentFailure Classify(int? status, string? code, string? message, bool invalidToken)
    {
        if (status == 429)
        {
            return DeploymentFailure.RateLimited;
        }

        if (status == 401 || invalidToken)
        {
            return DeploymentFailure.CredentialRejected;
        }

        if (status is null or >= 500)
        {
            return DeploymentFailure.Upstream;
        }

        var lowerCode = code?.Trim().ToLowerInvariant() ?? string.Empty;
        var lowerMessage = message?.ToLowerInvariant() ?? string.Empty;
        if (lowerMessage.Contains("hobby", StringComparison.Ordinal)
            && (lowerMessage.Contains("organization", StringComparison.Ordinal) || lowerMessage.Contains("organisation", StringComparison.Ordinal) || lowerMessage.Contains(" org", StringComparison.Ordinal)))
        {
            return DeploymentFailure.PlanLimit;
        }

        if (lowerCode is "plan_limit" or "plan_limit_reached")
        {
            return DeploymentFailure.PlanLimit;
        }

        if (GitNotConnectedCodes.Contains(lowerCode) || GitNotConnectedPhrases.Any(p => lowerMessage.Contains(p, StringComparison.Ordinal)))
        {
            return DeploymentFailure.GitNotConnected;
        }

        return status switch
        {
            403 => DeploymentFailure.Forbidden,
            404 => DeploymentFailure.NotFound,
            _ => DeploymentFailure.Rejected,
        };
    }

    /// <summary>The API error code for an endpoint answering a platform failure (<c>UPSTREAM_ERROR</c> unless specific).</summary>
    public static string ErrorCodeFor(DeploymentFailure failure) => failure switch
    {
        DeploymentFailure.GitNotConnected => ErrorCodes.PlatformGitNotConnected,
        DeploymentFailure.PlanLimit => ErrorCodes.PlatformPlanLimit,
        DeploymentFailure.PermissionMissing => ErrorCodes.PlatformPermissionMissing,
        DeploymentFailure.NotSupported => ErrorCodes.PlatformError,
        DeploymentFailure.CredentialRejected => ErrorCodes.CredentialInvalid,
        DeploymentFailure.NotFound => ErrorCodes.NotFound,
        _ => ErrorCodes.UpstreamError,
    };
}

/// <summary>One write of a project env var to Vercel for one target: create (no id yet) or update an existing Vercel var.</summary>
public sealed record EnvTargetWrite(DeploymentEnvironment Target, string? ExistingVercelId);

/// <summary>One delete of a Vercel env var (a target the AIDE var no longer has).</summary>
public sealed record EnvTargetDelete(DeploymentEnvironment Target, string VercelId);

/// <summary>What pushing an env var to Vercel does: write each of its targets, delete the Vercel vars of dropped targets.</summary>
public sealed record EnvSyncPlan(IReadOnlyList<EnvTargetWrite> Writes, IReadOnlyList<EnvTargetDelete> Deletes);

/// <summary>
/// §4.15 env sync: an AIDE env var is one Vercel env var per target, so upserts and deletes are by <c>(key, target)</c>;
/// Vercel's ids are kept per target (<c>deployment_env_vars.vercel_env_ids</c>). Vercel allows 120 env writes a minute:
/// writes are counted in a sliding minute and creates are sent in batches. Pure.
/// </summary>
public static class EnvSyncRules
{
    /// <summary>Vercel's limit on env var writes per minute (§4.15).</summary>
    public const int WritesPerMinute = 120;

    /// <summary>The most env vars created in one batched request.</summary>
    public const int BatchSize = 50;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The plan for pushing a var with <paramref name="targets"/> whose Vercel ids so far are <paramref name="vercelIds"/>.</summary>
    public static EnvSyncPlan Plan(IReadOnlyCollection<DeploymentEnvironment> targets, IReadOnlyDictionary<string, string>? vercelIds)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var ids = vercelIds ?? new Dictionary<string, string>();
        var writes = targets.Distinct().OrderBy(t => t)
            .Select(t => new EnvTargetWrite(t, ids.TryGetValue(t.ToWire(), out var id) && !string.IsNullOrEmpty(id) ? id : null))
            .ToList();
        var deletes = ids
            .Where(pair => !string.IsNullOrEmpty(pair.Value) && (!EnumStrings.TryParse<DeploymentEnvironment>(pair.Key, out var t) || !targets.Contains(t)))
            .Select(pair => new EnvTargetDelete(EnumStrings.TryParse<DeploymentEnvironment>(pair.Key, out var t) ? t : DeploymentEnvironment.Preview, pair.Value))
            .OrderBy(d => d.Target)
            .ToList();
        return new EnvSyncPlan(writes, deletes);
    }

    /// <summary>Every Vercel id of a var, for deleting it in Vercel.</summary>
    public static IReadOnlyList<string> AllIds(IReadOnlyDictionary<string, string>? vercelIds) =>
        vercelIds is null ? [] : [.. vercelIds.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal)];

    /// <summary>The Vercel env type: <c>sensitive</c> (write-only in Vercel) or <c>encrypted</c>.</summary>
    public static string VercelType(bool sensitive) => sensitive ? "sensitive" : "encrypted";

    /// <summary>
    /// Sliding-minute budget: with writes at <paramref name="recent"/>, whether <paramref name="count"/> more fit at
    /// <paramref name="now"/>, and if not, when the oldest write in the window leaves it.
    /// </summary>
    public static (bool Allowed, DateTimeOffset RetryAt) TryWrite(IReadOnlyCollection<DateTimeOffset> recent, int count, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var inWindow = recent.Where(t => now - t < Window).OrderBy(t => t).ToList();
        if (inWindow.Count + count <= WritesPerMinute)
        {
            return (true, now);
        }

        var mustLeave = Math.Min(inWindow.Count, inWindow.Count + count - WritesPerMinute);
        return (false, inWindow[mustLeave - 1] + Window);
    }

    /// <summary>Splits <paramref name="count"/> creates into batches of at most <see cref="BatchSize"/>.</summary>
    public static IReadOnlyList<int> Batches(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var batches = new List<int>();
        for (var left = count; left > 0; left -= BatchSize)
        {
            batches.Add(Math.Min(BatchSize, left));
        }

        return batches;
    }
}

/// <summary>
/// FR-90, NFR-SEC2: masking a build log before it is stored (members see the tail; review gives it to the LLM). The whole log
/// is masked first and cut to the tail afterwards, so a secret straddling the cut leaves no fragment. Env values are masked
/// from 4 characters, with their base64 and URL-encoded forms; the generic secret patterns apply on top (callers). Pure.
/// </summary>
public static class BuildLogMasking
{
    public const string Mask = "••••";

    /// <summary>Env values shorter than this are left alone (they would mask ordinary words).</summary>
    public const int MinEnvValueLength = 4;

    /// <summary><paramref name="log"/> with every form of every env value replaced by <see cref="Mask"/>.</summary>
    public static string MaskEnvValues(string log, IEnumerable<string?> envValues)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(envValues);
        var forms = envValues
            .Where(v => v is { Length: >= MinEnvValueLength })
            .SelectMany(v => Forms(v!))
            .Where(f => f.Length >= MinEnvValueLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(f => f.Length);
        var result = log;
        foreach (var form in forms)
        {
            result = result.Replace(form, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>A value as it may appear in a log: plain, base64 (padded and unpadded) and URL-encoded.</summary>
    public static IEnumerable<string> Forms(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        yield return value;
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        yield return base64;
        yield return base64.TrimEnd('=');
        yield return Uri.EscapeDataString(value);
        yield return System.Net.WebUtility.UrlEncode(value);
    }
}

/// <summary>Whether the Fake deployment provider may run in this environment (§9).</summary>
public enum FakeProviderDecision
{
    /// <summary>Not configured (Vercel is used).</summary>
    NotUsed = 0,

    /// <summary>Development or Testing: allowed silently.</summary>
    Allowed = 1,

    /// <summary>Elsewhere with <c>Deployments:AllowFake=true</c> (E2E on Kubernetes): allowed, with a warning every boot.</summary>
    AllowedWithWarning = 2,

    /// <summary>Elsewhere without the opt-in: startup fails.</summary>
    Refused = 3,
}

/// <summary>§9: <c>Deployments:Provider=Fake</c> is for Development, Testing and explicitly opted-in E2E only. Pure.</summary>
public static class FakeProviderPolicy
{
    public static FakeProviderDecision Decide(bool useFake, string environmentName, bool allowFake)
    {
        if (!useFake)
        {
            return FakeProviderDecision.NotUsed;
        }

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return FakeProviderDecision.Allowed;
        }

        return allowFake ? FakeProviderDecision.AllowedWithWarning : FakeProviderDecision.Refused;
    }
}

/// <summary>
/// §4.15, §9: the deterministic Fake deployment provider (<c>Deployments:Provider=Fake</c>, Development and E2E): <c>READY</c>
/// after 2 polls at <c>https://fake-&lt;sha8&gt;.vercel.test</c>; <c>ERROR</c> when the deployed commit contains
/// <see cref="FailBuildMarker"/>. Pure.
/// </summary>
public static class FakeDeploymentRules
{
    /// <summary>A file whose presence on the deployed branch makes the Fake build fail.</summary>
    public const string FailBuildMarker = ".aide-fake/fail-build";

    /// <summary>Polls before the Fake deployment finishes.</summary>
    public const int PollsToFinish = 2;

    /// <summary>The Fake deployment URL of a commit.</summary>
    public static string Url(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        var short8 = sha.Length >= 8 ? sha[..8] : sha;
        return string.Create(CultureInfo.InvariantCulture, $"https://fake-{short8.ToLowerInvariant()}.vercel.test");
    }

    /// <summary>A file whose presence on the deployed branch makes a Fake backend pre-deploy (migration) fail (§4.20).</summary>
    public const string FailMigrationMarker = ".aide-fake/fail-migration";

    /// <summary>§4.20: the Fake URL of a component's deployment: <c>https://fake-&lt;sha8&gt;-&lt;component&gt;.&lt;provider&gt;.test</c>.</summary>
    public static string Url(string sha, DeploymentComponent component, DeploymentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(sha);
        var short8 = (sha.Length >= 8 ? sha[..8] : sha).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"https://fake-{short8}-{component.ToWire()}.{DeploymentPlatforms.HostLabel(provider)}.test");
    }

    /// <summary>Whether a backend commit with <paramref name="paths"/> fails its Fake pre-deploy migration.</summary>
    public static bool FailsMigration(IEnumerable<string>? paths) =>
        paths?.Any(p => string.Equals(p.Replace('\\', '/').TrimStart('/'), FailMigrationMarker, StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>The Fake log of a failed backend pre-deploy.</summary>
    public const string FailedMigrationLog =
        "==> Running pre-deploy command\nApplying migration 0002_add_items...\nerror: relation \"items\" already exists\n"
        + "The Fake deployment provider fails backend pre-deploys of commits containing .aide-fake/fail-migration.\n==> Pre-deploy command failed (exit 1)";

    /// <summary>Whether a commit with <paramref name="paths"/> fails the Fake build.</summary>
    public static bool FailsBuild(IEnumerable<string>? paths) =>
        paths?.Any(p => string.Equals(p.Replace('\\', '/').TrimStart('/'), FailBuildMarker, StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>The Fake state after <paramref name="poll"/> polls (1-based): building, then ready or error.</summary>
    public static DeploymentStatus StateAfter(int poll, bool fails) =>
        poll < PollsToFinish ? DeploymentStatus.Building : fails ? DeploymentStatus.Error : DeploymentStatus.Ready;

    /// <summary>The Fake build log of a failed build (ends like a Next.js type error).</summary>
    public const string FailedBuildLog =
        "Running \"npm run build\"\n> next build\n   Creating an optimized production build ...\nFailed to compile.\n\n"
        + "./app/page.tsx:1:7\nType error: Type 'number' is not assignable to type 'string'.\n\n"
        + "The Fake deployment provider fails builds of commits containing .aide-fake/fail-build.\nError: Command \"npm run build\" exited with 1";
}
