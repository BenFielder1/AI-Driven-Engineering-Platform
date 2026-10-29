using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// FR-133: Admin → Integrations → Bitbucket: the OAuth consumer (key, and secret encrypted with
/// <see cref="SecretPurposes.GitHostClientSecret"/>, never returned) on the Bitbucket Cloud row, with the callback URL and
/// the consumer permissions to tick. A consumer from configuration (<c>Bitbucket__OAuth__*</c>) takes precedence and can't
/// be changed or removed here (<c>CONFLICT</c>).
/// </summary>
public sealed partial class BitbucketAdmin(
    AideDbContext db,
    IBitbucketConsumer consumer,
    ISecretProtectors protectors,
    TimeProvider time,
    ILogger<BitbucketAdmin> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostClientSecret);

    public async Task<AdminBitbucketIntegrationDto> GetAsync(CancellationToken cancellationToken)
    {
        var info = await consumer.GetAsync(cancellationToken);
        var connections = await db.GitConnections.AsNoTracking().CountAsync(c => c.InstanceId == info.InstanceId, cancellationToken);
        return new AdminBitbucketIntegrationDto(
            consumer.RedirectUri,
            BitbucketRules.ConsumerPermissions,
            info.Source,
            info.ClientId,
            info.HasClientSecret,
            info.OAuthConfigured,
            connections,
            info.UpdatedAt);
    }

    /// <summary>FR-133: saves the consumer key and (when sent) secret. <c>CONFLICT</c> when the consumer is configured.</summary>
    public async Task<Result<AdminBitbucketIntegrationDto>> UpdateAsync(UpdateBitbucketConsumerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clientId = request.ClientId.Trim();
        if (clientId.Length == 0 || clientId.Any(char.IsWhiteSpace) || request.ClientSecret is { } s && (s.Length == 0 || s.Any(char.IsWhiteSpace)))
        {
            return Result.Failure<AdminBitbucketIntegrationDto>(new DomainError(ErrorCodes.Validation, "The Bitbucket consumer is invalid.")
            {
                Details = clientId.Length == 0 || clientId.Any(char.IsWhiteSpace)
                    ? ["clientId: Paste the consumer's Key from Bitbucket."]
                    : ["clientSecret: Paste the consumer's Secret from Bitbucket, or leave it out to keep the saved one."],
            });
        }

        var row = await RowAsync(cancellationToken);
        if (row.Configured)
        {
            return Result.Failure<AdminBitbucketIntegrationDto>(Configured());
        }

        if (request.ClientSecret is null && row.Row.OauthClientSecretEncrypted.Length == 0)
        {
            return Result.Failure<AdminBitbucketIntegrationDto>(new DomainError(ErrorCodes.Validation, "The Bitbucket consumer is invalid.")
            {
                Details = ["clientSecret: Paste the consumer's Secret from Bitbucket."],
            });
        }

        row.Row.OauthClientId = clientId;
        if (request.ClientSecret is { } secret)
        {
            row.Row.OauthClientSecretEncrypted = Protector.Protect(secret);
        }

        row.Row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        LogSaved(logger);
        return Result.Success(await GetAsync(cancellationToken));
    }

    /// <summary>
    /// FR-133: removes the saved consumer (key and secret). Sign-in with Bitbucket and OAuth connect stop being offered, and
    /// OAuth connections become invalid at their next refresh; API-token connections keep working. <c>CONFLICT</c> when the
    /// consumer is configured.
    /// </summary>
    public async Task<Result> DeleteAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        if (row.Configured)
        {
            return Result.Failure(Configured());
        }

        row.Row.OauthClientId = string.Empty;
        row.Row.OauthClientSecretEncrypted = string.Empty;
        row.Row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        LogRemoved(logger);
        return Result.Success();
    }

    /// <summary>The tracked Bitbucket Cloud row (created on first use) and whether configuration owns the consumer.</summary>
    private async Task<(GitHostInstance Row, bool Configured)> RowAsync(CancellationToken cancellationToken)
    {
        var info = await consumer.GetAsync(cancellationToken);
        var row = await db.GitHostInstances.FirstAsync(i => i.Id == info.InstanceId, cancellationToken);
        return (row, info.Source == GitHostInstanceSource.Configuration);
    }

    private static DomainError Configured() =>
        new(ErrorCodes.Conflict, "The Bitbucket consumer is configured in the server's environment (Bitbucket__OAuth__*), which takes precedence. Change it there.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket OAuth consumer saved")]
    private static partial void LogSaved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket OAuth consumer removed")]
    private static partial void LogRemoved(ILogger logger);
}
