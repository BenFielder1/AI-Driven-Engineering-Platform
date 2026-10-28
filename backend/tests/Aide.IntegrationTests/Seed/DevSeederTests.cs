using Aide.Api.Seed;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.IntegrationTests.Seed;

/// <summary>DEP-2: the dev seed (Admin + demo project) is idempotent, runs only on a fully migrated database, and
/// only when <c>Seed:Enabled</c>. Each test uses its own database on the shared server.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DevSeederTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DEP2_Seeding_twice_creates_one_admin_and_one_demo_project()
    {
        await using var app = await AppAsync();

        await Task.WhenAll(SeedAsync(app), SeedAsync(app));
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var admin = await db.Users.SingleAsync(u => u.Email == DevSeeder.AdminEmail, Ct);
            Assert.Equal((SystemRole.Admin, DevSeeder.AdminName), (admin.SystemRole, admin.Name));
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoProjectName, Ct);
            Assert.Equal((DevSeeder.DemoProjectName, Phase.Release, ProjectStatus.Released, ApprovalMode.Advisory), (project.Name, project.CurrentPhase, project.Status, project.ApprovalMode));
            var owner = await db.ProjectMembers.SingleAsync(m => m.ProjectId == project.Id, Ct);
            Assert.Equal((admin.Id, ProjectRole.Owner), (owner.UserId, owner.Role));
            Assert.Equal(LlmProvider.Fake, (await db.ProjectLlmSettings.SingleAsync(s => s.ProjectId == project.Id, Ct)).Provider);
            Assert.Equal(GitProvider.Local, (await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == project.Id, Ct)).Provider);
            var marker = await db.Events.SingleAsync(e => e.Type == EventTypes.DemoProjectSeeded, Ct);
            Assert.Null(marker.ProjectId);
            Assert.Contains(project.Id.ToString(), marker.Payload, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task DEPV2_Demo_project_is_released_at_0_1_0_with_a_tagged_local_repository()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoProjectName, Ct);
            Assert.Equal(("0.1.0", (string?)null), (project.ReleasedVersion, project.DevelopmentVersion));
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == project.Id, Ct);
            Assert.Equal((ReleaseVersionStatus.Released, "0.1.0", "v0.1.0"), (version.Status, version.Version, version.Tag));
            Assert.Equal(version.Id, project.CurrentReleaseVersionId);
            Assert.NotNull(version.MergeCommitSha);
            var release = await db.Releases.SingleAsync(r => r.ProjectId == project.Id, Ct);
            Assert.Equal((ReleaseStatus.Succeeded, "v0.1.0", version.Id), (release.Status, release.Tag, release.ReleaseVersionId));

            // §9 (v4): the released demo shows a composition.
            Assert.Equal("be-aspnet", Domain.Stack.StackCompositionJson.Deserialize(version.StackComposition)!.Backend!.Pack);
            Assert.Equal(["fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github"], project.StackPacks.Take(4));

            // The tag exists in the Local repository, so starting 0.1.0 again would be TAG_EXISTS and 0.2.0 is possible.
            var git = app.Services.GetRequiredService<IWorkingCopies>();
            var tag = await git.TagAsync(project.Id, "v0.1.0", Ct);
            Assert.Equal(version.MergeCommitSha, tag?.Commit);
        });
    }

    [Fact]
    public async Task DEPV2_An_untouched_v1_demo_in_refinement_is_upgraded_once_to_released_0_1_0()
    {
        await using var app = await AppAsync();
        var projectId = await SeedV1DemoAsync(app);

        await SeedAsync(app);
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Id == projectId, Ct);
            Assert.Equal((Phase.Release, ProjectStatus.Released, "0.1.0", (string?)null), (project.CurrentPhase, project.Status, project.ReleasedVersion, project.DevelopmentVersion));
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == projectId, Ct);
            Assert.Equal((ReleaseVersionStatus.Released, "0.1.0", "v0.1.0"), (version.Status, version.Version, version.Tag));
            Assert.NotNull(version.MergeCommitSha);
            var release = await db.Releases.SingleAsync(r => r.ProjectId == projectId, Ct);
            Assert.Equal((ReleaseStatus.Succeeded, "v0.1.0"), (release.Status, release.Tag));
            Assert.Single(await db.StateTransitions.Where(t => t.ProjectId == projectId && t.Reason == DevSeeder.DemoReleasedReason).ToListAsync(Ct));
            var marker = await db.Events.SingleAsync(e => e.Type == EventTypes.DemoProjectReleaseUpgraded, Ct);
            Assert.Null(marker.ProjectId);
            Assert.Equal(DevSeeder.UpgradeReleased, Outcome(marker.Payload));
            Assert.Equal(1, await db.Projects.CountAsync(p => p.Name == DevSeeder.DemoProjectName, Ct));

            var tag = await app.Services.GetRequiredService<IWorkingCopies>().TagAsync(projectId, "v0.1.0", Ct);
            Assert.Equal(version.MergeCommitSha, tag?.Commit);
        });
    }

    [Fact]
    public async Task DEPV2_A_v1_demo_someone_used_is_left_in_refinement_and_the_upgrade_does_not_run_again()
    {
        await using var app = await AppAsync();
        var projectId = await SeedV1DemoAsync(app);
        await WithDbAsync(app, async db =>
        {
            db.AddEvent(EventTypes.ProjectUpdated, projectId, new { field = "approvalMode" }, "someone", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await SeedAsync(app);
        await WithDbAsync(app, db => db.Events.Where(e => e.ProjectId == projectId && e.Type == EventTypes.ProjectUpdated).ExecuteDeleteAsync(Ct));
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Id == projectId, Ct);
            Assert.Equal((Phase.Refinement, ProjectStatus.Active, (string?)null), (project.CurrentPhase, project.Status, project.ReleasedVersion));
            Assert.Equal(ReleaseVersionStatus.InDevelopment, (await db.ReleaseVersions.SingleAsync(v => v.ProjectId == projectId, Ct)).Status);
            Assert.False(await db.Releases.AnyAsync(r => r.ProjectId == projectId, Ct));
            var marker = await db.Events.SingleAsync(e => e.Type == EventTypes.DemoProjectReleaseUpgraded, Ct);
            Assert.Equal(DevSeeder.UpgradeSkippedTouched, Outcome(marker.Payload));
        });
    }

    [Fact]
    public async Task DEPV2_A_fresh_seed_is_not_upgraded_twice()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoProjectName, Ct);
            Assert.Single(await db.Releases.Where(r => r.ProjectId == project.Id).ToListAsync(Ct));
            Assert.Single(await db.StateTransitions.Where(t => t.ProjectId == project.Id && t.Reason == DevSeeder.DemoReleasedReason).ToListAsync(Ct));
            var marker = await db.Events.SingleAsync(e => e.Type == EventTypes.DemoProjectReleaseUpgraded, Ct);
            Assert.Equal(DevSeeder.UpgradeSkippedTouched, Outcome(marker.Payload));
        });
    }

    private static string? Outcome(string payload)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("outcome").GetString();
    }

    /// <summary>A database as the v1 seed left it: the seed Admin and the demo project in refinement, with its marker.</summary>
    private static async Task<Guid> SeedV1DemoAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var admin = new AppUser { Email = DevSeeder.AdminEmail, UserName = DevSeeder.AdminEmail, Name = DevSeeder.AdminName, SystemRole = SystemRole.Admin, EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow };
        Assert.True((await services.GetRequiredService<UserManager<AppUser>>().CreateAsync(admin, AideApiFactory.Password)).Succeeded);
        var created = await services.GetRequiredService<Api.Features.Projects.ProjectCreator>().CreateAsync(
            new Api.Features.Projects.CreateProjectRequest
            {
                Name = DevSeeder.DemoProjectName,
                Idea = DevSeeder.DemoIdea,
                ApprovalMode = ApprovalMode.Advisory,
                Git = new Api.Features.Projects.CreateProjectGitRequest { Provider = GitProvider.Local },
            },
            admin.Id,
            SystemActor.Name,
            Ct);
        Assert.True(created.IsSuccess);
        var db = services.GetRequiredService<AideDbContext>();
        db.AddEvent(EventTypes.DemoProjectSeeded, projectId: null, new { projectId = created.Value.Id }, SystemActor.Name, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(Ct);
        Assert.Equal(Phase.Refinement, (await db.Projects.AsNoTracking().SingleAsync(p => p.Id == created.Value.Id, Ct)).CurrentPhase);
        return created.Value.Id;
    }

    [Fact]
    public async Task S9_FR129_The_second_demo_is_seeded_migrated_from_nextjs_frontend_in_planning_once_and_the_Fake_plans_it()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        await SeedAsync(app);

        var projectId = Guid.Empty;
        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoPresetProjectName, Ct);
            projectId = project.Id;
            Assert.Equal((Phase.Planning, 3), (project.CurrentPhase, project.Checkpoint));
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == project.Id, Ct);
            Assert.Equal((DevSeeder.DemoPresetId, DevSeeder.DemoPresetVersion, ReleaseVersionStatus.InDevelopment), (version.PresetId, version.PresetVersion, version.Status));
            var composition = Domain.Stack.StackCompositionJson.Deserialize(version.StackComposition)!;
            Assert.Equal(("fe-nextjs", ".", (Domain.Stack.StackComponent?)null, (Domain.Stack.StackDataLayer?)null), (composition.Frontend!.Pack, composition.Frontend.Path, composition.Backend, composition.Data));
            Assert.Equal(LlmProvider.Fake, (await db.ProjectLlmSettings.SingleAsync(s => s.ProjectId == project.Id, Ct)).Provider);
            var artifacts = await db.Artifacts.Where(a => a.ProjectId == project.Id).OrderBy(a => a.Type).ToListAsync(Ct);
            Assert.All(artifacts, a => Assert.Equal(Domain.Artifacts.ArtifactStatus.Locked, a.Status));
            Assert.Contains("## Routes & Rendering Strategy", artifacts.Single(a => a.Type == Domain.Artifacts.ArtifactType.TechnicalSpecification).Content, StringComparison.Ordinal);
            Assert.Single(await db.Events.Where(e => e.Type == EventTypes.DemoPresetProjectSeeded).ToListAsync(Ct));
            Assert.Equal(3, await db.Projects.CountAsync(Ct));
        });

        var jobs = await Support.AgentJobs.RunQueuedAsync(app, projectId);

        Assert.Equal((Domain.Jobs.JobTypes.PlanGeneration, Domain.Jobs.JobStatus.Succeeded), (Assert.Single(jobs).Type, jobs[0].Status));
        await WithDbAsync(app, async db =>
        {
            var plan = await db.Artifacts.SingleAsync(a => a.ProjectId == projectId && a.Type == Domain.Artifacts.ArtifactType.ImplementationPlan, Ct);
            Assert.Contains("**Agent:** scaffolder", plan.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task S9_A_third_demo_on_fe_vue_and_Supabase_BaaS_is_seeded_in_specification_once_and_the_Fake_writes_its_spec()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        await SeedAsync(app);

        var projectId = Guid.Empty;
        await WithDbAsync(app, async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoBaasProjectName, Ct);
            projectId = project.Id;
            Assert.Equal((Phase.Specification, 2), (project.CurrentPhase, project.Checkpoint));
            Assert.Contains("fe-vue", project.StackPacks);
            Assert.Contains("data-supabase", project.StackPacks);
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == project.Id, Ct);
            Assert.Equal("baas", Domain.Stack.StackCompositionJson.Deserialize(version.StackComposition)!.Data!.Mode);
            Assert.Null(version.PresetId);
            Assert.Equal(Domain.Artifacts.ArtifactStatus.Locked, (await db.Artifacts.SingleAsync(a => a.ProjectId == project.Id, Ct)).Status);
            Assert.Single(await db.Events.Where(e => e.Type == EventTypes.DemoStackProjectSeeded).ToListAsync(Ct));
        });

        var jobs = await Support.AgentJobs.RunQueuedAsync(app, projectId);

        Assert.Equal((Domain.Jobs.JobTypes.SpecGeneration, Domain.Jobs.JobStatus.Succeeded), (Assert.Single(jobs).Type, jobs[0].Status));
    }

    [Fact]
    public async Task DEP2_Existing_admin_account_is_untouched()
    {
        await using var app = await AppAsync();
        string hash;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var mine = new AppUser { Email = DevSeeder.AdminEmail, UserName = DevSeeder.AdminEmail, Name = "Mine", SystemRole = SystemRole.User, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await users.CreateAsync(mine, AideApiFactory.Password)).Succeeded);
            hash = mine.PasswordHash!;
        }

        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            var user = await db.Users.SingleAsync(Ct);
            Assert.Equal(("Mine", SystemRole.User, hash), (user.Name, user.SystemRole, user.PasswordHash));
            var project = await db.Projects.SingleAsync(p => p.Name == DevSeeder.DemoProjectName, Ct);
            Assert.True(await db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == user.Id && m.Role == ProjectRole.Owner, Ct));
        });
    }

    [Fact]
    public async Task DEP2_Pending_migrations_skip_the_seed()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync(Ct);
        await using var app = App(connectionString);

        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync(Ct));
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync(Ct));
        });
        Assert.Contains(factory.Logs.Entries, e => e.Category == typeof(DevSeeder).FullName && e.Message.Contains("pending migration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DEP2_Deleted_demo_project_is_not_recreated()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);

        await WithDbAsync(app, async db => Assert.Equal(3, await db.Projects.ExecuteDeleteAsync(Ct)));
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            Assert.False(await db.Projects.AnyAsync(Ct));
            Assert.Equal(1, await db.Events.CountAsync(e => e.Type == EventTypes.DemoProjectSeeded, Ct));
            Assert.Equal(1, await db.Events.CountAsync(e => e.Type == EventTypes.DemoPresetProjectSeeded, Ct));
            Assert.Equal(1, await db.Events.CountAsync(e => e.Type == EventTypes.DemoStackProjectSeeded, Ct));
        });
    }

    [Fact]
    public async Task DEP2_Demo_project_seeded_before_the_marker_is_not_duplicated()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        await WithDbAsync(app, db => db.Events.Where(e => e.Type == EventTypes.DemoProjectSeeded).ExecuteDeleteAsync(Ct));

        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            Assert.Equal(1, await db.Projects.CountAsync(p => p.Name == DevSeeder.DemoProjectName, Ct));
            Assert.Equal(1, await db.Events.CountAsync(e => e.Type == EventTypes.DemoProjectSeeded, Ct));
        });
    }

    [Fact]
    public async Task DEP2_Seeding_is_off_by_default()
    {
        await using var app = await AppAsync();

        Assert.False(app.Services.GetRequiredService<IOptions<SeedOptions>>().Value.Enabled);
        await WithDbAsync(app, async db =>
        {
            Assert.False(await db.Users.AnyAsync(Ct));
            Assert.False(await db.Projects.AnyAsync(Ct));
        });
    }

    [Fact]
    public async Task DEP2_Seed_runs_at_startup_when_enabled()
    {
        var connectionString = await factory.CreateMigratedDatabaseAsync(Ct);
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", connectionString);
            b.UseSetting("Seed:Enabled", "true");
        });

        _ = app.Services; // starts the host and its hosted services

        await WithDbAsync(app, async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Email == DevSeeder.AdminEmail, Ct));
            Assert.Equal([DevSeeder.DemoPresetProjectName, DevSeeder.DemoProjectName, DevSeeder.DemoBaasProjectName], await db.Projects.OrderBy(p => p.Name).Select(p => p.Name).ToListAsync(Ct));
        });
    }

    private async Task<WebApplicationFactory<Program>> AppAsync() => App(await factory.CreateMigratedDatabaseAsync(Ct));

    private WebApplicationFactory<Program> App(string connectionString) =>
        factory.WithWebHostBuilder(b => b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", connectionString));

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var seeder = new DevSeeder(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<ILogger<DevSeeder>>());
        await seeder.SeedAsync(scope.ServiceProvider, Ct);
    }

    private static async Task WithDbAsync(WebApplicationFactory<Program> app, Func<AideDbContext, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AideDbContext>());
    }
}
