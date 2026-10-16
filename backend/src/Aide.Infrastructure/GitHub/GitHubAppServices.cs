using System.Collections.Concurrent;
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
/// The AIDE GitHub App in use (FR-80), with its secrets decrypted: from <c>GitHub:App:*</c> configuration (which takes
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

/// <summary>Reads and registers the AIDE GitHub App (FR-80).</summary>
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

        _loaded = true;
        return _app;
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
/// FR-80: what GitHub says about the App (<c>GET /app</c>: owner, page, granted permissions), for an App configured
/// through <c>GitHub:App:*</c> (a registered App stores it). Cached in memory for 10 minutes.
/// </summary>
public sealed class GitHubAppInfoCache(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private (long AppId, GitHubAppInfo Info, DateTimeOffset FetchedAt)? _cached;

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
        if (_cached is { } cached && cached.AppId == app.AppId && now - cached.FetchedAt < Lifetime)
        {
            return cached.Info;
        }

        try
        {
            var info = await api.GetAppAsync(GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, now), cancellationToken);
            _cached = (app.AppId, info, now);
            return info;
        }
        catch (GitHubApiException)
        {
            return null;
        }
    }
}
