using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.Api.Features.GitLab;

/// <summary>A started GitLab OAuth flow (FR-131), held server-side until its callback. The PKCE verifier never leaves AIDE.</summary>
public sealed record GitLabOAuthFlow(Guid InstanceId, GitOAuthMode Mode, string ReturnUrl, Guid? StartedByUserId, string CodeVerifier, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"GitLabOAuthFlow {{ InstanceId = {InstanceId}, Mode = {Mode} }}";
}

/// <summary>
/// §8 OAuth <c>state</c> for GitLab (the v2 manifest rule): signed and time-limited with Data Protection (10 minutes),
/// single-use (its nonce is held in memory and removed by the callback that uses it), and bound to the browser that started
/// the flow by an HttpOnly cookie holding the nonce (so a state can't be replayed from another browser: no login CSRF). A
/// state whose nonce isn't held (used, expired, or issued before a restart) is refused.
/// <para>
/// Unlike the Admin-only manifest flow, starting is anonymous, so the held flows are bounded: at most
/// <see cref="MaxPending"/> at once (a start beyond that is refused until flows are used or expire), and expired flows are
/// swept at most once per <see cref="SweepInterval"/> rather than on every start.
/// </para>
/// </summary>
public sealed class GitLabOAuthStates(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most flows held at once.</summary>
    public const int MaxPending = 10_000;

    /// <summary>How often expired flows are swept (amortised over starts).</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>The browser-binding cookie (path <c>/api/auth/gitlab</c>).</summary>
    public const string CookieName = "aide.gitlab.oauth";

    private const string Purpose = "Aide.GitLab.OAuthState.v1";

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, GitLabOAuthFlow> _pending = new(StringComparer.Ordinal);
    private int _count;
    private long _nextSweepTicks;

    /// <summary>How many flows are held (expired ones included until the next sweep).</summary>
    public int PendingCount => Volatile.Read(ref _count);

    /// <summary>
    /// Starts a flow: returns the <c>state</c> parameter, the cookie value and a new PKCE verifier (in the flow); <c>null</c>
    /// when <see cref="MaxPending"/> flows are already held.
    /// </summary>
    public (string State, string CookieValue, GitLabOAuthFlow Flow)? Issue(Guid instanceId, GitOAuthMode mode, string returnUrl, Guid? userId)
    {
        var now = time.GetUtcNow();
        SweepIfDue(now);
        if (Interlocked.Increment(ref _count) > MaxPending)
        {
            Interlocked.Decrement(ref _count);
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(32);
        var flow = new GitLabOAuthFlow(instanceId, mode, returnUrl, userId, GitLabRules.Base64Url(RandomNumberGenerator.GetBytes(32)), now + Lifetime);
        var key = Convert.ToHexString(nonce);
        if (!_pending.TryAdd(key, flow))
        {
            // A 256-bit nonce collision: practically impossible; don't count it twice.
            Interlocked.Decrement(ref _count);
        }

        return (GitLabRules.Base64Url(_protector.Protect(nonce, now + Lifetime)), key, flow);
    }

    /// <summary>Uses <paramref name="state"/> once: valid signature, not expired, held, and matching the browser's cookie.</summary>
    public GitLabOAuthFlow? TryConsume(string? state, string? cookieValue)
    {
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookieValue) || state.Length > 400)
        {
            return null;
        }

        byte[] nonce;
        try
        {
            var padded = state.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            nonce = _protector.Unprotect(Convert.FromBase64String(padded), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }

        var key = Convert.ToHexString(nonce);
        if (!Remove(key, out var flow))
        {
            return null;
        }

        var cookieMatches = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(cookieValue));
        return cookieMatches && flow.ExpiresAt > time.GetUtcNow() ? flow : null;
    }

    /// <summary>Removes expired flows, by one caller at a time and at most once per <see cref="SweepInterval"/>.</summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepInterval).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt <= now)
            {
                Remove(key, out _);
            }
        }
    }

    private bool Remove(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out GitLabOAuthFlow flow)
    {
        if (!_pending.TryRemove(key, out flow))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        return true;
    }
}

/// <summary>
/// FR-131: a sign-in whose verified GitLab email matches an existing, unlinked AIDE account. Held in memory for 10 minutes
/// (with the OAuth tokens, so confirming needs no second GitLab round trip), referenced by an HttpOnly, Data
/// Protection–protected cookie. Only the account it was offered to can confirm it.
/// </summary>
public sealed record GitLabPendingLink(
    Guid Id,
    GitLabInstance Instance,
    Guid OfferedToUserId,
    string Email,
    GitLabUser User,
    GitLabOAuthTokens Tokens,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"GitLabPendingLink {{ Id = {Id}, Login = {User.Username} }}";
}

public sealed class GitLabPendingLinks(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const string CookieName = "aide.gitlab.link";

    private const string Purpose = "Aide.GitLab.PendingLink.v1";

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<Guid, GitLabPendingLink> _pending = new();

    /// <summary>Holds a link and returns its cookie value.</summary>
    public (string CookieValue, GitLabPendingLink Link) Add(GitLabInstance instance, Guid offeredTo, string email, GitLabUser user, GitLabOAuthTokens tokens)
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        var link = new GitLabPendingLink(Guid.CreateVersion7(), instance, offeredTo, email, user, tokens, now + Lifetime);
        _pending[link.Id] = link;
        return (_protector.Protect(link.Id.ToString("N"), now + Lifetime), link);
    }

    public GitLabPendingLink? Find(string? cookieValue)
    {
        if (Id(cookieValue) is not { } id || !_pending.TryGetValue(id, out var link))
        {
            return null;
        }

        return link.ExpiresAt > time.GetUtcNow() ? link : null;
    }

    /// <summary>Removes the link (confirmed or cancelled); returns it if it was held.</summary>
    public GitLabPendingLink? Take(string? cookieValue) =>
        Id(cookieValue) is { } id && _pending.TryRemove(id, out var link) ? link : null;

    private Guid? Id(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > 1000)
        {
            return null;
        }

        try
        {
            return Guid.TryParseExact(_protector.Unprotect(cookieValue), "N", out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
