using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Infrastructure.GitHub;

/// <summary>
/// The Tevrin GitHub App in use (FR-80), with its secrets decrypted: from <c>GitHub:App:*</c> configuration (which takes
/// precedence) or the row registered through the manifest flow. Never serialised or logged; the private key never leaves
/// the server (§8). <see cref="Permissions"/>, owner and HTML URL are <c>null</c> for a configured App until read from
/// GitHub (<c>GET /app</c>).
/// </summary>
public sealed record GitHubAppCredentials(
    long AppId,
    string Slug,
    string ClientId,
    string ClientSecret,
    string PrivateKeyPem,
    GitHubAppSource Source,
    string? OwnerLogin,
    GitHubOwnerType? OwnerType,
    string? HtmlUrl,
    IReadOnlyDictionary<string, string>? Permissions)
{
    public string InstallUrl => GitHubRules.InstallUrl(Slug);

    /// <summary>Redacted: the record's secrets never reach a log or an exception message.</summary>
    public override string ToString() => $"GitHubApp {{ AppId = {AppId}, Slug = {Slug}, Source = {Source} }}";
}

/// <summary>Reads and registers the Tevrin GitHub App (FR-80).</summary>
public interface IGitHubAppStore
{
    /// <summary>The App in use, or <c>null</c> when none is registered or configured.</summary>
    Task<GitHubAppCredentials?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// FR-80: stores the App created by the manifest flow with its secrets encrypted (per-purpose protectors). Returns
    /// <c>false</c> (nothing saved) when an App row already exists.
    /// </summary>
    Task<bool> SaveAsync(GitHubManifestConversion conversion, CancellationToken cancellationToken);
}

