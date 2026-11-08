using System.Security.Cryptography;
using Aide.Api.Auth;
using Aide.Api.Features.Connections;
using Aide.Api.Features.Databases;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Api.Features.Stack;
using Aide.Api.Features.Trackers;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Stack;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Aide.Infrastructure.Stack;
using Aide.Infrastructure.Trackers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Seed;

/// <summary>DEP-2 dev seed settings (section <c>Seed</c>). <c>Seed:Enabled</c> is true in appsettings.Development.json
/// and set by the Kubernetes overlay; it is off everywhere else.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }
}

/// <summary>
/// DEP-2, DEP-V2 (§9): when <c>Seed:Enabled</c>, after the migrations have been applied (skipped while any are pending),
/// idempotently creates the Admin <c>admin@aide.local</c> with a generated password, logged once at Warning (the
/// only secret ever logged, so the operator can sign in), and a demo project using the Fake LLM provider and Local Git,
/// on the stack <c>fe-react-vite</c> + <c>be-aspnet</c> + <c>data-efcore-postgres</c> + <c>vcs-github</c> (§9 0.4.0), owned by
/// that Admin and <b>released at <see cref="DemoReleasedVersion"/></b> (its Local repository tagged
/// <c>v0.1.0</c>), so "Start new version" (FR-60) can be tried at once. An existing account with the seed email is left untouched (its
/// password is never reset); other Admins don't matter. The demo project is created once per database: a
/// <see cref="EventTypes.DemoProjectSeeded"/> marker event records it, so deleting the demo doesn't bring it back.
/// <para>
/// §9 (0.2.0, migrated in 0.4.0): a second demo project, <see cref="DemoPresetProjectName"/>, seeded as the RetirePresets migration
/// converts a <see cref="DemoPresetId"/> project (FR-129, §4.23): <c>fe-nextjs</c> at <c>.</c> + <c>vcs-github</c>, with the
/// retired preset kept as history (the "retired, migrated" note). It starts in <b>planning</b>: its project request and
/// specification (with the composition's sections) are seeded locked, and the plan generation job is queued, so the Fake's
/// scripted specialist plan appears as a draft. Its own marker (<see cref="EventTypes.DemoPresetProjectSeeded"/>) means
/// databases seeded before it get it on the next start (and a database that had it is converted by the migration).
/// </para>
/// <para>
/// §9 (0.4.0): a third demo project, <see cref="DemoBaasProjectName"/>, on <c>fe-vue</c> + <c>data-supabase</c> (BaaS), starts in
/// <b>specification</b>: its project request is locked and the spec generation job is queued. Marker:
/// <see cref="EventTypes.DemoStackProjectSeeded"/>.
/// </para>
/// <para>
/// §9 (0.5.0): a fourth demo project, <see cref="DemoAnalysisProjectName"/>, infers its stack: it is in <b>analysis</b> with a
/// <c>stack_profile</c> draft holding one <c>unknown</c> layer, and a completed Fake tracker import (the version's
/// <c>source_items</c> v1). Marker: <see cref="EventTypes.DemoAnalysisProjectSeeded"/>.
/// </para>
/// <para>
/// §9 (0.4.0), only where deployments and databases are both Fake (<see cref="SeedFakeHostsAsync"/>): the released demo is
/// hosted (Netlify, Render, Render Postgres) and the BaaS demo gets its Fake Supabase database, each set up through the same
/// services as Settings → Deployment with the seed Admin's own Fake connections. With real providers they stay hostless.
/// </para>
/// <para>
/// DEP-V2: on a database seeded under 0.1.0, the demo project (still in refinement) is upgraded once to released 0.1.0 when
/// it is untouched (<see cref="UpgradeV1DemoAsync"/>), with its own marker (<see cref="EventTypes.DemoProjectReleaseUpgraded"/>).
/// </para>
/// </summary>
public sealed partial class DevSeeder(IServiceScopeFactory scopes, ILogger<DevSeeder> logger) : IHostedService
{
    public const string AdminEmail = "admin@aide.local";
    public const string AdminName = "Tevrin Admin";
    public const string DemoProjectName = "Demo: Todo API";
    public const int PasswordLength = 20;

    /// <summary>DEP-V2: the version the demo project is seeded as released at.</summary>
    public const string DemoReleasedVersion = "0.1.0";

    /// <summary>The <see cref="EventTypes.DemoProjectReleaseUpgraded"/> outcome when the 0.1.0 demo was moved to released 0.1.0.</summary>
    public const string UpgradeReleased = "released";

    /// <summary>The upgrade outcome when the demo project no longer exists.</summary>
    public const string UpgradeSkippedMissing = "skipped_missing";

    /// <summary>The upgrade outcome when the demo project was used or edited (or is already released).</summary>
    public const string UpgradeSkippedTouched = "skipped_touched";

    /// <summary>The reason recorded on the demo's seeded transition into release.</summary>
    public const string DemoReleasedReason = "Demo project seeded as released 0.1.0";

    /// <summary>§9: the second demo project, migrated from a retired preset, in planning.</summary>
    public const string DemoPresetProjectName = "Demo: Recipe Finder";

    /// <summary>§9, FR-129: the retired preset the second demo project was migrated from (kept as history).</summary>
    public const string DemoPresetId = "nextjs-frontend";

    /// <summary>The last shipped version of <see cref="DemoPresetId"/>.</summary>
    public const string DemoPresetVersion = "1.0.0";

    /// <summary>The reason recorded on the preset demo's seeded transition into planning.</summary>
    public const string DemoPresetReason = "Demo project seeded in planning, migrated from the retired nextjs-frontend preset";

    /// <summary>§9 (0.4.0): the third demo project, Vue with Supabase as a backend-as-a-service, in specification.</summary>
    public const string DemoBaasProjectName = "Demo: Vue Notes";

    /// <summary>The reason recorded on the BaaS demo's seeded transition into specification.</summary>
    public const string DemoBaasReason = "Demo project seeded in specification on fe-vue + data-supabase (BaaS)";

