using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// FR-130: Admin → Integrations → GitLab: list, add, change, check and remove instances. Adding or changing a base URL runs
/// the SSRF guard (normalisation, then the resolved addresses, §8) and the reachability check (<c>GET /api/v4/version</c>; an
/// unauthenticated 401 counts as reachable; the version is recorded and a version below 17.11 warns). Configured instances
/// (<c>GitLab:Instances:*</c>) take precedence: only <c>enabled</c> changes here. Client secrets are encrypted
/// (<see cref="SecretPurposes.GitHostClientSecret"/>) and never returned.
/// </summary>
public sealed partial class GitLabAdmin(
    AideDbContext db,
    IGitLabInstances instances,
    IGitLabClients clients,
    GitHostConnector connector,
    ISecretProtectors protectors,
    IOptionsMonitor<GitLabOptions> options,
    TimeProvider time,
    ILogger<GitLabAdmin> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostClientSecret);

    public async Task<AdminGitLabIntegrationDto> OverviewAsync(CancellationToken cancellationToken)
    {
        var all = await instances.ListAsync(cancellationToken);
        var counts = await CountsAsync([.. all.Select(i => i.Id)], cancellationToken);
        return new AdminGitLabIntegrationDto(
            instances.RedirectUri,
            GitTokenRules.GitLabOauthScopes,
            $"{GitLabRules.MinimumVersion.Major}.{GitLabRules.MinimumVersion.Minor}",
            options.CurrentValue.AllowPrivateNetworks,
            [.. all.Select(i => ToDto(i, counts))]);
    }

    public async Task<AdminGitLabInstanceDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await instances.ListAsync(cancellationToken);
        var instance = await instances.GetAsync(id, cancellationToken);
        return instance is null ? null : ToDto(instance, await CountsAsync([id], cancellationToken));
    }

    public async Task<Result<AdminGitLabInstanceDto>> CreateAsync(Guid adminId, CreateGitLabInstanceRequest request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = GitLabRules.ValidateInstanceName(request.Name);
        if (name.IsFailure)
        {
            return Result.Failure<AdminGitLabInstanceDto>(name.Error!);
        }

        var baseUrl = Normalize(request.BaseUrl, request.AllowInsecureHttp);
        if (baseUrl.IsFailure)
        {
            return Result.Failure<AdminGitLabInstanceDto>(baseUrl.Error!);
        }

        if (baseUrl.Value is null)
        {
            return Conflict("gitlab.com is already listed. Edit it to add its OAuth application.");
        }

        await instances.ListAsync(cancellationToken);
        if (instances.ConfiguredFor(baseUrl.Value) is not null)
        {
            return Conflict("This instance is configured in the server's environment (GitLab__Instances__*), which takes precedence.");
        }

        if (await db.GitHostInstances.AnyAsync(i => i.Kind == GitHostKind.Gitlab && i.BaseUrl == baseUrl.Value, cancellationToken))
        {
            return Conflict("A GitLab instance with this base URL already exists.");
        }

        var check = await CheckReachableAsync(new Uri(baseUrl.Value), cancellationToken);
        if (check.IsFailure)
        {
            return Result.Failure<AdminGitLabInstanceDto>(check.Error!);
        }

        var now = time.GetUtcNow();
        var row = new GitHostInstance
        {
            Kind = GitHostKind.Gitlab,
            Name = request.Name.Trim(),
            BaseUrl = baseUrl.Value,
            OauthClientId = request.ClientId?.Trim() ?? string.Empty,
            OauthClientSecretEncrypted = string.IsNullOrEmpty(request.ClientSecret) ? string.Empty : Protector.Protect(request.ClientSecret),
            AllowInsecureHttp = request.AllowInsecureHttp,
            Enabled = request.Enabled,
            Version = check.Value,
            CreatedByUserId = adminId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.GitHostInstances.Add(row);
        AddChanged(row, "created", actor, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict("A GitLab instance with this base URL already exists.");
        }

        LogAdded(logger, row.Id, baseUrl.Value);
        return Result.Success(ToDto(instances.ToInstance(row), new Dictionary<Guid, (int, int)>()));
    }

    public async Task<Result<AdminGitLabInstanceDto>> UpdateAsync(Guid id, UpdateGitLabInstanceRequest request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await db.GitHostInstances.FirstOrDefaultAsync(i => i.Id == id && i.Kind == GitHostKind.Gitlab, cancellationToken);
        if (row is null)
        {
            return Result.Failure<AdminGitLabInstanceDto>(new DomainError(ErrorCodes.NotFound, "The GitLab instance was not found."));
        }

        var configured = instances.ConfiguredFor(row.BaseUrl) is not null;
        if (configured && (request.Name is not null || request.BaseUrl is not null || request.ClientId is not null
            || request.ClientSecret is not null || request.AllowInsecureHttp is not null))
        {
            return Conflict("This instance is configured in the server's environment (GitLab__Instances__*); only enabled can be changed here.");
        }

        if (request.Name is not null)
        {
            var name = GitLabRules.ValidateInstanceName(request.Name);
            if (name.IsFailure)
            {
                return Result.Failure<AdminGitLabInstanceDto>(name.Error!);
            }

            row.Name = request.Name.Trim();
        }

        var insecure = request.AllowInsecureHttp ?? row.AllowInsecureHttp;
        var addressChanged = false;
        if (request.BaseUrl is not null || request.AllowInsecureHttp is { } flag && flag != row.AllowInsecureHttp)
        {
            if (row.BaseUrl is null)
            {
                var stillGitLabCom = request.BaseUrl is null || Normalize(request.BaseUrl, insecure) is { IsSuccess: true, Value: null };
                if (!stillGitLabCom || request.AllowInsecureHttp == true)
                {
                    return Result.Failure<AdminGitLabInstanceDto>(new DomainError(ErrorCodes.Validation, "The GitLab instance is invalid.")
                    {
                        Details = ["baseUrl: gitlab.com's base URL can't change. Add a self-managed instance instead."],
                    });
                }
            }
            else
            {
                var baseUrl = Normalize(request.BaseUrl ?? row.BaseUrl, insecure);
                if (baseUrl.IsFailure)
                {
                    return Result.Failure<AdminGitLabInstanceDto>(baseUrl.Error!);
                }

                if (baseUrl.Value is null)
                {
                    return Conflict("gitlab.com is already listed.");
                }

                if (!string.Equals(baseUrl.Value, row.BaseUrl, StringComparison.Ordinal))
                {
                    if (instances.ConfiguredFor(baseUrl.Value) is not null
                        || await db.GitHostInstances.AnyAsync(i => i.Kind == GitHostKind.Gitlab && i.BaseUrl == baseUrl.Value && i.Id != id, cancellationToken))
                    {
                        return Conflict("A GitLab instance with this base URL already exists.");
                    }

                    if (await db.GitConnections.AnyAsync(c => c.InstanceId == id, cancellationToken)
                        || await db.ProjectGitSettings.AnyAsync(s => s.InstanceId == id, cancellationToken))
                    {
                        // §8: tokens are bound to the instance that issued them; moving the URL would send them elsewhere.
                        return Conflict("The base URL can't change while connections or projects use this instance. Add a new instance instead.");
                    }
                }

                row.BaseUrl = baseUrl.Value;
                addressChanged = true;
            }

            row.AllowInsecureHttp = insecure;
        }

        if (request.ClientId is not null)
        {
            row.OauthClientId = request.ClientId.Trim();
        }

        if (request.ClientSecret is not null)
        {
            row.OauthClientSecretEncrypted = request.ClientSecret.Length == 0 ? string.Empty : Protector.Protect(request.ClientSecret);
        }

        if (request.Enabled is { } enabled)
        {
            row.Enabled = enabled;
        }

        if (addressChanged)
        {
            var check = await CheckReachableAsync(new Uri(row.BaseUrl!), cancellationToken);
            if (check.IsFailure)
            {
                return Result.Failure<AdminGitLabInstanceDto>(check.Error!);
            }

            row.Version = check.Value;
        }

        row.UpdatedAt = time.GetUtcNow();
        AddChanged(row, "updated", actor, row.UpdatedAt);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict("A GitLab instance with this base URL already exists.");
        }

        LogUpdated(logger, id, row.Enabled);
        return Result.Success(ToDto(instances.ToInstance(row), await CountsAsync([id], cancellationToken)));
    }

    /// <summary>FR-130: runs the SSRF guard and the reachability check again and records the version.</summary>
    public async Task<Result<AdminGitLabInstanceDto>> CheckAsync(Guid id, CancellationToken cancellationToken)
    {
        await instances.ListAsync(cancellationToken);
        var row = await db.GitHostInstances.FirstOrDefaultAsync(i => i.Id == id && i.Kind == GitHostKind.Gitlab, cancellationToken);
        if (row is null)
        {
            return Result.Failure<AdminGitLabInstanceDto>(new DomainError(ErrorCodes.NotFound, "The GitLab instance was not found."));
        }

        var instance = instances.ToInstance(row);
        var check = await CheckReachableAsync(instance.BaseUrl, cancellationToken);
        if (check.IsFailure)
        {
            return Result.Failure<AdminGitLabInstanceDto>(check.Error!);
        }

        row.Version = check.Value;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(instances.ToInstance(row), await CountsAsync([id], cancellationToken)));
    }

    /// <summary>
    /// FR-130: removes an instance no project uses (<c>CONFLICT</c> otherwise), with its users' connections and sign-in links.
    /// gitlab.com and configured instances can't be removed (disable them instead).
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        var row = await db.GitHostInstances.FirstOrDefaultAsync(i => i.Id == id && i.Kind == GitHostKind.Gitlab, cancellationToken);
        if (row is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "The GitLab instance was not found."));
        }

        if (row.BaseUrl is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "gitlab.com is built in and can't be removed. Disable it instead."));
        }

        if (instances.ConfiguredFor(row.BaseUrl) is not null)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This instance is configured in the server's environment (GitLab__Instances__*). Remove it there."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ProjectGitSettings.AnyAsync(s => s.InstanceId == id, cancellationToken))
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "Projects use this GitLab instance, so it can't be removed. Disable it instead."));
        }

        // The external-login unique index is NULLS NOT DISTINCT, so logins go before the instance (not ON DELETE SET NULL).
        var connections = await db.GitConnections.Where(c => c.InstanceId == id).ExecuteDeleteAsync(cancellationToken);
        await db.UserExternalLogins.Where(l => l.InstanceId == id).ExecuteDeleteAsync(cancellationToken);
        db.GitHostInstances.Remove(row);
        AddChanged(row, "removed", actor, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "Projects use this GitLab instance, so it can't be removed. Disable it instead."));
        }

        LogRemoved(logger, id, connections);
        return Result.Success();
    }

    /// <summary>FR-155: <c>GIT_HOST_INSTANCE_CHANGED</c> (no project; never a secret).</summary>
    private void AddChanged(GitHostInstance row, string change, string actor, DateTimeOffset now) =>
        db.AddEvent(
            EventTypes.GitHostInstanceChanged,
            null,
            new { instanceId = row.Id, kind = row.Kind.ToWire(), change, name = row.Name, baseUrl = row.BaseUrl, enabled = row.Enabled },
            actor,
            now);

    private Result<string?> Normalize(string? baseUrl, bool allowInsecureHttp)
    {
        var normalized = GitHostAddressRules.NormalizeBaseUrl(baseUrl, allowInsecureHttp, options.CurrentValue.AllowPrivateNetworks);
        if (normalized.IsFailure)
        {
            return Result.Failure<string?>(normalized.Error!);
        }

        var text = normalized.Value.AbsoluteUri.TrimEnd('/');
        return Result.Success<string?>(string.Equals(text, GitLabRules.GitLabComBaseUrl, StringComparison.Ordinal) ? null : text);
    }

    /// <summary>
    /// §8 then FR-130: the host's resolved addresses must pass the SSRF guard (<c>GIT_HOST_NOT_ALLOWED</c>), and
    /// <c>GET /api/v4/version</c> must answer with GitLab's JSON (200 with a version, or 401) — else <c>UPSTREAM_ERROR</c>.
    /// Returns the version (<c>null</c> when unauthenticated).
    /// </summary>
    private async Task<Result<string?>> CheckReachableAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            await connector.ResolveAllowedAsync(baseUrl.IdnHost, cancellationToken);
            var probe = await clients.ForInstance(baseUrl).ProbeVersionAsync(cancellationToken);
            if (!probe.Reachable)
            {
                return Upstream($"{baseUrl.Host} didn't answer like GitLab (GET /api/v4/version returned {(probe.Status is { } s ? $"HTTP {s}" : "no answer")}).");
            }

            return Result.Success(probe.Version);
        }
        catch (GitHostBlockedException ex)
        {
            return Result.Failure<string?>(new DomainError(ErrorCodes.GitHostNotAllowed, "That address isn't allowed for a GitLab instance.")
            {
                Details = [$"baseUrl: {ex.Message}"],
            });
        }
        catch (HttpRequestException)
        {
            return Upstream($"{baseUrl.Host} could not be resolved.");
        }
        catch (GitLabApiException)
        {
            return Upstream($"{baseUrl.Host} couldn't be reached.");
        }
    }

    private async Task<Dictionary<Guid, (int Connections, int Projects)>> CountsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var connections = await db.GitConnections.AsNoTracking()
            .Where(c => ids.Contains(c.InstanceId))
            .GroupBy(c => c.InstanceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var projects = await db.ProjectGitSettings.AsNoTracking()
            .Where(s => s.InstanceId != null && ids.Contains(s.InstanceId.Value))
            .GroupBy(s => s.InstanceId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return ids.ToDictionary(id => id, id => (connections.GetValueOrDefault(id), projects.GetValueOrDefault(id)));
    }

    private static AdminGitLabInstanceDto ToDto(GitLabInstance instance, IReadOnlyDictionary<Guid, (int Connections, int Projects)> counts)
    {
        var (connections, projects) = counts.GetValueOrDefault(instance.Id);
        return new AdminGitLabInstanceDto(
            instance.Id,
            instance.Name,
            instance.BaseUrl.AbsoluteUri.TrimEnd('/'),
            instance.IsGitLabCom,
            instance.Source,
            instance.ClientId,
            instance.HasClientSecret,
            instance.OAuthConfigured,
            instance.AllowInsecureHttp,
            instance.Enabled,
            instance.Version,
            GitLabRules.CheckVersion(instance.Version),
            GitLabRules.VersionWarning(instance.Version),
            connections,
            projects,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    private static Result<AdminGitLabInstanceDto> Conflict(string message) =>
        Result.Failure<AdminGitLabInstanceDto>(new DomainError(ErrorCodes.Conflict, message));

    private static Result<string?> Upstream(string detail) =>
        Result.Failure<string?>(new DomainError(ErrorCodes.UpstreamError, "The GitLab instance couldn't be reached.") { Details = [detail] });

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab instance {InstanceId} added ({BaseUrl})")]
    private static partial void LogAdded(ILogger logger, Guid instanceId, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab instance {InstanceId} updated (enabled {Enabled})")]
    private static partial void LogUpdated(ILogger logger, Guid instanceId, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab instance {InstanceId} removed with {Connections} connections")]
    private static partial void LogRemoved(ILogger logger, Guid instanceId, int connections);
}