internal sealed partial class GitHubAppStore(
    AideDbContext db,
    IOptions<GitHubAppOptions> options,
    ISecretProtectors protectors,
    GitHubApi api,
    GitHubAppInfoCache appInfo,
    TimeProvider time,
    ILogger<GitHubAppStore> logger) : IGitHubAppStore
{
    private bool _loaded;
    private GitHubAppCredentials? _app;

    public async Task<GitHubAppCredentials?> GetAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return _app;
        }

        var configured = options.Value;
        if (configured.IsComplete)
        {
            _app = new GitHubAppCredentials(
                configured.AppId!.Value, configured.Slug!.Trim(), configured.ClientId!.Trim(), configured.ClientSecret!, configured.PrivateKeyPem!,
                GitHubAppSource.Configuration, null, null, null, null);
        }
        else
        {
            var row = await db.GitHubApps.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (row is not null)
            {
                if (protectors.ForPurpose(SecretPurposes.GitHubAppClientSecret).TryUnprotect(row.ClientSecretEncrypted, out var secret)
                    && protectors.ForPurpose(SecretPurposes.GitHubAppPrivateKey).TryUnprotect(row.PrivateKeyEncrypted, out var pem))
                {
                    _app = new GitHubAppCredentials(
                        row.AppId, row.Slug, row.ClientId, secret, pem, GitHubAppSource.Database, row.OwnerLogin, row.OwnerType, row.HtmlUrl, row.Permissions);
                }
                else
                {
                    LogUnreadable(logger, row.AppId);
                }
            }
        }

        if (_app is not null)
        {
            _app = await WithCurrentSlugAsync(_app, cancellationToken);
        }

        _loaded = true;
        return _app;
    }

    /// <summary>
    /// FR-228: the App with GitHub's current slug, so install links, App pages and the bot identity follow a rename. A
    /// registered App's row is updated (slug and page) once; a configured App keeps its configuration, and the cache logs
    /// a warning to update <c>GitHub__App__Slug</c>. When GitHub can't be asked, the stored slug is used.
    /// </summary>
    private async Task<GitHubAppCredentials> WithCurrentSlugAsync(GitHubAppCredentials app, CancellationToken cancellationToken)
    {
        var current = await appInfo.GetCurrentAsync(app, api, cancellationToken);
        var slug = GitHubRules.CurrentSlug(app.AppId, app.Slug, current?.Id, current?.Slug);
        if (current is null || string.Equals(slug, app.Slug, StringComparison.Ordinal))
        {
            return app;
        }

        if (app.Source != GitHubAppSource.Database)
        {
            return app with { Slug = slug };
        }

        var htmlUrl = current.HtmlUrl ?? GitHubRules.AppHtmlUrl(slug);
        if (db.Database.CurrentTransaction is not null)
        {
            // Not inside a caller's transaction (its locks, or an aborted transaction on failure): a later request updates the row.
            return app with { Slug = slug, HtmlUrl = htmlUrl };
        }

        try
        {
            await db.GitHubApps
                .Where(a => a.AppId == app.AppId && a.Slug == app.Slug)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Slug, slug).SetProperty(a => a.HtmlUrl, htmlUrl), cancellationToken);
            LogSlugUpdated(logger, app.AppId, app.Slug, slug);
        }
        catch (DbException ex)
        {
            // Still use the current slug for this request; the row is updated on a later one.
            LogSlugUpdateFailed(logger, ex, app.AppId);
        }

        return app with { Slug = slug, HtmlUrl = htmlUrl };
    }

    public async Task<bool> SaveAsync(GitHubManifestConversion conversion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        if (await db.GitHubApps.AnyAsync(cancellationToken))
        {
            return false;
        }

        var app = conversion.App;
        db.GitHubApps.Add(new GitHubApp
        {
            AppId = app.Id,
            Slug = app.Slug,
            OwnerLogin = app.OwnerLogin ?? string.Empty,
            OwnerType = app.OwnerType,
            ClientId = conversion.ClientId,
            ClientSecretEncrypted = protectors.ForPurpose(SecretPurposes.GitHubAppClientSecret).Protect(conversion.ClientSecret),
            PrivateKeyEncrypted = protectors.ForPurpose(SecretPurposes.GitHubAppPrivateKey).Protect(conversion.PrivateKeyPem),
            Permissions = new Dictionary<string, string>(app.Permissions, StringComparer.Ordinal),
            HtmlUrl = app.HtmlUrl,
            CreatedAt = time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent completion registered an App first.
            return false;
        }

        _loaded = false;
        LogRegistered(logger, app.Id, app.Slug);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The registered GitHub App {AppId} can't be decrypted (Data Protection keys changed?); it is ignored")]
    private static partial void LogUnreadable(ILogger logger, long appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub App {AppId} renamed on GitHub: stored slug {StoredSlug} updated to {Slug}")]
    private static partial void LogSlugUpdated(ILogger logger, long appId, string storedSlug, string slug);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub App {AppId}: the stored slug couldn't be updated; it is retried on a later request")]
    private static partial void LogSlugUpdateFailed(ILogger logger, Exception exception, long appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub App {AppId} ({Slug}) registered through the manifest flow")]
    private static partial void LogRegistered(ILogger logger, long appId, string slug);
}

/// <summary>
/// FR-82, §4.14: short-lived installation tokens, minted from the App JWT and scoped to one repository
/// (<c>repository_ids</c>), cached in memory for at most 55 minutes per (App, installation, repository). Never written to
/// disk or logged.
/// </summary>
public interface IGitHubInstallationTokens
{
    /// <summary>
    /// An installation token for <paramref name="installationId"/>, scoped to <paramref name="repoId"/> (or the whole
    /// installation when <c>null</c>). Throws <see cref="GitHubApiException"/> (e.g. 404: the App was uninstalled or the
    /// repository removed from it) or <see cref="GitHubRateLimitedException"/>.
    /// </summary>
    Task<GitHubInstallationToken> GetAsync(GitHubAppCredentials app, long installationId, long? repoId, CancellationToken cancellationToken);
}

/// <summary>The process-wide cache behind <see cref="IGitHubInstallationTokens"/> (single API replica, §9).</summary>
public sealed class GitHubInstallationTokenCache
{
    private readonly ConcurrentDictionary<(long AppId, long InstallationId, long RepoId), (GitHubInstallationToken Token, DateTimeOffset UsableUntil)> _tokens = new();

    internal bool TryGet((long, long, long) key, DateTimeOffset now, out GitHubInstallationToken token)
    {
        if (_tokens.TryGetValue(key, out var cached) && cached.UsableUntil > now)
        {
            token = cached.Token;
            return true;
        }

        token = null!;
        return false;
    }

    internal void Set((long, long, long) key, GitHubInstallationToken token, DateTimeOffset usableUntil) => _tokens[key] = (token, usableUntil);

    /// <summary>Forgets every token of an installation (e.g. after GitHub refused one).</summary>
    public void Forget(long installationId)
    {
        foreach (var key in _tokens.Keys.Where(k => k.InstallationId == installationId))
        {
            _tokens.TryRemove(key, out _);
        }
    }
}

internal sealed partial class GitHubInstallationTokens(
    GitHubApi api,
    GitHubInstallationTokenCache cache,
    TimeProvider time,
    ILogger<GitHubInstallationTokens> logger) : IGitHubInstallationTokens
{
    public async Task<GitHubInstallationToken> GetAsync(GitHubAppCredentials app, long installationId, long? repoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        var key = (app.AppId, installationId, repoId ?? 0);
        var now = time.GetUtcNow();
        if (cache.TryGet(key, now, out var cached))
        {
            return cached;
        }

        var jwt = GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, now);
        var minted = await api.CreateInstallationTokenAsync(jwt, installationId, repoId is { } id ? [id] : null, cancellationToken);
        cache.Set(key, minted, GitHubRules.InstallationTokenUsableUntil(now, minted.ExpiresAt));
        LogMinted(logger, installationId, repoId);
        return minted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Minted a GitHub installation token for installation {InstallationId} (repository {RepoId})")]
    private static partial void LogMinted(ILogger logger, long installationId, long? repoId);
}

/// <summary>Looks the App bot user up once per slug (<c>GET /users/&lt;slug&gt;[bot]</c>) and caches it.</summary>
public sealed class GitHubBotIdentities(GitHubApi api)
{
    private readonly ConcurrentDictionary<string, GitCommitIdentity> _cache = new(StringComparer.Ordinal);

    public async Task<GitCommitIdentity> GetAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        if (_cache.TryGetValue(slug, out var identity))
        {
            return identity;
        }

        var login = GitHubRules.BotLogin(slug);
        GitHubUser? bot = null;
        try
        {
            bot = await api.FindUserAsync(login, cancellationToken);
        }
        catch (GitHubApiException)
        {
            // Fall back to the login without the numeric id this time; looked up again next time.
        }

        if (bot is null)
        {
            return new GitCommitIdentity(login, $"{login}@users.noreply.github.com");
        }

        identity = new GitCommitIdentity(login, GitHubRules.BotEmail(bot.Id, slug));
        _cache[slug] = identity;
        return identity;
    }
}

/// <summary>
/// FR-80: what GitHub says about the App (<c>GET /app</c>: slug, owner, page, granted permissions), for an App configured
/// through <c>GitHub:App:*</c> (a registered App stores it), and FR-228: the App's current slug for either source. One
/// cached answer serves both, kept 10 minutes; a failed FR-228 lookup is remembered for a minute so an unreachable GitHub
/// isn't asked on every request.
/// </summary>
public sealed partial class GitHubAppInfoCache(TimeProvider time, ILogger<GitHubAppInfoCache> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private volatile Entry? _cached;
    private (long AppId, Task<GitHubAppInfo> Task)? _inflight;

    /// <summary>The App's details; the registered row's for a database App; <c>null</c> when GitHub can't be asked.</summary>
    public async Task<GitHubAppInfo?> GetAsync(GitHubAppCredentials app, GitHubApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(api);
        if (app.Permissions is { } permissions)
        {
            return new GitHubAppInfo(app.AppId, app.Slug, app.OwnerLogin, app.OwnerType ?? GitHubOwnerType.User, app.HtmlUrl, permissions);
        }

        var now = time.GetUtcNow();
        if (_cached is { Info: { } info } cached && cached.AppId == app.AppId && now < cached.UsableUntil)
        {
            return info;
        }

        try
        {
            return await FetchAsync(app, api, now, cancellationToken);
        }
        catch (GitHubApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-228: GitHub's current answer for the App (<c>GET /app</c>), whatever its source, or <c>null</c> when GitHub can't
    /// be asked (unreachable, rate limited, refused, over 5 seconds, unreadable key). Never throws, except when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<GitHubAppInfo?> GetCurrentAsync(GitHubAppCredentials app, GitHubApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(api);
        var now = time.GetUtcNow();
        var seen = _cached;
        if (seen is { } cached && cached.AppId == app.AppId && now < cached.UsableUntil)
        {
            return cached.Info;
        }

        try
        {
            return await FetchAsync(app, api, now, cancellationToken);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubRateLimitedException or CryptographicException or ArgumentException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Only over what this lookup saw: a newer answer stored meanwhile by another request wins.
            Interlocked.CompareExchange(ref _cached, new Entry(app.AppId, null, now + RetryAfter), seen);
            LogCurrentUnavailable(logger, ex, app.AppId);
            return null;
        }
    }

    /// <summary>
    /// One <c>GET /app</c> at a time per App: concurrent callers wait on the same lookup (each can stop waiting on its own
    /// token). Over 5 seconds is a <see cref="GitHubApiException"/>, like the client's own timeout.
    /// </summary>
    private Task<GitHubAppInfo> FetchAsync(GitHubAppCredentials app, GitHubApi api, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inflight is not { Task.IsCompleted: false } inflight || inflight.AppId != app.AppId)
            {
                inflight = (app.AppId, FetchOnceAsync(app, api, now));
                _inflight = inflight;
            }

            return inflight.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task<GitHubAppInfo> FetchOnceAsync(GitHubAppCredentials app, GitHubApi api, DateTimeOffset now)
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        GitHubAppInfo info;
        try
        {
            info = await api.GetAppAsync(GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, now), timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub didn't answer GET /app within 5 seconds.");
        }

        _cached = new Entry(app.AppId, info, now + Lifetime);
        if (info.Id == app.AppId && !string.Equals(info.Slug, app.Slug, StringComparison.Ordinal))
        {
            LogSlugChanged(logger, app.AppId, app.Slug, info.Slug, app.Source);
        }

        return info;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub App {AppId} is now {Slug} on GitHub (stored as {StoredSlug}, source {Source}); the current slug is used. For a configured App, set GitHub__App__Slug to it")]
    private static partial void LogSlugChanged(ILogger logger, long appId, string storedSlug, string slug, GitHubAppSource source);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub App {AppId}: GitHub couldn't be asked for the current slug; the stored slug is used for a minute")]
    private static partial void LogCurrentUnavailable(ILogger logger, Exception exception, long appId);

    private sealed record Entry(long AppId, GitHubAppInfo? Info, DateTimeOffset UsableUntil);
}
