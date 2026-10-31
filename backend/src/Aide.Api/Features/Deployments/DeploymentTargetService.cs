using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Jobs;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>Payload of the <c>deployment_setup</c> job (FR-144): the target and what to do (create with name/region/plan, or link).</summary>
public sealed record DeploymentSetupPayload(Guid ProjectId, Guid TargetId, bool Create, string? Name, string? Region, string? Plan, string? ResourceId);

/// <summary>
/// FR-143–FR-146: Settings → Deployment. Targets come from the composition of the project's current version (one card per
/// component with a host); setting one up checks what needs no platform call and queues a <c>deployment_setup</c> job
/// (progress on the target); verify runs the readiness checklist; the inventory and origin model come from the composition.
/// </summary>
public sealed partial class DeploymentTargetService(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentInventoryService inventory,
    DeploymentEnvSync envSync,
    Databases.ManagedDatabaseService databaseCards,
    Git.GitHubRepoAccess github,
    IGitHubAppStore apps,
    Infrastructure.Git.IWorkingCopies workingCopies,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<DeploymentTargetService> logger)
{
    public const string StepQueued = "queued";
    public const string StepCreating = "creating";
    public const string StepLinking = "linking";
    public const string StepSyncingEnv = "syncing_env";

    private static readonly DeploymentComponent[] Components = [DeploymentComponent.Frontend, DeploymentComponent.Backend];

    /// <summary>FR-144: Settings → Deployment as a member sees it.</summary>
    public async Task<DeploymentSettingsDto> GetAsync(Guid projectId, ProjectRole? callerRole, CancellationToken cancellationToken)
    {
        var facts = await inventory.FactsAsync(projectId, cancellationToken);
        var rows = await inventory.RowsAsync(projectId, facts, cancellationToken);
        var composition = facts.Composition;
        var targets = new List<DeploymentTargetDto>();
        foreach (var component in Components)
        {
            var row = facts.Targets.FirstOrDefault(t => t.Component == component);
            if (row is null && DeploymentPlatforms.ProviderOf(composition, component) is null)
            {
                continue;
            }

            targets.Add(await ToDtoAsync(projectId, component, row, facts, rows, callerRole == ProjectRole.Owner, null, cancellationToken));
        }

        var databases = await databaseCards.CardsAsync(projectId, new Databases.StackCompositionView(composition?.Deploy.DatabaseHost), callerRole, cancellationToken);
        return new DeploymentSettingsDto(
            targets,
            databases,
            new DeploymentEnvInventoryDto(facts.PublicPrefix, [.. rows.Select(DeploymentInventoryService.ToDto)]),
            new DeploymentOriginModelDto(
                composition?.OriginModel,
                OriginModels.ToPackVocabulary(composition?.OriginModel),
                composition?.Derived.OriginModelOptions ?? [],
                OriginModels.Explanation(composition?.OriginModel)),
            callerRole == ProjectRole.Owner,
            access.UsesFake);
    }

    /// <summary>One target card; <paramref name="checkedItems"/> replaces the local readiness after a verify.</summary>
    private async Task<DeploymentTargetDto> ToDtoAsync(
        Guid projectId,
        DeploymentComponent component,
        DeploymentTarget? row,
        DeploymentFacts facts,
        IReadOnlyList<EnvInventoryRow> rows,
        bool canBind,
        IReadOnlyList<ReadinessItem>? checkedItems,
        CancellationToken cancellationToken)
    {
        var composed = DeploymentPlatforms.ProviderOf(facts.Composition, component);
        var provider = row?.Provider ?? composed!.Value;
        var inComposition = composed is { } c && (c == provider || provider == DeploymentProvider.Fake);
        var derived = DeploymentBuildRules.Derive(facts.Composition, component, provider, facts.Stack?.MigrateCommand);
        var config = row is { ResourceId: not null } ? row.BuildConfig : derived.Config;

        DeploymentTargetBindingDto? binding = null;
        PlatformConnection? connection = null;
        if (row?.ConnectionId is { } connectionId)
        {
            connection = await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken);
            var user = connection is null ? null
                : await db.Users.AsNoTracking().Where(u => u.Id == connection.UserId).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(cancellationToken);
            if (connection is not null && user is not null)
            {
                binding = new DeploymentTargetBindingDto(
                    connection.Id, connection.Label, connection.Last4, connection.Metadata.TeamSlug ?? connection.AccountName, new DeploymentUserDto(user.Id, user.Name), connection.Status);
            }
        }

        var caps = DeploymentPlatforms.Capabilities(provider);
        var items = checkedItems ?? ReadinessRules.Checklist(new ReadinessInput(
            provider,
            Checked: false,
            ConnectionState(provider, connection),
            ConnectionFixUrl(provider),
            null,
            null,
            null,
            EnvInventoryRules.MissingRequired(rows, component),
            provider == DeploymentProvider.GithubPages ? BranchControlState.NotApplicable : BranchControlState.Unknown,
            BranchControlPath(provider, row?.RootDirectory ?? derived.RootDirectory),
            PreviewReady(row)));
        return new DeploymentTargetDto(
            component,
            provider,
            DeploymentPlatforms.HostOption(provider),
            inComposition,
            DeploymentPlatforms.NeedsConnection(provider),
            binding,
            canBind,
            row?.ResourceId,
            row?.Name,
            row?.RootDirectory ?? derived.RootDirectory,
            new DeploymentBuildConfigDto(config.Framework, config.BuildCommand, config.OutputDirectory, config.StartCommand, config.DockerfilePath, config.HealthCheckPath, config.PreDeployCommand),
            row?.ProductionUrl,
            row?.PreviewUrl,
            row?.Status ?? DeploymentTargetStatus.NotConfigured,
            row?.StatusReason,
            row is null ? null : await SetupAsync(projectId, row, facts, cancellationToken),
            new DeploymentCapabilitiesDto(caps.ShaPinned, caps.Previews, caps.Rollback, caps.PerEnvironmentVariables, caps.RuntimeSecrets),
            [.. items.Select(i => new ReadinessItemDto(i.Id, i.State, i.Message, i.FixUrl))],
            DeploymentPlatforms.ImplementedForReal(provider),
            access.UsesFake);
    }

    /// <summary>The target's setup progress from its row and its newest <c>deployment_setup</c> job.</summary>
    private async Task<DeploymentSetupDto?> SetupAsync(Guid projectId, DeploymentTarget row, DeploymentFacts facts, CancellationToken cancellationToken)
    {
        var job = await LatestSetupJobAsync(projectId, row.Id, cancellationToken);
        if (row.Status == DeploymentTargetStatus.Configuring)
        {
            return new DeploymentSetupDto(job?.Id, job?.Status == JobStatus.Running ? "running" : "queued", row.StatusReason ?? StepQueued, null, null, null, job?.CreatedAt, null);
        }

        if (job is null)
        {
            return null;
        }

        if (row.Status == DeploymentTargetStatus.Error)
        {
            var code = row.StatusReason ?? ErrorCodes.PlatformError;
            return new DeploymentSetupDto(
                job.Id, "failed", null, code, job.LastError, await FixUrlAsync(row.Provider, code, DeploymentPlatforms.VcsHost(facts.Composition), cancellationToken), job.CreatedAt, job.CompletedAt);
        }

        return new DeploymentSetupDto(job.Id, "succeeded", null, null, null, null, job.CreatedAt, job.CompletedAt);
    }

    private async Task<Job?> LatestSetupJobAsync(Guid projectId, Guid targetId, CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup)
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(j => TryPayload(j.Payload)?.TargetId == targetId);
    }

    /// <summary>The fix link of a setup or readiness failure: the platform's Git connect link, or the AIDE GitHub App's page.</summary>
    internal async Task<string?> FixUrlAsync(DeploymentProvider provider, string code, string? vcsHost, CancellationToken cancellationToken) => code switch
    {
        ErrorCodes.PlatformGitNotConnected => DeploymentPlatforms.GitConnectUrl(provider, vcsHost),
        ErrorCodes.PlatformPermissionMissing => await AppSettingsUrlAsync(cancellationToken),
        ErrorCodes.CredentialInvalid or ErrorCodes.ConnectionScopeInsufficient => ConnectionFixUrl(provider),
        _ => null,
    };

    /// <summary>FR-143: the AIDE GitHub App's page (its permissions are updated from there), or <c>null</c> without an App.</summary>
    internal async Task<string?> AppSettingsUrlAsync(CancellationToken cancellationToken) =>
        await apps.GetAsync(cancellationToken) is { } app ? GitHubRules.AppHtmlUrl(app.Slug) : null;

    private static ReadinessConnection ConnectionState(DeploymentProvider provider, PlatformConnection? connection)
    {
        if (!DeploymentPlatforms.NeedsConnection(provider))
        {
            return ReadinessConnection.Valid;
        }

        return connection?.Status switch
        {
            null => ReadinessConnection.Missing,
            PlatformConnectionStatus.Valid => ReadinessConnection.Valid,
            PlatformConnectionStatus.Invalid => ReadinessConnection.Invalid,
            PlatformConnectionStatus.InsufficientScope => ReadinessConnection.InsufficientScope,
            _ => ReadinessConnection.Unchecked,
        };
    }

    /// <summary>Account → Connections, where platform tokens are added (relative to the web app).</summary>
    private static string? ConnectionFixUrl(DeploymentProvider provider) =>
        DeploymentPlatforms.NeedsConnection(provider) ? "/account/connections" : null;

    /// <summary>The branch-control file AIDE reads (Vercel <c>vercel.json</c>, Netlify <c>netlify.toml</c>); <c>null</c> where the platform manages it.</summary>
    internal static string? BranchControlPath(DeploymentProvider provider, string rootDirectory) => provider switch
    {
        DeploymentProvider.Vercel or DeploymentProvider.Fake => VercelConfigRules.ConfigPath(rootDirectory),
        DeploymentProvider.Netlify => rootDirectory is "" or "." ? "netlify.toml" : $"{rootDirectory}/netlify.toml",
        _ => null,
    };

    /// <summary>The preview environment from AIDE's data: per-branch previews (Vercel, Netlify, Cloudflare) once the resource exists; else a preview resource.</summary>
    private static bool? PreviewReady(DeploymentTarget? row) => row?.ResourceId is null ? null
        : row.Provider is DeploymentProvider.Render or DeploymentProvider.Railway ? row.PreviewResourceId is not null || row.PreviewUrl is not null
        : true;

    public static DeploymentSetupPayload? TryPayload(string payload)
    {
        try
        {
            return AideJson.Deserialize<DeploymentSetupPayload>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
