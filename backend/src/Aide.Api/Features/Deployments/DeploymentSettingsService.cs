using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>The Vercel project a settings change links (an existing one, or one created for the repository).</summary>
public sealed record LinkedVercelProject(string Id, string Name, string RootDirectory);

/// <summary>A deployment choice checked with Vercel before it is saved (wizard or settings): the connection and project.</summary>
public sealed record PreparedDeployment(DeploymentProvider Provider, Guid ConnectionId, LinkedVercelProject? Project);

/// <summary>
/// FR-88: the new-project wizard's Deployment step (a Vercel frontend target; Settings → Deployment uses
/// <see cref="DeploymentTargetService"/> since FR-144). Vercel is asked first (outside any transaction): linking
/// reads the project (<c>GET /v9/projects</c>; it must be unlinked or linked to this repository), creating first adopts an
/// existing project of that name linked to this repository (so a retry is safe), else <c>POST /v11/projects</c>. Then the
/// binding, project and provider are saved under the project row lock (as LLM binding, FR-79). Vercel's failures map to
/// <c>PLATFORM_GIT_NOT_CONNECTED</c> (with the install link), <c>PLATFORM_PLAN_LIMIT</c>, <c>CREDENTIAL_INVALID</c> (the
/// connection becomes <c>invalid</c>), <c>NOT_FOUND</c> or <c>UPSTREAM_ERROR</c>.
/// </summary>
public sealed partial class DeploymentSettingsService(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentEnvSync envSync,
    TimeProvider time,
    ILogger<DeploymentSettingsService> logger)
{
    /// <summary>
    /// FR-88: checks the connection and Vercel project with Vercel (nothing saved). <paramref name="repoFullName"/> is the
    /// project's GitHub repository (<c>null</c> for a Local project under the Fake provider).
    /// </summary>
    public async Task<Result<PreparedDeployment>> PrepareAsync(
        DeploymentProvider provider,
        Guid connectionId,
        string? vercelProjectId,
        CreateVercelProjectRequest? create,
        string? repoFullName,
        string fieldPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fieldPrefix);
        var connection = await db.PlatformConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.Provider == PlatformProvider.Vercel, cancellationToken);
        if (connection is null)
        {
            return Result.Failure<PreparedDeployment>(new DomainError(ErrorCodes.NotFound, "The connection was not found."));
        }

        if (vercelProjectId is null && create is null)
        {
            return Result.Success(new PreparedDeployment(provider, connectionId, null));
        }

        if (!access.Tokens.TryUnprotect(connection.SecretEncrypted, out var token) || token.Length == 0)
        {
            return Result.Failure<PreparedDeployment>(new DomainError(ErrorCodes.CredentialInvalid, "The Vercel token can't be decrypted; add the connection again."));
        }

        var auth = new DeploymentAuth(token, connection.Metadata.TeamId);
        var vercel = access.Vercel;
        try
        {
            if (vercelProjectId is not null)
            {
                var found = await vercel.FindProjectAsync(auth, vercelProjectId, cancellationToken);
                if (found is null)
                {
                    return Result.Failure<PreparedDeployment>(new DomainError(ErrorCodes.NotFound, "The Vercel project was not found (with this connection's account or team)."));
                }

                if (!DeploymentSettingsRules.LinkMatches(found.LinkedRepo, repoFullName))
                {
                    return Invalid($"{fieldPrefix}vercelProjectId", $"The Vercel project is linked to {found.LinkedRepo}, not {repoFullName}.");
                }

                return Result.Success(new PreparedDeployment(provider, connectionId, new LinkedVercelProject(found.Id, found.Name, DeploymentTarget.DefaultRootDirectory)));
            }

            var root = string.IsNullOrEmpty(create!.RootDirectory) ? DeploymentTarget.DefaultRootDirectory : create.RootDirectory.TrimEnd('/');

            // A retry after a failure further on finds the project it created: adopt it when it is linked to this repository.
            var existing = await vercel.FindProjectAsync(auth, create.Name, cancellationToken);
            if (existing is not null && !access.UsesFake)
            {
                return DeploymentSettingsRules.LinkMatches(existing.LinkedRepo, repoFullName) && existing.LinkedRepo is not null
                    ? Result.Success(new PreparedDeployment(provider, connectionId, new LinkedVercelProject(existing.Id, existing.Name, root)))
                    : Invalid($"{fieldPrefix}create.name", "A Vercel project with this name exists already; link it instead or pick another name.");
            }

            var created = await vercel.CreateProjectAsync(
                auth, new CreateDeploymentProject(create.Name, repoFullName ?? create.Name, DeploymentTarget.DefaultFramework, root), cancellationToken);
            LogProjectCreated(logger, created.Id);
            return Result.Success(new PreparedDeployment(provider, connectionId, new LinkedVercelProject(created.Id, created.Name, root)));
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == DeploymentFailure.CredentialRejected)
            {
                await access.MarkInvalidAsync(connectionId, null, SystemActor.Name, time.GetUtcNow(), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            LogVercelFailed(logger, ex.Failure, ex.Status);
            return Result.Failure<PreparedDeployment>(new DomainError(VercelErrorRules.ErrorCodeFor(ex.Failure), Message(ex)) { Reason = ex.InstallUrl ?? (ex.Failure == DeploymentFailure.GitNotConnected ? VercelErrorRules.DefaultGitHubInstallUrl : null) });
        }
    }

    /// <summary>
    /// FR-88: saves a prepared choice (the caller is the binding Owner; the project row is locked by the caller's
    /// transaction). A different Vercel project resets the env vars' sync state so they are pushed to the new one.
    /// </summary>
    public async Task ApplyAsync(Guid projectId, Guid callerId, PreparedDeployment prepared, bool bindConnection, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var now = time.GetUtcNow();
        var settings = await db.DeploymentTargets.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Component == DeploymentComponent.Frontend, cancellationToken);
        var changes = new List<string>();
        if (settings is null)
        {
            settings = new DeploymentTarget
            {
                ProjectId = projectId,
                Component = DeploymentComponent.Frontend,
                BuildConfig = new DeploymentBuildConfig { Framework = DeploymentTarget.DefaultFramework },
                UpdatedAt = now,
            };
            db.DeploymentTargets.Add(settings);
            changes.Add("provider");
        }
        else if (settings.Provider != prepared.Provider)
        {
            changes.Add("provider");
        }

        settings.Provider = prepared.Provider;
        if (bindConnection && (settings.ConnectionId != prepared.ConnectionId || settings.BoundByUserId != callerId))
        {
            settings.ConnectionId = prepared.ConnectionId;
            settings.BoundByUserId = callerId;
            changes.Add("connection");
        }

        if (prepared.Project is { } project && project.Id != settings.ResourceId)
        {
            settings.ResourceId = project.Id;
            settings.Name = project.Name;
            settings.RootDirectory = project.RootDirectory;
            settings.ProductionUrl = null;
            changes.Add("vercelProject");
            await envSync.ResetAsync(projectId, DeploymentComponent.Frontend, cancellationToken);
        }

        DeploymentTargetRules.Refresh(settings);
        settings.UpdatedAt = now;
        if (changes.Count > 0)
        {
            db.AddEvent(EventTypes.SettingsUpdated, projectId, new { projectId, section = "deployment", changes }, actor, now);
        }
    }

    /// <summary>The problem for a failed <see cref="PrepareAsync"/> (FR-144 codes carry <c>platform</c> and the fix link).</summary>
    public static ProblemHttpResult Problem(DomainError error) => ApiProblems.Platform(error, DeploymentProvider.Vercel);

    internal static IEnumerable<DeploymentEnvironment> Targets(IEnumerable<string> wire) =>
        wire.Select(t => EnumStrings.TryParse<DeploymentEnvironment>(t, out var target) ? (DeploymentEnvironment?)target : null).OfType<DeploymentEnvironment>();

    internal static string Message(DeploymentProviderException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var platform = DeploymentPlatforms.DisplayName(ex.Platform);
        return ex.Failure switch
        {
            DeploymentFailure.CredentialRejected => $"{platform} rejected the connection's token; it is now marked invalid. Add a new token in Account → Connections.",
            DeploymentFailure.GitNotConnected => $"{platform} can't see this repository. Connect it in {platform}, then try again. ({ex.Message})",
            DeploymentFailure.PlanLimit => $"Your {platform} plan doesn't allow this. ({ex.Message})",
            DeploymentFailure.PermissionMissing => $"{platform}: a permission is missing. ({ex.Message})",
            DeploymentFailure.RateLimited => $"{platform}'s rate limit was reached; try again in a minute.",
            DeploymentFailure.NotSupported => ex.Message,
            _ => $"{platform} failed: {ex.Message}",
        };
    }

    private static Result<PreparedDeployment> Invalid(string field, string message) =>
        Result.Failure<PreparedDeployment>(new DomainError(ErrorCodes.Validation, "The deployment settings are invalid.") { Details = [$"{field}: {message}"] });

    [LoggerMessage(Level = LogLevel.Information, Message = "Vercel project {VercelProjectId} created")]
    private static partial void LogProjectCreated(ILogger logger, string vercelProjectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vercel refused the deployment settings: {Failure} (HTTP {Status})")]
    private static partial void LogVercelFailed(ILogger logger, DeploymentFailure failure, int? status);
}