    /// <summary>§9 (0.5.0): the fourth demo project, inferring its stack, in analysis with a draft profile and a Fake tracker import.</summary>
    public const string DemoAnalysisProjectName = "Demo: Weather Board";

    public const string DemoAnalysisIdea =
        "A small weather dashboard: people save a few cities and see the current conditions and a five-day forecast for each, from a public weather API. Units switch between metric and imperial.";

    /// <summary>§9 (0.5.0): the Fake Jira items the analysis demo's import holds (an epic and four stories).</summary>
    public static IReadOnlyList<string> DemoImportKeys { get; } = ["FAKE-1", "FAKE-2", "FAKE-3", "FAKE-4", "FAKE-5"];

    public const string DemoBaasIdea =
        "A Vue single-page app for personal notes backed by Supabase: users sign in with email, create, edit, tag and delete their own notes, and search them. Row level security keeps every user's notes private.";

    public const string DemoPresetIdea =
        "A Next.js web app to search recipes from a public recipe API, view a recipe's ingredients and steps, and keep a list of favourites in the browser. There is no database of its own: the app calls the recipe API through backend-for-frontend route handlers, and tests use mocked API responses.";

    public const string DemoIdea =
        "A small REST API for a personal todo list. Users can create, list, update, complete and delete todos; each todo has a title, optional notes, an optional due date and a done flag. Lists can be filtered by done/not done and sorted by due date. Data is stored in a relational database, and the API is documented with OpenAPI.";

