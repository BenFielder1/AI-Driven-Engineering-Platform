using Aide.Api.Features.Deployments;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Platforms;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Connections;

/// <summary>
/// FR-142: the caller's platform connections (<c>platform_connections</c>): Vercel, Netlify, Cloudflare, Render, Railway and
/// Supabase tokens, checked with the platform on save (§4.20, §4.21), encrypted (<see cref="SecretPurposes.PlatformToken"/>)
/// and never returned or logged. Connections are personal: another user's id is <c>NOT_FOUND</c>.
/// <list type="bullet">
/// <item>A token the platform rejects (401, or its invalid-token 403) isn't saved (<c>CREDENTIAL_INVALID</c>); found
/// rejected on re-test or in use, the connection becomes <c>invalid</c> and its owner is notified (<c>CONNECTION_INVALID</c>).</item>
/// <item>A 403 for missing permissions while checking the token saves (or marks) the connection <c>insufficient_scope</c>,
/// with the missing names where the platform returns them (else a placeholder). In a picker request it fails that request
/// only (<c>CONNECTION_SCOPE_INSUFFICIENT</c>).</item>
/// <item>429 is <c>UPSTREAM_RATE_LIMITED</c>; other platform failures are <c>UPSTREAM_ERROR</c> (status unchanged).</item>
/// </list>
/// </summary>
public sealed partial class PlatformConnectionService(
    AideDbContext db,
    IPlatformConnectionValidators validators,
    ISecretProtectors protectors,
    TimeProvider time,
    ILogger<PlatformConnectionService> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.PlatformToken);

    public async Task<IReadOnlyList<PlatformConnectionDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.PlatformConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        var bound = await BoundProjectsAsync([.. rows.Select(c => c.Id)], cancellationToken);
        return [.. rows.Select(c => ToDto(c, bound.GetValueOrDefault(c.Id) ?? []))];
    }

    /// <summary>FR-142: checks the token with the platform, then saves it; nothing is saved when the check fails.</summary>
    public async Task<Result<PlatformConnectionDto>> CreateAsync(Guid userId, CreatePlatformConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = PlatformConnectionRules.ValidateNew(request.Label, request.Token, request.AccountId);
        if (valid.IsFailure)
        {
            return Result.Failure<PlatformConnectionDto>(valid.Error!);
        }

        var kind = PlatformConnectionRules.CheckTokenKind(request.Provider, request.Token);
        if (kind.IsFailure)
        {
            LogRefused(logger, request.Provider, null, ErrorCodes.ConnectionScopeInsufficient);
            return Result.Failure<PlatformConnectionDto>(kind.Error!);
        }

        var now = Now();
        var connection = new PlatformConnection
        {
            UserId = userId,
            Provider = request.Provider,
            Label = request.Label.Trim(),
            SecretEncrypted = Protector.Protect(request.Token),
            Last4 = PlatformConnectionRules.Last4(request.Token),
            AccountId = request.AccountId,
            LastValidatedAt = now,
            CreatedAt = now,
        };
        try
        {
            var check = await validators.ForProvider(request.Provider)
                .ValidateAsync(new PlatformCredential(request.Token, request.AccountId, new PlatformConnectionMetadata()), cancellationToken);
            var applied = Apply(connection, check, request.AccountId);
            if (applied.IsFailure)
            {
                return Result.Failure<PlatformConnectionDto>(applied.Error!);
            }
        }
        catch (PlatformApiException ex)
        {
            if (PlatformConnectionRules.SaveError(request.Provider, ex.Failure) is { } error)
            {
                LogRefused(logger, request.Provider, ex.Status, error.Code);
                return Result.Failure<PlatformConnectionDto>(error with { RetryAfter = RetryAfter(ex) });
            }

            // FR-142: missing permissions save the connection as insufficient_scope, with the names the platform gave.
            connection.Status = PlatformConnectionStatus.InsufficientScope;
            connection.Metadata = connection.Metadata with { MissingPermissions = PlatformConnectionRules.MissingOrPlaceholder(ex.MissingPermissions) };
        }

        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, connection.Id, connection.Provider, connection.Status);
        return Result.Success(ToDto(connection, []));
    }

    public async Task<Result<PlatformConnectionDto>> RenameAsync(Guid userId, Guid id, string label, CancellationToken cancellationToken)
    {
        var valid = PlatformConnectionRules.ValidateLabel(label);
        if (valid.IsFailure)
        {
            return Result.Failure<PlatformConnectionDto>(valid.Error!);
        }

        var connection = await OwnAsync(userId, id, cancellationToken);
        if (connection is null)
        {
            return NotFound<PlatformConnectionDto>();
        }

        connection.Label = label.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(await DtoAsync(connection, cancellationToken));
    }

    /// <summary>
    /// FR-142, FR-144: deletes one of the caller's connections. Every deployment target bound to it loses its binding in the
    /// same transaction (<c>SETTINGS_UPDATED</c>, <c>connection_deleted</c>); targets, env vars and history stay.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, string actor, CancellationToken cancellationToken)
    {
        var connection = await OwnAsync(userId, id, cancellationToken);
        if (connection is null)
        {
            return false;
        }

        var now = Now();
        var targets = await db.DeploymentTargets.Where(t => t.ConnectionId == id).ToListAsync(cancellationToken);
        foreach (var target in targets)
        {
            DeploymentBindings.Clear(db, target, DeploymentBindings.ConnectionDeleted, actor, now);
        }

        db.PlatformConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
        LogDeleted(logger, id, targets.Count);
        return true;
    }

    /// <summary>
    /// FR-142: checks the token with the platform again and records the result: <c>valid</c> (account and metadata
    /// refreshed), <c>insufficient_scope</c>, or <c>invalid</c> (rejected, or the token can't be decrypted any more; the owner
    /// is notified). <c>UPSTREAM_RATE_LIMITED</c> or <c>UPSTREAM_ERROR</c> leave the status as it was.
    /// </summary>
    public async Task<Result<PlatformConnectionDto>> TestAsync(Guid userId, Guid id, string actor, CancellationToken cancellationToken)
    {
        var connection = await OwnAsync(userId, id, cancellationToken);
        if (connection is null)
        {
            return NotFound<PlatformConnectionDto>();
        }

        var now = Now();
        if (!Protector.TryUnprotect(connection.SecretEncrypted, out var token) || token.Length == 0)
        {
            // A token that can't be decrypted (lost key ring) can never work again: it has to be added anew.
            SetStatus(connection, PlatformConnectionStatus.Invalid, null, "unreadable", actor, now);
        }
        else
        {
            try
            {
                var check = await validators.ForProvider(connection.Provider)
                    .ValidateAsync(new PlatformCredential(token, connection.AccountId, connection.Metadata), cancellationToken);
                if (Apply(connection, check, connection.AccountId).IsFailure)
                {
                    // The token no longer reaches the account it was saved for.
                    connection.Status = PlatformConnectionStatus.InsufficientScope;
                }

                LogChecked(logger, connection.Id, connection.Provider, connection.Status, 200);
            }
            catch (PlatformApiException ex) when (PlatformConnectionRules.StatusAfter(ex.Failure, ex.MissingPermissions.Count) is { } status)
            {
                SetStatus(connection, status, ex.MissingPermissions, "rejected", actor, now);
                LogChecked(logger, connection.Id, connection.Provider, connection.Status, ex.Status);
            }
            catch (PlatformApiException ex)
            {
                LogChecked(logger, connection.Id, connection.Provider, connection.Status, ex.Status);
                return Result.Failure<PlatformConnectionDto>(
                    PlatformConnectionRules.UseError(connection.Provider, ex.Failure) with { RetryAfter = RetryAfter(ex) });
            }
        }

        connection.LastValidatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(await DtoAsync(connection, cancellationToken));
    }

    /// <summary>FR-142 picker: the teams, accounts, workspaces or organisations the connection's token reaches.</summary>
    public Task<Result<IReadOnlyList<PlatformAccountDto>>> AccountsAsync(Guid userId, Guid id, string actor, CancellationToken cancellationToken) =>
        UseAsync(userId, id, actor, async (validator, credential) =>
        {
            var accounts = await validator.ListAccountsAsync(credential, cancellationToken);
            return (IReadOnlyList<PlatformAccountDto>)[.. accounts.Select(ToDto)];
        }, cancellationToken);

    /// <summary>
    /// FR-142 picker: the connection's resources of <paramref name="type"/> (the provider's default when <c>null</c>;
    /// <c>VALIDATION</c> when the provider doesn't list it), in <paramref name="accountId"/> (default: the connection's account).
    /// </summary>
    public async Task<Result<IReadOnlyList<PlatformResourceDto>>> ResourcesAsync(
        Guid userId, Guid id, string? type, string? accountId, string actor, CancellationToken cancellationToken)
    {
        if (accountId is not null && !PlatformConnectionRules.IsAccountId(accountId))
        {
            return Result.Failure<IReadOnlyList<PlatformResourceDto>>(new DomainError(ErrorCodes.Validation, "The request is invalid.")
            {
                Details = [$"accountId: Letters, digits, '.', '_', ':' or '-', up to {PlatformConnectionRules.MaxAccountIdLength} characters."],
            });
        }

        var provider = await db.PlatformConnections.AsNoTracking()
            .Where(c => c.Id == id && c.UserId == userId)
            .Select(c => (PlatformProvider?)c.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        if (provider is not { } known)
        {
            return NotFound<IReadOnlyList<PlatformResourceDto>>();
        }

        var resourceType = PlatformConnectionRules.ResourceType(known, type);
        if (resourceType.IsFailure)
        {
            return Result.Failure<IReadOnlyList<PlatformResourceDto>>(resourceType.Error!);
        }

        return await UseAsync(userId, id, actor, async (validator, credential) =>
        {
            var resources = await validator.ListResourcesAsync(credential, resourceType.Value, accountId, cancellationToken);
            return (IReadOnlyList<PlatformResourceDto>)[.. resources.Select(r => new PlatformResourceDto(r.Id, r.Name, r.Url, r.Detail))];
        }, cancellationToken);
    }

    /// <summary>
    /// FR-142: the platform rejected <paramref name="connectionId"/>'s token while it was in use: the connection becomes
    /// <c>invalid</c> and, the first time, <c>CONNECTION_INVALID</c> notifies its owner (no secret). Added to the caller's unit
    /// of work; the caller saves.
    /// </summary>
    public static async Task MarkInvalidAsync(
        AideDbContext db, Guid connectionId, Guid? projectId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = await db.PlatformConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return;
        }

        connection.LastValidatedAt = now;
        Invalidate(db, connection, projectId, "rejected", actor, now);
    }

    /// <summary>Runs <paramref name="call"/> with the caller's connection's token; a rejection or missing permission is recorded.</summary>
    private async Task<Result<T>> UseAsync<T>(
        Guid userId, Guid id, string actor, Func<IPlatformConnectionValidator, PlatformCredential, Task<T>> call, CancellationToken cancellationToken)
    {
        var connection = await OwnAsync(userId, id, cancellationToken);
        if (connection is null)
        {
            return NotFound<T>();
        }

        if (connection.Status == PlatformConnectionStatus.Invalid)
        {
            return Result.Failure<T>(new DomainError(
                ErrorCodes.CredentialInvalid,
                $"The {PlatformConnectionRules.DisplayName(connection.Provider)} connection is invalid. Test it again or add a new token in Account → Connections."));
        }

        var now = Now();
        if (!Protector.TryUnprotect(connection.SecretEncrypted, out var token) || token.Length == 0)
        {
            SetStatus(connection, PlatformConnectionStatus.Invalid, null, "unreadable", actor, now);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure<T>(PlatformConnectionRules.UseError(connection.Provider, PlatformFailure.Rejected));
        }

        try
        {
            return Result.Success(await call(validators.ForProvider(connection.Provider), new PlatformCredential(token, connection.AccountId, connection.Metadata)));
        }
        catch (PlatformApiException ex)
        {
            // Only a rejected token invalidates the connection (and notifies its owner). A permission refused for one picker
            // request (e.g. Cloudflare Pages projects without Pages Edit) fails that request only: the connection keeps its
            // status, as the permission may be optional.
            if (ex.Failure == PlatformFailure.Rejected)
            {
                SetStatus(connection, PlatformConnectionStatus.Invalid, null, "rejected", actor, now);
                connection.LastValidatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            LogUseFailed(logger, connection.Id, connection.Provider, ex.Failure, ex.Status);
            return Result.Failure<T>(PlatformConnectionRules.UseError(connection.Provider, ex.Failure, ex.MissingPermissions) with { RetryAfter = RetryAfter(ex) });
        }
    }

    /// <summary>
    /// Records what a successful check found (account chosen from what the token reaches, metadata, scopes, expiry, status
    /// <c>valid</c> or <c>insufficient_scope</c>); <c>VALIDATION</c> when <paramref name="requestedAccount"/> isn't reachable.
    /// </summary>
    private static Result Apply(PlatformConnection connection, PlatformTokenCheck check, string? requestedAccount)
    {
        var account = PlatformConnectionRules.ChooseAccount(check.Accounts, requestedAccount);
        if (requestedAccount is not null && account is null && !string.Equals(requestedAccount, check.UserId, StringComparison.Ordinal))
        {
            return Result.Failure(new DomainError(ErrorCodes.Validation, "The connection is invalid.")
            {
                Details = [$"accountId: The token can't reach that {PlatformConnectionRules.DisplayName(connection.Provider)} account."],
            });
        }

        connection.AccountId = account?.Id ?? check.UserId;
        connection.AccountName = account?.Name ?? check.UserName;
        connection.Metadata = check.Metadata with { Accounts = check.Accounts };
        connection.Scopes = check.Scopes;
        connection.ExpiresAt = check.ExpiresAt;
        connection.Status = PlatformConnectionRules.StatusAfter(null, check.Metadata.MissingPermissions.Count)!.Value;
        return Result.Success();
    }

    /// <summary>Sets the status (with the missing permissions for <c>insufficient_scope</c>); becoming invalid notifies the owner.</summary>
    private void SetStatus(PlatformConnection connection, PlatformConnectionStatus status, IReadOnlyList<string>? missing, string reason, string actor, DateTimeOffset now)
    {
        if (status == PlatformConnectionStatus.Invalid)
        {
            Invalidate(db, connection, null, reason, actor, now);
            return;
        }

        connection.Status = status;
        if (status == PlatformConnectionStatus.InsufficientScope)
        {
            connection.Metadata = connection.Metadata with { MissingPermissions = PlatformConnectionRules.MissingOrPlaceholder(missing) };
        }
    }

    private static void Invalidate(AideDbContext db, PlatformConnection connection, Guid? projectId, string reason, string actor, DateTimeOffset now)
    {
        var before = connection.Status;
        connection.Status = PlatformConnectionStatus.Invalid;
        if (PlatformConnectionRules.NotifiesInvalid(before, PlatformConnectionStatus.Invalid))
        {
            db.AddEvent(
                EventTypes.ConnectionInvalid,
                projectId,
                new { connectionId = connection.Id, userId = connection.UserId, provider = connection.Provider.ToWire(), reason },
                actor,
                now);
        }
    }

    private Task<PlatformConnection?> OwnAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.PlatformConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);

    private async Task<PlatformConnectionDto> DtoAsync(PlatformConnection connection, CancellationToken cancellationToken) =>
        ToDto(connection, (await BoundProjectsAsync([connection.Id], cancellationToken)).GetValueOrDefault(connection.Id) ?? []);

    private async Task<Dictionary<Guid, List<PlatformBoundProjectDto>>> BoundProjectsAsync(IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0)
        {
            return [];
        }

        var rows = await (
                from t in db.DeploymentTargets.AsNoTracking()
                join p in db.Projects.AsNoTracking() on t.ProjectId equals p.Id
                where t.ConnectionId != null && connectionIds.Contains(t.ConnectionId.Value)
                orderby p.Name
                select new { ConnectionId = t.ConnectionId!.Value, p.Id, p.Name })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.ConnectionId)
            .ToDictionary(g => g.Key, g => g.DistinctBy(r => r.Id).Select(r => new PlatformBoundProjectDto(r.Id, r.Name)).ToList());
    }

    internal static PlatformConnectionDto ToDto(PlatformConnection c, IReadOnlyList<PlatformBoundProjectDto> boundProjects) =>
        new(
            c.Id,
            c.Provider,
            c.Label,
            c.Last4,
            c.AccountId,
            c.AccountName,
            new PlatformConnectionMetadataDto(
                c.Metadata.TeamId,
                c.Metadata.TeamSlug,
                [.. c.Metadata.Accounts.Select(ToDto)],
                c.Metadata.Policies,
                c.Metadata.MissingPermissions,
                c.Metadata.TokenKind),
            c.Scopes,
            c.ExpiresAt,
            c.Status,
            c.LastValidatedAt,
            c.CreatedAt,
            boundProjects);

    private static PlatformAccountDto ToDto(PlatformAccountRef a) => new(a.Id, a.Name, a.Slug, a.Kind);

    private TimeSpan? RetryAfter(PlatformApiException ex) =>
        ex.RetryAt is { } at && at > time.GetUtcNow() ? at - time.GetUtcNow() : null;

    /// <summary>The current time at PostgreSQL's <c>timestamptz</c> precision (microseconds).</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static Result<T> NotFound<T>() => Result.Failure<T>(new DomainError(ErrorCodes.NotFound, "The connection was not found."));

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform connection {ConnectionId} added ({Provider}, {Status})")]
    private static partial void LogCreated(ILogger logger, Guid connectionId, PlatformProvider provider, PlatformConnectionStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} token refused on save: HTTP {StatusCode}, {ErrorCode}")]
    private static partial void LogRefused(ILogger logger, PlatformProvider provider, int? statusCode, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform connection {ConnectionId} ({Provider}) checked: {Status} (HTTP {StatusCode})")]
    private static partial void LogChecked(ILogger logger, Guid connectionId, PlatformProvider provider, PlatformConnectionStatus status, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform connection {ConnectionId} ({Provider}) failed in use: {Failure} (HTTP {StatusCode})")]
    private static partial void LogUseFailed(ILogger logger, Guid connectionId, PlatformProvider provider, PlatformFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform connection {ConnectionId} deleted; {Bindings} target bindings cleared")]
    private static partial void LogDeleted(ILogger logger, Guid connectionId, int bindings);
}
