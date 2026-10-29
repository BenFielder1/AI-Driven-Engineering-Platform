using Aide.Api.Errors;
using Aide.Api.Features.Stack;
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
/// FR-88: Settings → Deployment and the wizard's Deployment step. Vercel is asked first (outside any transaction): linking
/// reads the project (<c>GET /v9/projects</c>; it must be unlinked or linked to this repository), creating first adopts an
/// existing project of that name linked to this repository (so a retry is safe), else <c>POST /v11/projects</c>. Then the
/// binding, project and provider are saved under the project row lock (as LLM binding, FR-79). Vercel's failures map to
/// <c>VERCEL_GIT_NOT_CONNECTED</c> (with the install link), <c>VERCEL_PLAN_LIMIT</c>, <c>CREDENTIAL_INVALID</c> (the
/// connection becomes <c>invalid</c>), <c>NOT_FOUND</c> or <c>UPSTREAM_ERROR</c>.
/// </summary>
public sealed partial class DeploymentSettingsService(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentEnvSync envSync,
    VersionStackLoader stacks,
    TimeProvider time,
    ILogger<DeploymentSettingsService> logger)
{
    private static readonly string[] ProviderChange = ["provider"];

    /// <summary>FR-88: the settings as a member sees them.</summary>
    public async Task<DeploymentSettingsDto> GetAsync(Guid projectId, ProjectRole? callerRole, CancellationToken cancellationToken)
    {
        var settings = await DeploymentAccess.FrontendTargetAsync(db, projectId, cancellationToken);
        // §4.22: the required variables and deploy hosts come from the version's resolved composition.
        var stack = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } versionId
            ? (await stacks.ForVersionAsync(versionId, cancellationToken, readBase: false)).Stack
            : null;
        IReadOnlyList<string> deployProviders = stack is null
            ? []
            : [.. new[] { stack.Composition.Deploy.FrontendHost, stack.Composition.Deploy.BackendHost }
                .OfType<string>()
                .Select(h => h.StartsWith("deploy-", StringComparison.Ordinal) ? h["deploy-".Length..] : h)];
        var vars = await db.DeploymentEnvVars.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .Select(v => new { v.Key, v.Targets })
            .ToListAsync(cancellationToken);
        var present = vars.ToDictionary(v => v.Key, v => (IReadOnlyList<DeploymentEnvironment>)[.. Targets(v.Targets)], StringComparer.Ordinal);
        var required = (stack?.Env ?? [])
            .Select(e =>
            {
                IReadOnlyList<DeploymentEnvironment> targets = [.. Targets(e.Targets)];
                return new RequiredEnvVarDto(e.Key, targets, e.Sensitive, e.Description, DeploymentEnvRules.IsPresent(e.Key, targets, present));
            })
            .ToList();

        DeploymentBindingDto? binding = null;
        string? owner = null;
        if (settings?.ConnectionId is { } connectionId)
        {
            var connection = await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
            var user = connection is null
                ? null
                : await db.Users.AsNoTracking().Where(u => u.Id == connection.UserId).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(cancellationToken);
            if (connection is not null && user is not null)
            {
                var teamSlug = connection.Metadata.TeamSlug;
                binding = new DeploymentBindingDto(connection.Id, connection.Label, connection.Last4, teamSlug, new DeploymentUserDto(user.Id, user.Name), connection.Status);
                owner = teamSlug ?? connection.AccountName;
            }
        }

        var project = settings?.ResourceId is { } vercelId
            ? new VercelProjectDto(
                vercelId,
                settings.Name ?? vercelId,
                owner is null || access.UsesFake ? null : $"https://vercel.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(settings.Name ?? vercelId)}",
                settings.ProductionUrl)
            : null;
        return new DeploymentSettingsDto(
            settings?.Provider,
            DeploymentSettingsRules.Supported(deployProviders, settings is not null),
            binding,
            callerRole == ProjectRole.Owner,
            project,
            settings?.RootDirectory ?? DeploymentTarget.DefaultRootDirectory,
            settings?.BuildConfig.Framework ?? DeploymentTarget.DefaultFramework,
            required,
            project is null ? null : true,
            null);
    }

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
        var vercel = access.Provider;
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
            if (ex.Failure == VercelFailure.CredentialRejected)
            {
                await access.MarkInvalidAsync(connectionId, null, SystemActor.Name, time.GetUtcNow(), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            LogVercelFailed(logger, ex.Failure, ex.Status);
            return Result.Failure<PreparedDeployment>(new DomainError(VercelErrorRules.ErrorCodeFor(ex.Failure), Message(ex)) { Reason = ex.InstallUrl });
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
            await envSync.ResetAsync(projectId, cancellationToken);
        }

        DeploymentTargetRules.Refresh(settings);
        settings.UpdatedAt = now;
        if (changes.Count > 0)
        {
            db.AddEvent(EventTypes.SettingsUpdated, projectId, new { projectId, section = "deployment", changes }, actor, now);
        }
    }

    /// <summary>FR-88: turning deployment off removes the frontend target; deployments and env vars stay as history.</summary>
    public async Task TurnOffAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var settings = await db.DeploymentTargets.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Component == DeploymentComponent.Frontend, cancellationToken);
        if (settings is null)
        {
            return;
        }

        db.DeploymentTargets.Remove(settings);
        db.AddEvent(EventTypes.SettingsUpdated, projectId, new { projectId, section = "deployment", changes = ProviderChange, provider = (string?)null }, actor, time.GetUtcNow());
    }

    /// <summary>The problem for a failed <see cref="PrepareAsync"/>.</summary>
    public static ProblemHttpResult Problem(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code == ErrorCodes.VercelGitNotConnected ? ApiProblems.VercelGitNotConnected(error.Reason ?? VercelErrorRules.DefaultGitHubInstallUrl, error.Message) : ApiProblems.FromError(error with { Reason = null });
    }

    internal static IEnumerable<DeploymentEnvironment> Targets(IEnumerable<string> wire) =>
        wire.Select(t => EnumStrings.TryParse<DeploymentEnvironment>(t, out var target) ? (DeploymentEnvironment?)target : null).OfType<DeploymentEnvironment>();

    internal static string Message(DeploymentProviderException ex) => ex.Failure switch
    {
        VercelFailure.CredentialRejected => "Vercel rejected the connection's token; it is now marked invalid. Add a new token in Account → Connections.",
        VercelFailure.GitNotConnected => $"Vercel's GitHub App can't access this repository. Install it for the repository, then try again. ({ex.Message})",
        VercelFailure.PlanLimit => $"Vercel's plan doesn't allow this (a Hobby account can't deploy an organisation's repository). ({ex.Message})",
        VercelFailure.RateLimited => "Vercel's rate limit was reached; try again in a minute.",
        _ => $"Vercel failed: {ex.Message}",
    };

    private static Result<PreparedDeployment> Invalid(string field, string message) =>
        Result.Failure<PreparedDeployment>(new DomainError(ErrorCodes.Validation, "The deployment settings are invalid.") { Details = [$"{field}: {message}"] });

    [LoggerMessage(Level = LogLevel.Information, Message = "Vercel project {VercelProjectId} created")]
    private static partial void LogProjectCreated(ILogger logger, string vercelProjectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vercel refused the deployment settings: {Failure} (HTTP {Status})")]
    private static partial void LogVercelFailed(ILogger logger, VercelFailure failure, int? status);
}