    // No look-alike characters (0/O, 1/l/I).
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>().Value.Enabled)
        {
            return;
        }

        try
        {
            await SeedAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            // A seed failure must not stop the API (the readiness check reports database problems).
            LogSeedFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs the seed once against the scope's database, in one transaction holding the account advisory lock (so
    /// concurrent replicas and registrations can't race it). Public for tests.
    /// </summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var db = services.GetRequiredService<AideDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending > 0)
        {
            LogPendingMigrations(logger, pending);
            return;
        }

        var accounts = services.GetRequiredService<AccountService>();
        string? newPassword = null;
        Guid? demoProjectId = null;
        Guid? presetProjectId = null;
        Guid? baasProjectId = null;
        Guid? analysisProjectId = null;
        Guid? upgradedProjectId = null;
        AppUser? admin;
        var usesFakeHosts = UsesFakeHosts(services);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await accounts.LockAccountsAsync(cancellationToken);

            admin = await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(AdminEmail);
            if (admin is null)
            {
                (admin, newPassword) = await CreateAdminAsync(services, cancellationToken);
                if (admin is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return;
                }
            }

            if (!await DemoAlreadySeededAsync(services, db, admin, cancellationToken))
            {
                demoProjectId = await CreateDemoProjectAsync(services, db, admin, usesFakeHosts, cancellationToken);
            }
            else if (!await db.Events.AnyAsync(e => e.Type == EventTypes.DemoProjectReleaseUpgraded, cancellationToken))
            {
                upgradedProjectId = await UpgradeV1DemoAsync(services, db, admin, cancellationToken);
            }

            if (!await db.Events.AnyAsync(e => e.Type == EventTypes.DemoPresetProjectSeeded, cancellationToken))
            {
                presetProjectId = await CreatePresetDemoProjectAsync(services, db, admin, cancellationToken);
            }

            if (!await db.Events.AnyAsync(e => e.Type == EventTypes.DemoStackProjectSeeded, cancellationToken))
            {
                baasProjectId = await CreateBaasDemoProjectAsync(services, db, admin, cancellationToken);
            }

            if (!await db.Events.AnyAsync(e => e.Type == EventTypes.DemoAnalysisProjectSeeded, cancellationToken))
            {
                analysisProjectId = await CreateAnalysisDemoProjectAsync(services, db, admin, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (newPassword is not null)
        {
            LogAdminSeeded(logger, AdminEmail, newPassword);
        }

        // §9: after the commit, as the setup services hold their own transaction and project lock.
        if (usesFakeHosts && admin is not null)
        {
            await SeedFakeHostsAsync(services, admin.Id, demoProjectId, baasProjectId, cancellationToken);
        }

        if (demoProjectId is { } projectId)
        {
            LogDemoProject(logger, projectId);
        }

        if (presetProjectId is { } presetProject)
        {
            LogDemoProject(logger, presetProject);
        }

        if (baasProjectId is { } baasProject)
        {
            LogDemoProject(logger, baasProject);
        }

        if (analysisProjectId is { } analysisProject)
        {
            LogDemoProject(logger, analysisProject);
        }

        if (upgradedProjectId is { } upgraded)
        {
            LogDemoUpgraded(logger, upgraded, DemoReleasedVersion);
        }
    }

    /// <summary>
    /// DEP-V2 (§9), once per database (its <see cref="EventTypes.DemoProjectReleaseUpgraded"/> marker is written whatever
    /// the outcome): a demo project seeded under 0.1.0 sits in refinement; when it is <b>untouched</b> (<see cref="IsUntouchedAsync"/>)
    /// it is moved to released 0.1.0 exactly as a fresh seed does (<see cref="MarkDemoReleasedAsync"/>: Local repository
    /// tagged <c>v0.1.0</c>, the release version released, a succeeded release, the project released). A demo anyone used
    /// (or one already released by a 0.2.0 seed) is left as it is. Returns the upgraded project, or <c>null</c>.
    /// </summary>
    private static async Task<Guid?> UpgradeV1DemoAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var projectId = await DemoProjectIdAsync(db, admin, cancellationToken);
        var project = projectId is { } id ? await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) : null;
        var outcome = project is null
            ? UpgradeSkippedMissing
            : await IsUntouchedAsync(db, project, admin, cancellationToken) ? UpgradeReleased : UpgradeSkippedTouched;
        if (outcome == UpgradeReleased)
        {
            await UseFakeLlmAsync(services, db, project!.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await MarkDemoReleasedAsync(services, db, project, cancellationToken);
        }

        db.AddEvent(
            EventTypes.DemoProjectReleaseUpgraded,
            projectId: null,
            new { projectId = project?.Id, outcome, version = outcome == UpgradeReleased ? DemoReleasedVersion : null },
            SystemActor.Name,
            services.GetRequiredService<TimeProvider>().GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return outcome == UpgradeReleased ? project!.Id : null;
    }

    /// <summary>The demo project: the one the <see cref="EventTypes.DemoProjectSeeded"/> marker names, else the seed Admin's project named <see cref="DemoProjectName"/>.</summary>
    private static async Task<Guid?> DemoProjectIdAsync(AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var payload = await db.Events.AsNoTracking()
            .Where(e => e.Type == EventTypes.DemoProjectSeeded)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("projectId", out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.TryGetGuid(out var id))
                {
                    return id;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a payload the seed wrote: fall back to the name.
            }
        }

        return await db.ProjectMembers
            .Where(m => m.UserId == admin.Id)
            .Join(db.Projects.Where(p => p.Name == DemoProjectName), m => m.ProjectId, p => p.Id, (m, p) => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// DEP-V2: "untouched" is conservative: the demo still looks exactly as the 0.1.0 seed created it.
    /// <list type="bullet">
    /// <item>Named <see cref="DemoProjectName"/>, in refinement / active at the initial checkpoint, never released, with the
    /// seed Admin as its only member (an Owner).</item>
    /// <item>One release version (the current one), in development, untagged, with no base commit.</item>
    /// <item>No artifacts, agent runs, gates, execution steps, review reports, releases, rollbacks or deployments; no jobs
    /// but notifications; no state transition but the creation one; and no project event but <c>PROJECT_CREATED</c> (so no
    /// settings, member, LLM or preset edits).</item>
    /// </list>
    /// Anything else means someone used it, and the upgrade leaves it alone.
    /// </summary>
    private static async Task<bool> IsUntouchedAsync(AideDbContext db, Project project, AppUser admin, CancellationToken cancellationToken)
    {
        var id = project.Id;
        if (project.Name != DemoProjectName
            || project.CurrentPhase != Phase.Refinement
            || project.Status != ProjectStatus.Active
            || project.Checkpoint != LifecycleState.Initial.Version
            || project.ReleasedVersion is not null)
        {
            return false;
        }

        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == id).Select(m => new { m.UserId, m.Role }).ToListAsync(cancellationToken);
        if (members.Count != 1 || members[0].UserId != admin.Id || members[0].Role != ProjectRole.Owner)
        {
            return false;
        }

        var versions = await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).ToListAsync(cancellationToken);
        if (versions.Count != 1
            || versions[0].Id != project.CurrentReleaseVersionId
            || versions[0].Status != ReleaseVersionStatus.InDevelopment
            || versions[0].Tag is not null
            || !string.IsNullOrWhiteSpace(versions[0].BaseCommitSha))
        {
            return false;
        }

        return !await db.Artifacts.AnyAsync(a => a.ProjectId == id, cancellationToken)
            && !await db.AgentRuns.AnyAsync(r => r.ProjectId == id, cancellationToken)
            && !await db.ApprovalGates.AnyAsync(g => g.ProjectId == id, cancellationToken)
            && !await db.ExecutionSteps.AnyAsync(s => s.ProjectId == id, cancellationToken)
            && !await db.ComplianceReports.AnyAsync(r => r.ProjectId == id, cancellationToken)
            && !await db.Releases.AnyAsync(r => r.ProjectId == id, cancellationToken)
            && !await db.RollbackEvents.AnyAsync(r => r.ProjectId == id, cancellationToken)
            && !await db.Deployments.AnyAsync(d => d.ProjectId == id, cancellationToken)
            && !await db.Jobs.AnyAsync(j => j.ProjectId == id && j.Type != JobTypes.Notification, cancellationToken)
            && !await db.StateTransitions.AnyAsync(t => t.ProjectId == id && t.FromPhase != null, cancellationToken)
            && !await db.Events.AnyAsync(e => e.ProjectId == id && e.Type != EventTypes.ProjectCreated, cancellationToken);
    }

    /// <summary>
    /// The demo project is seeded once per database: the <see cref="EventTypes.DemoProjectSeeded"/> marker (kept when
    /// the project is deleted) says so. A demo project from before the marker existed (named
    /// <see cref="DemoProjectName"/>, with the seed Admin as a member) counts too, and gets the marker.
    /// </summary>
    private static async Task<bool> DemoAlreadySeededAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        if (await db.Events.AnyAsync(e => e.Type == EventTypes.DemoProjectSeeded, cancellationToken))
        {
            return true;
        }

        var legacy = await db.ProjectMembers
            .Where(m => m.UserId == admin.Id)
            .Join(db.Projects.Where(p => p.Name == DemoProjectName), m => m.ProjectId, p => p.Id, (m, p) => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (legacy is null)
        {
            return false;
        }

        AddMarker(services, db, legacy.Value);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid> CreateDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, bool fakeHosts, CancellationToken cancellationToken)
    {
        var creator = services.GetRequiredService<ProjectCreator>();
        var created = await creator.CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoProjectName,
                Idea = DemoIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },

                // §9 (0.4.0): the released demo shows a composition.
                Stack = new StackCompositionRequest
                {
                    Frontend = new StackComponentSelection { Pack = "fe-react-vite" },
                    Backend = new StackComponentSelection { Pack = "be-aspnet" },
                    Data = new StackDataSelection { Pack = "data-efcore-postgres" },
                    Vcs = new StackVcsSelection { Pack = "vcs-github" },

                    // §9: Fake hosts, only where the Fake providers run them.
                    Deploy = fakeHosts
                        ? new StackDeploySelection { FrontendHost = "deploy-netlify", BackendHost = "deploy-render", DatabaseHost = "render-postgres" }
                        : null,
                },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The demo project is invalid: {created.Error!.Message}");
        }

        await UseFakeLlmAsync(services, db, created.Value.Id, cancellationToken);
        AddMarker(services, db, created.Value.Id);
        await db.SaveChangesAsync(cancellationToken);
        await MarkDemoReleasedAsync(services, db, created.Value, cancellationToken);
        return created.Value.Id;
    }

    /// <summary>§9: Fake hosts need both Fake providers: deployments (<c>Deployments:Provider=Fake</c>) and databases (<c>Databases:Provider=Fake</c>).</summary>
    private static bool UsesFakeHosts(IServiceProvider services) =>
        services.GetRequiredService<DeploymentAccess>().UsesFake && services.GetRequiredService<IDatabaseProviderResolver>().UsesFake;

    /// <summary>
    /// §9: sets up the Fake hosts of the demos created by this seed run, through the services behind Settings → Deployment
    /// (their jobs finish on the API's workers). Connections are the seed Admin's own, with Fake tokens. A refusal is logged
    /// and leaves a usable demo, whose card offers the setup.
    /// </summary>
    private async Task SeedFakeHostsAsync(IServiceProvider services, Guid adminId, Guid? releasedDemoId, Guid? baasDemoId, CancellationToken cancellationToken)
    {
        if (releasedDemoId is { } released)
        {
            var targets = services.GetRequiredService<DeploymentTargetService>();
            if (await FakeConnectionAsync(services, adminId, PlatformProvider.Netlify, cancellationToken) is { } netlify)
            {
                var (_, failure) = await targets.PutAsync(
                    released, DeploymentComponent.Frontend, new PutDeploymentTargetRequest { Provider = DeploymentProvider.Netlify, ConnectionId = netlify, Create = new CreateDeploymentResourceRequest { Name = "demo-todo-web" } }, adminId, true, SystemActor.Name, cancellationToken);
                LogIfRefused(released, failure?.Error);
            }

            if (await FakeConnectionAsync(services, adminId, PlatformProvider.Render, cancellationToken) is { } render)
            {
                var (_, failure) = await targets.PutAsync(
                    released, DeploymentComponent.Backend, new PutDeploymentTargetRequest { Provider = DeploymentProvider.Render, ConnectionId = render, Create = new CreateDeploymentResourceRequest { Name = "demo-todo-api" } }, adminId, true, SystemActor.Name, cancellationToken);
                LogIfRefused(released, failure?.Error);
                var (_, error) = await services.GetRequiredService<ManagedDatabaseService>().PutAsync(
                    released, DeploymentEnvironment.Production, new PutManagedDatabaseRequest { Provider = ManagedDatabaseProvider.Render, ConnectionId = render, Create = new CreateManagedDatabaseRequest() }, adminId, true, SystemActor.Name, cancellationToken);
                LogIfRefused(released, error);
            }
        }

        if (baasDemoId is { } baas && await FakeConnectionAsync(services, adminId, PlatformProvider.Supabase, cancellationToken) is { } supabase)
        {
            var (_, error) = await services.GetRequiredService<ManagedDatabaseService>().PutAsync(
                baas, DeploymentEnvironment.Production, new PutManagedDatabaseRequest { Provider = ManagedDatabaseProvider.Supabase, ConnectionId = supabase, Create = new CreateManagedDatabaseRequest { Region = "eu-west-2" } }, adminId, true, SystemActor.Name, cancellationToken);
            LogIfRefused(baas, error);
        }

        void LogIfRefused(Guid projectId, DomainError? refused)
        {
            if (refused is not null)
            {
                LogFakeHostRefused(logger, projectId, refused.Code);
            }
        }
    }

    /// <summary>The seed Admin's Fake connection of <paramref name="provider"/> (created through the connection service, which checks it with the Fake).</summary>
    private async Task<Guid?> FakeConnectionAsync(IServiceProvider services, Guid adminId, PlatformProvider provider, CancellationToken cancellationToken)
    {
        var created = await services.GetRequiredService<PlatformConnectionService>().CreateAsync(
            adminId,
            new CreatePlatformConnectionRequest { Provider = provider, Label = "Demo (Fake)", Token = $"fake_seed_{provider.ToWire()}" },
            cancellationToken);
        if (created.IsFailure)
        {
            LogFakeHostRefused(logger, adminId, created.Error!.Code);
            return null;
        }

        return created.Value.Id;
    }

    /// <summary>The demos always run offline on the Fake provider, whatever the system default is.</summary>
    private static async Task UseFakeLlmAsync(IServiceProvider services, AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var llm = await db.ProjectLlmSettings.FirstAsync(s => s.ProjectId == projectId, cancellationToken);
        if (llm.Provider != LlmProvider.Fake)
        {
            var catalog = services.GetRequiredService<ILlmProviderCatalog>();
            llm.Provider = LlmProvider.Fake;
            llm.DefaultModel = catalog.DefaultModel(LlmProvider.Fake);
            llm.ModelsByAgent = new Dictionary<string, string>(catalog.DefaultAgentModels(LlmProvider.Fake));
        }
    }

    /// <summary>
    /// §9, FR-129: the preset demo, created once as the RetirePresets migration converts a <see cref="DemoPresetId"/>
    /// project: the equivalent composition (<c>fe-nextjs</c> at <c>.</c>, no backend or data, <c>vcs-github</c>, no hosts) and
    /// the retired preset id and version kept as history. The project request (checkpoint 1) and the specification
    /// (checkpoint 2, the eleven sections plus the composition's) are locked, the project is put in planning at checkpoint 3
    /// with one seeded transition, and the plan generation job is queued in the same transaction (a seed shortcut, like the
    /// released demo). The marker is written even when the stack catalog didn't load (no retry on every start).
    /// </summary>
    private static async Task<Guid?> CreatePresetDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (services.GetRequiredService<IStackCatalog>().Catalog is null)
        {
            db.AddEvent(EventTypes.DemoPresetProjectSeeded, projectId: null, new { projectId = (Guid?)null, presetId = DemoPresetId, skipped = "stack catalog not loaded" }, SystemActor.Name, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var created = await services.GetRequiredService<ProjectCreator>().CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoPresetProjectName,
                Idea = DemoPresetIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },
                Stack = new StackCompositionRequest
                {
                    Frontend = new StackComponentSelection { Pack = "fe-nextjs", Path = "." },
                    Backend = new StackComponentSelection { Pack = StackIds.None },
                    Data = new StackDataSelection { Pack = StackIds.None },
                    Vcs = new StackVcsSelection { Pack = "vcs-github" },
                    Deploy = new StackDeploySelection { FrontendHost = StackIds.None, Local = StackIds.None },
                },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The preset demo project is invalid: {created.Error!.Message}");
        }

        var projectId = created.Value.Id;
        await UseFakeLlmAsync(services, db, projectId, cancellationToken);
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var versionId = project.CurrentReleaseVersionId!.Value;
        var version = await db.ReleaseVersions.FirstAsync(v => v.Id == versionId, cancellationToken);

        // FR-129: the retired preset stays on the version as history ("Preset nextjs-frontend@1.0.0 (retired, migrated)").
        version.PresetId = DemoPresetId;
        version.PresetVersion = DemoPresetVersion;
        const int planningCheckpoint = 3;
        AddLockedArtifact(db, project, versionId, ArtifactType.ProjectRequest, 1, PresetDemoRequest(), admin.Id, now);
        AddLockedArtifact(db, project, versionId, ArtifactType.TechnicalSpecification, 2, PresetDemoSpecification(await SpecSectionsAsync(services, version, cancellationToken)), admin.Id, now);
        EnterPhase(db, project, versionId, Phase.Planning, planningCheckpoint, DemoPresetReason, now);
        await services.GetRequiredService<JobEnqueuer>().EnqueueAsync(
            JobTypes.PlanGeneration,
            new LifecycleJobPayload(projectId, planningCheckpoint, LifecycleJobPayload.PhaseEntered),
            projectId,
            cancellationToken,
            now);
        db.AddEvent(EventTypes.DemoPresetProjectSeeded, projectId: null, new { projectId, presetId = DemoPresetId }, SystemActor.Name, now);
        await db.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    /// <summary>
    /// §9 (0.4.0): the BaaS demo, created once: <c>fe-vue</c> + <c>data-supabase</c> (BaaS) on Local Git, the project request
    /// locked (checkpoint 1), the project put in specification at checkpoint 2 with one seeded transition and the spec
    /// generation job queued (a seed shortcut). The marker is written even when the catalog didn't load.
    /// </summary>
    private static async Task<Guid?> CreateBaasDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (services.GetRequiredService<IStackCatalog>().Catalog is null)
        {
            db.AddEvent(EventTypes.DemoStackProjectSeeded, projectId: null, new { projectId = (Guid?)null, skipped = "stack catalog not loaded" }, SystemActor.Name, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var created = await services.GetRequiredService<ProjectCreator>().CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoBaasProjectName,
                Idea = DemoBaasIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },
                Stack = new StackCompositionRequest
                {
                    Frontend = new StackComponentSelection { Pack = "fe-vue" },
                    Backend = new StackComponentSelection { Pack = StackIds.None },
                    Data = new StackDataSelection { Pack = "data-supabase", Mode = "baas" },
                    Vcs = new StackVcsSelection { Pack = "vcs-github" },
                },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The BaaS demo project is invalid: {created.Error!.Message}");
        }

        var projectId = created.Value.Id;
        await UseFakeLlmAsync(services, db, projectId, cancellationToken);
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var versionId = project.CurrentReleaseVersionId!.Value;
        const int specificationCheckpoint = 2;
        AddLockedArtifact(db, project, versionId, ArtifactType.ProjectRequest, 1, BaasDemoRequest(), admin.Id, now);
        EnterPhase(db, project, versionId, Phase.Specification, specificationCheckpoint, DemoBaasReason, now);
        await services.GetRequiredService<JobEnqueuer>().EnqueueAsync(
            JobTypes.SpecGeneration,
            new LifecycleJobPayload(projectId, specificationCheckpoint, LifecycleJobPayload.PhaseEntered),
            projectId,
            cancellationToken,
            now);
        db.AddEvent(EventTypes.DemoStackProjectSeeded, projectId: null, new { projectId, packs = project.StackPacks }, SystemActor.Name, now);
        await db.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    /// <summary>
    /// §9 (0.5.0): the stack inference demo, created once on Local Git with the Fake LLM: its version infers its stack
    /// (<c>stack_source = infer</c>) from a small Vite + React skeleton committed to the Local repository, and is in <b>analysis</b>
    /// at checkpoint 1 with only its creation transition (into analysis), as a project created with "Infer" is. A
    /// <c>stack_profile</c> v1 draft holds one <c>unknown</c> layer (the backend, with the agent's question), so the Analysis page shows "Needs your input" and Approve is refused until it is answered. A completed Fake
    /// Jira import of <see cref="DemoImportKeys"/> (read from the Fake tracker in-process, whatever <c>Trackers:Provider</c> is) has
    /// written the locked <c>source_items</c> v1. No agent job is queued and no event but the marker is written, so nothing
    /// notifies. The marker is written even when the catalog didn't load.
    /// </summary>
    private static async Task<Guid?> CreateAnalysisDemoProjectAsync(IServiceProvider services, AideDbContext db, AppUser admin, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (services.GetRequiredService<IStackCatalog>().Catalog is null)
        {
            db.AddEvent(EventTypes.DemoAnalysisProjectSeeded, projectId: null, new { projectId = (Guid?)null, skipped = "stack catalog not loaded" }, SystemActor.Name, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var created = await services.GetRequiredService<ProjectCreator>().CreateAsync(
            new CreateProjectRequest
            {
                Name = DemoAnalysisProjectName,
                Idea = DemoAnalysisIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new CreateProjectGitRequest { Provider = GitProvider.Local },
            },
            admin.Id,
            SystemActor.Name,
            cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The analysis demo project is invalid: {created.Error!.Message}");
        }

        var projectId = created.Value.Id;
        await UseFakeLlmAsync(services, db, projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var baseCommit = await SkeletonCommitAsync(services, projectId, cancellationToken);

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var versionId = project.CurrentReleaseVersionId!.Value;
        var version = await db.ReleaseVersions.FirstAsync(v => v.Id == versionId, cancellationToken);

        // Preparing the working copy records the version's branch with a direct UPDATE, which moves its xmin: reload the
        // tracked version (from ProjectCreator) before changing it, or the save is refused as a concurrency conflict.
        await db.Entry(version).ReloadAsync(cancellationToken);
        version.StackSource = StackSource.Infer;
        version.BaseCommitSha = baseCommit;
        var checkpoint = LifecycleState.Initial.Version;

        // Rule 8, FR-178: like a project created with "Infer", the version starts in analysis: its creation transition (from no
        // phase) goes into analysis, and no refinement → analysis transition is recorded.
        project.CurrentPhase = Phase.Analysis;
        project.Status = PhaseOrder.StatusFor(Phase.Analysis);
        project.UpdatedAt = now;
        var creation = await db.StateTransitions.SingleAsync(t => t.ProjectId == projectId && t.ReleaseVersionId == versionId && t.FromPhase == null, cancellationToken);
        creation.ToPhase = Phase.Analysis;

        var profile = StackProfile.Render(AnalysisDemoNarrative, AnalysisDemoProfile(baseCommit));
        db.Artifacts.Add(new Artifact
        {
            ProjectId = projectId,
            ReleaseVersionId = versionId,
            Type = ArtifactType.StackProfile,
            Version = 1,
            Content = profile,
            ContentHash = ContentHash.Compute(profile),
            Status = ArtifactStatus.Draft,
            CheckpointAtCreation = checkpoint,
            CreatedByUserId = admin.Id,
            CreatedAt = now,
        });

        var importId = await AddDemoImportAsync(services, db, project, versionId, admin.Id, now, cancellationToken);
        db.AddEvent(EventTypes.DemoAnalysisProjectSeeded, projectId: null, new { projectId, importId }, SystemActor.Name, now);
        await db.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private const string AnalysisDemoNarrative =
        "## Findings\n\nSeeded for the demo: the frontend is React with Vite at the repository root (`package.json`, `index.html`, `src/main.tsx`). No server code was found, so the backend is left for you to answer.";

    /// <summary>The fixed id in the skeleton commit's <c>AIDE-Step-Id</c> trailer, so a second seed run finds the commit instead of adding one.</summary>
    private static readonly Guid DemoSkeletonCommitId = Guid.Parse("0199a5ee-d000-7000-8000-00000000c0de");

    /// <summary>§9 (0.5.0): the analysis demo's code: a minimal Vite + React app at the root, matching the draft profile.</summary>
    internal static IReadOnlyList<ProducedFile> DemoSkeletonFiles { get; } =
    [
        new("package.json", """
            {
              "name": "weather-board",
              "private": true,
              "version": "0.1.0",
              "type": "module",
              "scripts": { "dev": "vite", "build": "vite build", "preview": "vite preview" },
              "dependencies": { "react": "^19.0.0", "react-dom": "^19.0.0" },
              "devDependencies": { "@vitejs/plugin-react": "^5.0.0", "vite": "^7.0.0" }
            }

            """),
        new("index.html", """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="UTF-8" />
                <title>Weather Board</title>
              </head>
              <body>
                <div id="root"></div>
                <script type="module" src="/src/main.tsx"></script>
              </body>
            </html>

            """),
        new("src/main.tsx", """
            import { StrictMode } from 'react'
            import { createRoot } from 'react-dom/client'

            function App() {
              return <h1>Weather Board</h1>
            }

            createRoot(document.getElementById('root')!).render(
              <StrictMode>
                <App />
              </StrictMode>,
            )

            """),
    ];

    /// <summary>§9 (0.5.0): the analysis demo's profile: React with Vite at the root, the backend <c>unknown</c> with a question, no data layer, GitHub VCS.</summary>
    internal static StackProfileDocument AnalysisDemoProfile(string? baseCommitSha) => new()
    {
        BaseCommitSha = baseCommitSha,
        Layers = new StackProfileLayers(
            new StackProfileLayer
            {
                Option = "fe-react-vite",
                Path = StackPaths.Root,
                Status = StackProfileLayerStatus.Inferred,
                Confidence = StackProfileConfidence.High,
                Evidence = [new StackProfileEvidence("package.json", "react and vite"), new StackProfileEvidence("src/main.tsx", "the React root")],
            },
            StackProfileLayer.Unknown,
            new StackProfileLayer { Status = StackProfileLayerStatus.None, Confidence = StackProfileConfidence.Medium },
            new StackProfileLayer { Option = StackPinning.GitHubVcs, Status = StackProfileLayerStatus.Fixed, Confidence = StackProfileConfidence.High }),
        Questions = [new StackProfileQuestion(StackLayers.Backend, "No server code was found. Does the dashboard need a backend of its own (for example to keep the weather API key secret), or does it call the API directly?")],
    };

    /// <summary>
    /// §9 (0.5.0): a completed Fake Jira import of <see cref="DemoImportKeys"/> and the locked <c>source_items</c> version it wrote, as the
    /// import job would (normalised, children linked, rendered), but read from the Fake in-process, with no connection.
    /// </summary>
    private static async Task<Guid> AddDemoImportAsync(IServiceProvider services, AideDbContext db, Project project, Guid versionId, Guid adminId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var selection = new TrackerSelection { Keys = DemoImportKeys, ContainerId = FakeIssueTrackerProvider.DefaultContainer };
        var options = new TrackerImportOptions();
        var fake = new FakeIssueTrackerProvider(TrackerProvider.Jira, new FakeTrackerScript());
        var fetched = await fake.GetItemsAsync(new TrackerCredential("fake_seed"), selection, options, cancellationToken);
        var items = SourceItemLimits.LinkChildren([.. fetched.Select(i => SourceItemLimits.Normalize(i, options))]);
        var import = new TrackerImport
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            Provider = TrackerProvider.Jira,
            Query = selection,
            Options = options,
            Status = TrackerImportStatus.Completed,
            Items = items,
            ItemCount = items.Count,
            CreatedBy = adminId,
            CreatedAt = now,
            CompletedAt = now,
        };
        var rendered = SourceItemsRenderer.Render([new SourceImportSnapshot(import.Id, import.Provider, import.CreatedAt, items)]);
        var snapshot = await services.GetRequiredService<SourceSnapshotWriter>().AddAsync(project, versionId, rendered.Markdown, adminId, now, cancellationToken);
        import.RenderedBytes = rendered.ByteCount;
        import.SourceArtifactId = snapshot.Id;
        db.TrackerImports.Add(import);
        return import.Id;
    }

    /// <summary>
    /// §9 (0.5.0): commits <see cref="DemoSkeletonFiles"/> on the version's branch of the Local repository (both created now if absent,
    /// so the work of the version builds on it) and returns
    /// that commit, the version's base: the Stack Analyst (Retry, Reject) then analyses real code. Idempotent: the commit carries a
    /// fixed step trailer, so a repeated run returns it. <c>null</c> when the repository can't be created (no git CLI).
    /// </summary>
    private static async Task<string?> SkeletonCommitAsync(IServiceProvider services, Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            var prepared = await services.GetRequiredService<WorkingCopyPreparer>().PrepareAsync(projectId, cancellationToken);
            if (prepared.IsFailure)
            {
                return null;
            }

            var message = new StepCommitMessage(
                "Add the Weather Board skeleton (Vite + React)",
                $"Seeded by the Tevrin dev seed for the stack inference demo.\n\n{CodeGeneration.StepTrailerLine(DemoSkeletonCommitId)}");
            var committed = await services.GetRequiredService<IWorkingCopies>()
                .CommitStepAsync(projectId, prepared.Value.Branch, DemoSkeletonCommitId, DemoSkeletonFiles, message, cancellationToken);
            return committed.Sha;
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogAnalysisRepositoryFailed(services.GetRequiredService<ILogger<DevSeeder>>(), ex, projectId);
            return null;
        }
    }

    /// <summary>The seed shortcut into <paramref name="phase"/> at <paramref name="checkpoint"/>: one seeded transition from refinement.</summary>
    private static void EnterPhase(AideDbContext db, Project project, Guid versionId, Phase phase, int checkpoint, string reason, DateTimeOffset now)
    {
        project.CurrentPhase = phase;
        project.Status = PhaseOrder.StatusFor(phase);
        project.Checkpoint = checkpoint;
        project.UpdatedAt = now;
        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            FromPhase = Phase.Refinement,
            ToPhase = phase,
            Actor = SystemActor.Name,
            Reason = reason,
            Checkpoint = checkpoint,
            CreatedAt = now,
        });
    }

    /// <summary>FR-123: the required extra spec section titles of <paramref name="version"/>'s composition.</summary>
    private static async Task<IReadOnlyList<string>> SpecSectionsAsync(IServiceProvider services, ReleaseVersion version, CancellationToken cancellationToken)
    {
        var resolved = await services.GetRequiredService<VersionStackLoader>().ForVersionAsync(version, cancellationToken, readBase: false);
        return resolved.Stack is { } stack ? [.. StackGuidance.SpecSections(stack).Select(t => t.Title)] : [];
    }

    private static void AddLockedArtifact(AideDbContext db, Project project, Guid versionId, ArtifactType type, int checkpoint, string content, Guid authorId, DateTimeOffset now) =>
        db.Artifacts.Add(new Artifact
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            Type = type,
            Version = 1,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
            CheckpointAtCreation = checkpoint,
            CreatedByUserId = authorId,
            ApprovedBy = authorId,
            ApprovedAt = now,
            CreatedAt = now,
        });

    private static string PresetDemoRequest() =>
        $"# {DemoPresetProjectName}\n\n## Summary\n{DemoPresetIdea}\n\n## Users and roles\n- **Visitor**: searches recipes and keeps favourites in this browser.\n\n## Core features\n**Must**\n- Search recipes by name and list the results.\n- View a recipe's ingredients and steps.\n- Save and remove favourites (kept in the browser).\n- Show clear loading, empty and error states.\n\n## Out of scope\n- Accounts and a database of its own.\n";

    private static string BaasDemoRequest() =>
        $"# {DemoBaasProjectName}\n\n## Summary\n{DemoBaasIdea}\n\n## Users and roles\n- **User**: signs in and manages their own notes.\n\n## Core features\n**Must**\n- Sign in with email.\n- Create, edit, tag and delete notes.\n- Search notes by text and tag.\n- Nobody else can read a user's notes.\n\n## Out of scope\n- Sharing notes.\n";

    /// <summary>A valid specification (FR-123): the eleven sections, FR-1–FR-4 with criteria, and the composition's sections.</summary>
    private static string PresetDemoSpecification(IReadOnlyList<string> stackSections)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Overview"] = "Demo: Recipe Finder lets visitors search a public recipe API, view recipes and keep favourites in the browser.",
            ["Functional Requirements"] = "- **FR-1** Search recipes. Given a query, when the visitor searches, then matching recipes are listed.\n- **FR-2** List results with loading, empty and error states. Given no matches, when the search finishes, then an empty state is shown.\n- **FR-3** View a recipe. Given a recipe id, when it is opened, then its ingredients and steps are shown; an unknown id shows not found.\n- **FR-4** Favourites. Given a recipe, when the visitor saves it, then it appears in the favourites list after a reload.",
            ["Technical Architecture"] = "Next.js (App Router) with TypeScript strict and Tailwind; BFF route handlers under app/api call the recipe API.\n\n### Dependencies\n- next\n- react\n- react-dom\n- zod\n- vitest\n- @playwright/test",
            ["Data Models"] = "**Recipe**: `id` (string), `title` (string), `ingredients` (string[]), `steps` (string[]). **Favourite**: `recipeId` (string), `savedAt` (ISO timestamp).",
            ["API Contracts"] = "| Method | Path | Success | Errors |\n| --- | --- | --- | --- |\n| GET | /api/recipes | 200 list | 400, 502 |\n| GET | /api/recipes/{id} | 200 recipe | 404, 502 |",
            ["Feature Specifications"] = "Queries are trimmed (1–100 characters). Favourites live in localStorage.",
            ["Design System & UI/UX"] = "A search page and a recipe page; 44 px tap targets, WCAG AA contrast, works at 360 px wide.",
            ["Security & Authentication"] = "No accounts. The recipe API key, if any, stays server-side in route handlers.",
            ["Testing Strategy"] = "Vitest for the pure rules and route handlers (mocked upstream), Playwright for search, view and favourites.",
            ["Deployment & Infrastructure"] = "Vercel, from GitHub; environment variables per the Environment Variables section.",
            ["Open Questions"] = "- Which recipe API to use: default to a free public API with a server-side key.",
        };
        var sections = SpecSections.Required.Select((title, i) => $"## {i + 1}. {title}\n{bodies.GetValueOrDefault(title, title)}");
        var extra = stackSections.Select(title => $"## {title}\n{title} of {DemoPresetProjectName}: see the requirements and API contracts above.");
        return string.Join("\n\n", sections.Concat(extra)) + "\n";
    }

    /// <summary>
    /// DEP-V2: the demo starts released at 0.1.0. Its Local repository is created (initial commit on the default branch)
    /// and tagged <c>v0.1.0</c>; the first release version becomes <c>released</c> with that tag and commit, a succeeded
    /// release attempt is recorded, and the project is put in release / released with one seeded state transition (a seed
    /// shortcut: no agent work is faked). If the repository can't be created (no git CLI), the version is still released
    /// without a commit, so the database stays consistent.
    /// </summary>
    private static async Task MarkDemoReleasedAsync(IServiceProvider services, AideDbContext db, Project project, CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var version = await db.ReleaseVersions.FirstAsync(v => v.Id == project.CurrentReleaseVersionId, cancellationToken);
        var tag = ReleaseRecords.TagFor(version.TagPrefix, DemoReleasedVersion);
        string? commit = null;
        try
        {
            var prepared = await services.GetRequiredService<WorkingCopyPreparer>().PrepareAsync(project.Id, cancellationToken);
            if (prepared.IsSuccess)
            {
                var workingCopies = services.GetRequiredService<IWorkingCopies>();
                var settings = await db.ProjectGitSettings.AsNoTracking().FirstAsync(s => s.ProjectId == project.Id, cancellationToken);
                commit = await workingCopies.ResolveCommitAsync(project.Id, $"refs/heads/{settings.DefaultBranch}", cancellationToken);
                if (commit is not null)
                {
                    await workingCopies.CreateTagAsync(project.Id, tag, commit, ReleaseRules.TagMessage(project.Name, tag, DemoReleasedVersion), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogDemoRepositoryFailed(services.GetRequiredService<ILogger<DevSeeder>>(), ex, project.Id);
            commit = null;
        }

        await db.Entry(version).ReloadAsync(cancellationToken);
        version.Version = DemoReleasedVersion;
        version.Status = ReleaseVersionStatus.Released;
        version.Tag = tag;
        version.MergeCommitSha = commit;
        version.ReleasedAt = now;

        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id, cancellationToken);
        tracked.CurrentPhase = Phase.Release;
        tracked.Status = ProjectStatus.Released;
        tracked.ReleasedVersion = DemoReleasedVersion;
        tracked.DevelopmentVersion = null;
        tracked.UpdatedAt = now;
        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = project.Id,
            ReleaseVersionId = version.Id,
            FromPhase = Phase.Refinement,
            ToPhase = Phase.Release,
            Actor = SystemActor.Name,
            Reason = DemoReleasedReason,
            Checkpoint = tracked.Checkpoint,
            CreatedAt = now,
        });
        db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            ReleaseVersionId = version.Id,
            Checkpoint = tracked.Checkpoint,
            Tag = tag,
            MergeCommitSha = commit,
            Status = ReleaseStatus.Succeeded,
            Attempt = 1,
            CreatedAt = now,
            CompletedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddMarker(IServiceProvider services, AideDbContext db, Guid projectId) =>
        db.AddEvent(
            EventTypes.DemoProjectSeeded,
            projectId: null,
            new { projectId },
            SystemActor.Name,
            services.GetRequiredService<TimeProvider>().GetUtcNow());

    /// <summary>Creates the seed Admin (inside the caller's transaction) with a generated password.</summary>
    private async Task<(AppUser? Admin, string? Password)> CreateAdminAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var password = GeneratePassword();
        var admin = new AppUser
        {
            Email = AdminEmail,
            UserName = AdminEmail,
            Name = AdminName,
            SystemRole = SystemRole.Admin,
            EmailConfirmed = true,
            CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            LogAdminFailed(logger, string.Join("; ", result.Errors.Select(e => e.Code)));
            return (null, null);
        }

        return (admin, password);
    }

    /// <summary>20 random characters with at least one letter and one digit (the password policy).</summary>
    public static string GeneratePassword()
    {
        while (true)
        {
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);
            if (password.Any(char.IsLetter) && password.Any(char.IsDigit))
            {
                return password;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seeded admin {Email} with password {Password} (shown once; change it after signing in)")]
    private static partial void LogAdminSeeded(ILogger logger, string email, string password);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upgraded the untouched 0.1.0 demo project {ProjectId} to released {Version}")]
    private static partial void LogDemoUpgraded(ILogger logger, Guid projectId, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded demo project {ProjectId}")]
    private static partial void LogDemoProject(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed skipped: {Count} pending migration(s); apply them and restart")]
    private static partial void LogPendingMigrations(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dev seed could not create the admin account ({Errors})")]
    private static partial void LogAdminFailed(ILogger logger, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed could not create the Local repository of demo project {ProjectId}; it is released without a tagged commit")]
    private static partial void LogDemoRepositoryFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed could not commit the skeleton of demo project {ProjectId}; it is in analysis with no code to analyse")]
    private static partial void LogAnalysisRepositoryFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev seed couldn't set up a Fake host for {Id}: {Code}")]
    private static partial void LogFakeHostRefused(ILogger logger, Guid id, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dev seed failed")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
