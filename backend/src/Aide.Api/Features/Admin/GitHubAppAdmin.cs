using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aide.Api.Features.Notifications;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Infrastructure.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Admin;

/// <summary>
/// §8: the manifest flow's <c>state</c>: signed and time-limited with Data Protection (10 minutes) and single-use — its
/// nonce is held in memory, bound to the Admin who started the flow, and removed when the callback uses it. A state
/// whose nonce isn't held (used, expired, or issued before a restart) is refused, so the Admin starts again.
/// </summary>
public sealed class GitHubManifestStates(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string Purpose = "Aide.GitHub.ManifestState.v1";

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, (Guid UserId, DateTimeOffset ExpiresAt)> _pending = new(StringComparer.Ordinal);

    /// <summary>A new state for <paramref name="userId"/> (URL-safe, under 200 characters).</summary>
    public string Issue(Guid userId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        var nonce = RandomNumberGenerator.GetBytes(16);
        _pending[Convert.ToHexString(nonce)] = (userId, now + Lifetime);
        return Infrastructure.GitHub.GitHubAppJwt.Base64Url(_protector.Protect(nonce, now + Lifetime));
    }

    /// <summary>Uses <paramref name="state"/> once: valid signature, not expired, issued to <paramref name="userId"/>.</summary>
    public bool TryConsume(string? state, Guid userId)
    {
        if (string.IsNullOrEmpty(state))
        {
            return false;
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
            return false;
        }

        var key = Convert.ToHexString(nonce);
        return _pending.TryGetValue(key, out var pending)
            && pending.UserId == userId
            && pending.ExpiresAt > time.GetUtcNow()
            && _pending.TryRemove(key, out _);
    }
}

/// <summary>
/// FR-80: Admin → Integrations → GitHub: the App's status, the manifest (§4.14) and its completion (code exchange and
/// encrypted storage). The private key and client secret are never returned.
/// </summary>
public sealed partial class GitHubAppAdmin(
    IGitHubAppStore apps,
    GitHubApi api,
    GitHubAppInfoCache appInfo,
    GitHubManifestStates states,
    IOptions<AppOptions> appOptions,
    ILogger<GitHubAppAdmin> logger)
{
    public const string AdminPagePath = "/admin/integrations";

    /// <summary>FR-80: the App's status, source, owner, install URL and granted permissions.</summary>
    public async Task<AdminGitHubAppDto> StatusAsync(CancellationToken cancellationToken)
    {
        var app = await apps.GetAsync(cancellationToken);
        if (app is null)
        {
            return new AdminGitHubAppDto(GitHubAppStatus.NotConfigured, null, null, null, null, null, null, null, new Dictionary<string, string>(), false, GitHubPagesAccess.Unknown);
        }

        var info = await appInfo.GetAsync(app, api, cancellationToken);
        var permissions = info?.Permissions ?? new Dictionary<string, string>();
        return new AdminGitHubAppDto(
            GitHubAppStatus.Configured,
            app.Source,
            app.AppId,
            app.Slug,
            info?.OwnerLogin ?? app.OwnerLogin,
            info?.OwnerType ?? app.OwnerType,
            app.InstallUrl,
            info?.HtmlUrl ?? app.HtmlUrl ?? GitHubRules.AppHtmlUrl(app.Slug),
            permissions,
            GitHubRules.CanCreateRepositories(permissions),
            GitHubRules.PagesPermission(info?.Permissions));
    }

    /// <summary>FR-80: the manifest and where to post it; <c>CONFLICT</c> when an App is already configured.</summary>
    public async Task<Result<GitHubAppManifestResponseDto>> ManifestAsync(Guid adminId, CreateGitHubAppManifestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await apps.GetAsync(cancellationToken) is not null)
        {
            return Result.Failure<GitHubAppManifestResponseDto>(new DomainError(ErrorCodes.Conflict, "An AIDE GitHub App is already configured."));
        }

        if (request.OwnerType == GitHubOwnerType.Organization && string.IsNullOrWhiteSpace(request.Org))
        {
            return Result.Failure<GitHubAppManifestResponseDto>(
                new DomainError(ErrorCodes.Validation, "The manifest request is invalid.") { Details = ["org: Name the organisation that will own the App."] });
        }

        var baseUrl = appOptions.Value.PublicBaseUrl.TrimEnd('/');
        var host = new Uri(baseUrl).Host;
        var manifest = new GitHubAppManifestDto(
            GitHubRules.ManifestAppName(host),
            baseUrl,
            appOptions.Value.Absolute("/api/admin/integrations/github/callback"),
            [appOptions.Value.Absolute(Api.Auth.GitHubAuth.CallbackPath)],
            appOptions.Value.Absolute("/account/connections?github=installed"),
            false,
            GitHubRules.ManifestPermissions,
            [],
            false);
        var state = states.Issue(adminId);
        var postUrl = request.OwnerType == GitHubOwnerType.Organization
            ? $"https://github.com/organizations/{Uri.EscapeDataString(request.Org!.Trim())}/settings/apps/new?state={Uri.EscapeDataString(state)}"
            : $"https://github.com/settings/apps/new?state={Uri.EscapeDataString(state)}";
        LogManifestIssued(logger, request.OwnerType);
        return Result.Success(new GitHubAppManifestResponseDto(postUrl, manifest, state));
    }

    /// <summary>
    /// FR-80: GitHub's redirect after the App was created: checks the single-use state, exchanges the code and stores the
    /// App. Returns the page to redirect to (<c>?github=configured</c>, or <c>?error=GITHUB_AUTH_FAILED</c>).
    /// </summary>
    public async Task<string> CompleteAsync(Guid adminId, string? code, string? state, CancellationToken cancellationToken)
    {
        var failed = $"{AdminPagePath}?error={ErrorCodes.GitHubAuthFailed}";
        if (!states.TryConsume(state, adminId))
        {
            LogCompleteFailed(logger, "state");
            return failed;
        }

        if (string.IsNullOrEmpty(code))
        {
            LogCompleteFailed(logger, "code");
            return failed;
        }

        if (await apps.GetAsync(cancellationToken) is not null)
        {
            LogCompleteFailed(logger, "already_configured");
            return failed;
        }

        GitHubManifestConversion conversion;
        try
        {
            conversion = await api.ConvertManifestAsync(code, cancellationToken);
        }
        catch (GitHubApiException ex)
        {
            LogConversionFailed(logger, ex.Status);
            return failed;
        }
        catch (GitHubRateLimitedException)
        {
            LogCompleteFailed(logger, "rate_limited");
            return failed;
        }

        if (!await apps.SaveAsync(conversion, cancellationToken))
        {
            LogCompleteFailed(logger, "already_configured");
            return failed;
        }

        return $"{AdminPagePath}?github=configured";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub App manifest issued (owner type {OwnerType})")]
    private static partial void LogManifestIssued(ILogger logger, GitHubOwnerType ownerType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub App manifest callback refused ({Reason})")]
    private static partial void LogCompleteFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub App manifest conversion failed with HTTP {Status}")]
    private static partial void LogConversionFailed(ILogger logger, int? status);
}
