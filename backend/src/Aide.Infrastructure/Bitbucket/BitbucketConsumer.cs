using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// The Bitbucket Cloud OAuth consumer as AIDE uses it (FR-133): the single <c>git_host_instances</c> row of kind
/// <c>bitbucket</c> (base URL NULL: Bitbucket is Cloud only, its hosts fixed) with configuration
/// (<c>Bitbucket:OAuth:*</c>) applied on top. <see cref="InstanceId"/> is what Bitbucket connections and sign-in links
/// reference. The secret is never on this record (<see cref="IBitbucketConsumer.GetClientSecretAsync"/>).
/// </summary>
public sealed record BitbucketConsumerInfo(
    Guid InstanceId,
    GitHostInstanceSource Source,
    string? ClientId,
    bool HasClientSecret,
    DateTimeOffset UpdatedAt)
{
    /// <summary>OAuth (and so sign-in) needs the consumer key and secret.</summary>
    public bool OAuthConfigured => !string.IsNullOrWhiteSpace(ClientId) && HasClientSecret;
}

/// <summary>FR-133: the Bitbucket consumer, its row created on first use.</summary>
public interface IBitbucketConsumer
{
    Task<BitbucketConsumerInfo> GetAsync(CancellationToken cancellationToken);

    /// <summary>The consumer secret (configuration first, else the decrypted row), or <c>null</c>.</summary>
    Task<string?> GetClientSecretAsync(CancellationToken cancellationToken);

    /// <summary>The OAuth callback to register on the consumer: <c>App:PublicBaseUrl</c> + <c>/api/auth/bitbucket/callback</c>.</summary>
    string RedirectUri { get; }
}

internal sealed class BitbucketConsumer(
    AideDbContext db,
    IOptionsMonitor<BitbucketOptions> options,
    ISecretProtectors protectors,
    IConfiguration configuration,
    TimeProvider time) : IBitbucketConsumer
{
    /// <summary>Same default as the API's <c>App:PublicBaseUrl</c>.</summary>
    private const string DefaultPublicBaseUrl = "http://localhost:8080";

    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostClientSecret);

    public string RedirectUri =>
        (configuration["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured : DefaultPublicBaseUrl).TrimEnd('/') + BitbucketRules.CallbackPath;

    public async Task<BitbucketConsumerInfo> GetAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        var configured = options.CurrentValue.OAuth;
        return configured.IsConfigured
            ? new BitbucketConsumerInfo(row.Id, GitHostInstanceSource.Configuration, configured.ClientId!.Trim(), !string.IsNullOrEmpty(configured.ClientSecret), row.UpdatedAt)
            : new BitbucketConsumerInfo(
                row.Id,
                GitHostInstanceSource.Database,
                string.IsNullOrWhiteSpace(row.OauthClientId) ? null : row.OauthClientId,
                row.OauthClientSecretEncrypted.Length > 0,
                row.UpdatedAt);
    }

    public async Task<string?> GetClientSecretAsync(CancellationToken cancellationToken)
    {
        var configured = options.CurrentValue.OAuth;
        if (configured.IsConfigured)
        {
            return string.IsNullOrEmpty(configured.ClientSecret) ? null : configured.ClientSecret;
        }

        var row = await RowAsync(cancellationToken);
        return row.OauthClientSecretEncrypted.Length > 0 && Protector.TryUnprotect(row.OauthClientSecretEncrypted, out var secret) ? secret : null;
    }

    /// <summary>The Bitbucket Cloud row, created when missing.</summary>
    private async Task<GitHostInstance> RowAsync(CancellationToken cancellationToken)
    {
        var row = await db.GitHostInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Kind == GitHostKind.Bitbucket, cancellationToken);
        if (row is not null)
        {
            return row;
        }

        var now = time.GetUtcNow();
        var created = new GitHostInstance
        {
            Kind = GitHostKind.Bitbucket,
            Name = BitbucketRules.CloudName,
            BaseUrl = null,
            OauthClientId = string.Empty,
            OauthClientSecretEncrypted = string.Empty,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.GitHostInstances.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request created it first.
        }
        finally
        {
            db.Entry(created).State = EntityState.Detached;
        }

        return await db.GitHostInstances.AsNoTracking().FirstAsync(i => i.Kind == GitHostKind.Bitbucket, cancellationToken);
    }
}
