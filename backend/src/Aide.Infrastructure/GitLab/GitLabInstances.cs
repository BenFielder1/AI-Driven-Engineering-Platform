using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// A GitLab instance as AIDE uses it (FR-130): the <c>git_host_instances</c> row with configuration
/// (<c>GitLab:Instances:*</c>) applied on top. <see cref="BaseUrl"/> is the effective base URL (<c>https://gitlab.com</c> for
/// gitlab.com, whose row stores NULL). The client secret is never on this record (<see cref="IGitLabInstances.GetClientSecret"/>).
/// </summary>
public sealed record GitLabInstance(
    Guid Id,
    string Name,
    Uri BaseUrl,
    bool IsGitLabCom,
    GitHostInstanceSource Source,
    string? ClientId,
    bool HasClientSecret,
    bool AllowInsecureHttp,
    bool Enabled,
    string? Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>OAuth (and so sign-in) needs an application id and secret.</summary>
    public bool OAuthConfigured => !string.IsNullOrWhiteSpace(ClientId) && HasClientSecret;

    /// <summary>The normalised base URL as stored (<c>null</c> for gitlab.com).</summary>
    public string? StoredBaseUrl => IsGitLabCom ? null : BaseUrl.AbsoluteUri.TrimEnd('/');
}

/// <summary>
/// FR-130: the GitLab instances: Admin rows plus configured ones (which take precedence for the same base URL), with
/// gitlab.com always offered. Rows for gitlab.com and for configured instances are created on first use, so connections and
/// projects can reference them; their configured secrets stay in configuration.
/// </summary>
public interface IGitLabInstances
{
    /// <summary>Every GitLab instance (gitlab.com first, then by name).</summary>
    Task<IReadOnlyList<GitLabInstance>> ListAsync(CancellationToken cancellationToken);

    Task<GitLabInstance?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The instance's OAuth client secret (configuration first, else the decrypted row), or <c>null</c>.</summary>
    string? GetClientSecret(GitHostInstance row, GitLabInstance instance);

    /// <summary>The instance's OAuth client secret, loading its row.</summary>
    Task<string?> GetClientSecretAsync(GitLabInstance instance, CancellationToken cancellationToken);

    /// <summary>The configured instance with this stored base URL (<c>null</c> = gitlab.com), if any.</summary>
    GitLabInstanceSettings? ConfiguredFor(string? storedBaseUrl);

    /// <summary>The OAuth redirect URI registered on every GitLab application: <c>App:PublicBaseUrl</c> + the callback path.</summary>
    string RedirectUri { get; }

    /// <summary>The view of a row with configuration applied.</summary>
    GitLabInstance ToInstance(GitHostInstance row);
}

internal sealed class GitLabInstances(
    AideDbContext db,
    IOptionsMonitor<GitLabOptions> options,
    ISecretProtectors protectors,
    IConfiguration configuration,
    TimeProvider time) : IGitLabInstances
{
    /// <summary>Same default as the API's <c>App:PublicBaseUrl</c>.</summary>
    private const string DefaultPublicBaseUrl = "http://localhost:8080";

    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostClientSecret);

    public string RedirectUri =>
        (configuration["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured : DefaultPublicBaseUrl).TrimEnd('/') + GitLabRules.CallbackPath;

    public async Task<IReadOnlyList<GitLabInstance>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadAsync(cancellationToken);
        if (await EnsureRowsAsync(rows, cancellationToken))
        {
            rows = await LoadAsync(cancellationToken);
        }

        return [.. rows.Select(ToInstance).OrderBy(i => i.IsGitLabCom ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)];
    }

    public async Task<GitLabInstance?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.GitHostInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == GitHostKind.Gitlab, cancellationToken);
        return row is null ? null : ToInstance(row);
    }

    public async Task<string?> GetClientSecretAsync(GitLabInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var row = await db.GitHostInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instance.Id, cancellationToken);
        return row is null ? null : GetClientSecret(row, instance);
    }

    public string? GetClientSecret(GitHostInstance row, GitLabInstance instance)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(instance);
        if (ConfiguredFor(row.BaseUrl) is { ClientSecret: { Length: > 0 } configured })
        {
            return configured;
        }

        return row.OauthClientSecretEncrypted.Length > 0 && Protector.TryUnprotect(row.OauthClientSecretEncrypted, out var secret) ? secret : null;
    }

    public GitLabInstanceSettings? ConfiguredFor(string? storedBaseUrl)
    {
        var current = options.CurrentValue;
        return current.Instances.FirstOrDefault(i =>
            i.NormalizedBaseUrl(current.AllowPrivateNetworks) is { IsSuccess: true } normalized
            && string.Equals(normalized.Value, storedBaseUrl, StringComparison.Ordinal));
    }

    public GitLabInstance ToInstance(GitHostInstance row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var configured = ConfiguredFor(row.BaseUrl);
        var isGitLabCom = row.BaseUrl is null;
        var name = configured?.Name is { Length: > 0 } configuredName ? configuredName.Trim() : row.Name;
        var clientId = configured?.ClientId is { Length: > 0 } configuredId ? configuredId.Trim() : NullIfEmpty(row.OauthClientId);
        var hasSecret = configured?.ClientSecret is { Length: > 0 } || row.OauthClientSecretEncrypted.Length > 0;
        return new GitLabInstance(
            row.Id,
            name,
            new Uri(row.BaseUrl ?? GitLabRules.GitLabComBaseUrl, UriKind.Absolute),
            isGitLabCom,
            configured is null ? GitHostInstanceSource.Database : GitHostInstanceSource.Configuration,
            clientId,
            hasSecret,
            configured?.AllowInsecureHttp ?? row.AllowInsecureHttp,
            row.Enabled,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt);
    }

    private Task<List<GitHostInstance>> LoadAsync(CancellationToken cancellationToken) =>
        db.GitHostInstances.AsNoTracking().Where(i => i.Kind == GitHostKind.Gitlab).ToListAsync(cancellationToken);

    /// <summary>Creates the gitlab.com row and the rows of configured instances when missing. Returns whether any was added.</summary>
    private async Task<bool> EnsureRowsAsync(List<GitHostInstance> rows, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = GitLabRules.GitLabComName };
        foreach (var configured in current.Instances)
        {
            if (configured.NormalizedBaseUrl(current.AllowPrivateNetworks) is { IsSuccess: true } normalized)
            {
                var key = normalized.Value ?? string.Empty;
                wanted[key] = configured.Name is { Length: > 0 } n ? n.Trim() : key.Length == 0 ? GitLabRules.GitLabComName : new Uri(key).Host;
            }
        }

        var existing = rows.Select(r => r.BaseUrl ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(w => !existing.Contains(w.Key)).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        var now = time.GetUtcNow();
        foreach (var (baseUrl, name) in missing)
        {
            db.GitHostInstances.Add(new GitHostInstance
            {
                Kind = GitHostKind.Gitlab,
                Name = name.Length > GitLabRules.MaxNameLength ? name[..GitLabRules.MaxNameLength] : name,
                BaseUrl = baseUrl.Length == 0 ? null : baseUrl,
                OauthClientId = string.Empty,
                OauthClientSecretEncrypted = string.Empty,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request created them first.
        }
        finally
        {
            foreach (var entry in db.ChangeTracker.Entries<GitHostInstance>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        return true;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