/// <summary>
/// FR-88, §4.15 env sync: pushes env vars to Vercel, one Vercel var per (key, target): updates by stored id, creates batched
/// (upsert), and deletes the Vercel vars of dropped targets. Values are decrypted only to send them
/// (<c>SecretPurposes.DeploymentEnvValue</c>) and are never logged or returned. Changes are made on tracked entities; the
/// caller saves.
/// </summary>
public sealed partial class DeploymentEnvSync(AideDbContext db, DeploymentAccess access, TimeProvider time, ILogger<DeploymentEnvSync> logger)
{
    /// <summary>Pushes the given vars (tracked). Throws <see cref="DeploymentProviderException"/>; nothing is recorded then.</summary>
    public async Task PushAsync(DeploymentContext context, IReadOnlyList<DeploymentEnvVar> vars, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vars);
        if (vars.Count == 0 || context.Target.ResourceId is not { } projectId)
        {
            return;
        }

        var writes = new List<DeploymentEnvWrite>();
        var deletes = new List<(DeploymentEnvVar Var, EnvTargetDelete Delete)>();
        foreach (var v in vars)
        {
            if (!access.Values.TryUnprotect(v.ValueEncrypted, out var value))
            {
                v.SyncError = "The saved value can't be decrypted; save it again.";
                continue;
            }

            var plan = EnvSyncRules.Plan([.. DeploymentSettingsService.Targets(v.Targets)], v.VercelEnvIds);
            writes.AddRange(plan.Writes.Select(w => new DeploymentEnvWrite(v.Key, value, w.Target, v.Sensitive, w.ExistingVercelId)));
            deletes.AddRange(plan.Deletes.Select(d => (v, d)));
        }

        var written = await context.Provider.WriteEnvAsync(context.Auth, projectId, writes, cancellationToken);
        foreach (var (_, delete) in deletes)
        {
            await context.Provider.DeleteEnvAsync(context.Auth, projectId, delete.VercelId, cancellationToken);
        }

        var now = time.GetUtcNow();
        foreach (var v in vars.Where(v => writes.Any(w => w.Key == v.Key)))
        {
            v.VercelEnvIds = written.Where(w => w.Key == v.Key).ToDictionary(w => w.Target.ToWire(), w => w.Id, StringComparer.Ordinal);
            v.SyncedAt = now;
            v.SyncError = null;
        }

        LogPushed(logger, vars.Count, writes.Count, deletes.Count);
    }

    /// <summary>
    /// Before a deployment: pushes the vars not yet in Vercel (or whose last push failed). A failure is recorded on the vars
    /// (<c>syncError</c>) and logged, and the deployment goes ahead; a rejected token propagates.
    /// </summary>
    public async Task PushPendingAsync(DeploymentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pending = await db.DeploymentEnvVars
            .Where(v => v.ProjectId == context.Target.ProjectId && (v.SyncedAt == null || v.SyncError != null))
            .OrderBy(v => v.Key)
            .ToListAsync(cancellationToken);
        try
        {
            await PushAsync(context, pending, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure != VercelFailure.CredentialRejected)
        {
            foreach (var v in pending)
            {
                v.SyncError = DeploymentSettingsService.Message(ex);
            }

            LogPendingFailed(logger, ex.Failure, ex.Status);
        }
    }

    /// <summary>Another Vercel project: every var has to be pushed to it again.</summary>
    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        foreach (var v in await db.DeploymentEnvVars.Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken))
        {
            v.VercelEnvIds = null;
            v.SyncedAt = null;
            v.SyncError = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed {VarCount} environment variable(s) to Vercel ({Writes} writes, {Deletes} deletes)")]
    private static partial void LogPushed(ILogger logger, int varCount, int writes, int deletes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing pending environment variables to Vercel failed: {Failure} (HTTP {Status}); the deployment goes ahead")]
    private static partial void LogPendingFailed(ILogger logger, VercelFailure failure, int? status);
}
