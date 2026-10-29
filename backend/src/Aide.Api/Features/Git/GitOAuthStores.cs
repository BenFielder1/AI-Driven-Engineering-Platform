using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aide.Domain.GitHosts;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.Api.Features.Git;

/// <summary>The bounds of the in-memory OAuth flow and pending-link stores (FR-131, FR-134, §8).</summary>
public static class GitOAuthLimits
{
    /// <summary>How long a started flow (and a pending email link) is held.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most flows held at once, per host.</summary>
    public const int MaxPending = 10_000;

    /// <summary>How often expired flows are swept (amortised over starts).</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
}

/// <summary>A started Git host OAuth flow, held server-side until its callback.</summary>
public interface IGitOAuthFlow
{
    DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// §8 OAuth <c>state</c> for the Git hosts (FR-131, FR-134; the v2 manifest rule): signed and time-limited with Data
/// Protection (10 minutes), single-use (its nonce is held in memory and removed by the callback that uses it), and bound to
/// the browser that started the flow by an HttpOnly cookie holding the nonce (so a state can't be replayed from another
/// browser: no login CSRF). A state whose nonce isn't held (used, expired, or issued before a restart) is refused.
/// <para>
/// Starting is anonymous, so the held flows are bounded: at most <see cref="GitOAuthLimits.MaxPending"/> at once per host (a start beyond
/// that is refused until flows are used or expire), and expired flows are swept at most once per
/// <see cref="GitOAuthLimits.SweepInterval"/> rather than on every start. In memory: AIDE runs one API replica (M27 decision).
/// </para>
/// </summary>
public abstract class GitOAuthStateStore<TFlow>(IDataProtectionProvider dataProtection, TimeProvider time, string purpose)
    where TFlow : class, IGitOAuthFlow
{
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(purpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, TFlow> _pending = new(StringComparer.Ordinal);
    private int _count;
    private long _nextSweepTicks;

    /// <summary>How many flows are held (expired ones included until the next sweep).</summary>
    public int PendingCount => Volatile.Read(ref _count);

    protected TimeProvider Time => time;

    /// <summary>
    /// Holds the flow <paramref name="create"/> builds (given its expiry) and returns the <c>state</c> parameter and the cookie
    /// value; <c>null</c> when <see cref="GitOAuthLimits.MaxPending"/> flows are already held.
    /// </summary>
    protected (string State, string CookieValue, TFlow Flow)? IssueCore(Func<DateTimeOffset, TFlow> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        var now = time.GetUtcNow();
        SweepIfDue(now);
        if (Interlocked.Increment(ref _count) > GitOAuthLimits.MaxPending)
        {
            Interlocked.Decrement(ref _count);
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(32);
        var flow = create(now + GitOAuthLimits.Lifetime);
        var key = Convert.ToHexString(nonce);
        if (!_pending.TryAdd(key, flow))
        {
            // A 256-bit nonce collision: practically impossible; don't count it twice.
            Interlocked.Decrement(ref _count);
        }

        return (GitLabRules.Base64Url(_protector.Protect(nonce, now + GitOAuthLimits.Lifetime)), key, flow);
    }

    /// <summary>Uses <paramref name="state"/> once: valid signature, not expired, held, and matching the browser's cookie.</summary>
    public TFlow? TryConsume(string? state, string? cookieValue)
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

    /// <summary>Removes expired flows, by one caller at a time and at most once per <see cref="GitOAuthLimits.SweepInterval"/>.</summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + GitOAuthLimits.SweepInterval).UtcTicks, due) != due)
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

    private bool Remove(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TFlow flow)
    {
        if (!_pending.TryRemove(key, out flow))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        return true;
    }
}

/// <summary>A held pending email link (FR-131, FR-134, D-V4-18).</summary>
public interface IGitPendingLink
{
    Guid Id { get; }

    DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// FR-131, FR-134, D-V4-18: sign-ins whose verified email matches an existing, unlinked AIDE account, held in memory for 10
/// minutes (with the OAuth tokens, so confirming needs no second round trip to the host), referenced by an HttpOnly, Data
/// Protection–protected cookie. Only the account a link was offered to can confirm it.
/// </summary>
public abstract class GitPendingLinkStore<TLink>(IDataProtectionProvider dataProtection, TimeProvider time, string purpose)
    where TLink : class, IGitPendingLink
{
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(purpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<Guid, TLink> _pending = new();

    protected TimeProvider Time => time;

    /// <summary>Holds a link (sweeping expired ones) and returns its cookie value.</summary>
    protected string AddCore(TLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var now = time.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        _pending[link.Id] = link;
        return _protector.Protect(link.Id.ToString("N"), link.ExpiresAt);
    }

    public TLink? Find(string? cookieValue)
    {
        if (Id(cookieValue) is not { } id || !_pending.TryGetValue(id, out var link))
        {
            return null;
        }

        return link.ExpiresAt > time.GetUtcNow() ? link : null;
    }

    /// <summary>Removes the link (confirmed or cancelled); returns it if it was held.</summary>
    public TLink? Take(string? cookieValue) =>
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
