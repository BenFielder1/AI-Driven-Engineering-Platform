using Aide.Api.Seed;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure;
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
            var project = await db.Projects.SingleAsync(Ct);
            Assert.Equal((DevSeeder.DemoProjectName, LifecycleState.Initial.Phase, ApprovalMode.Advisory), (project.Name, project.CurrentPhase, project.ApprovalMode));
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
            var project = await db.Projects.SingleAsync(Ct);
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

        await WithDbAsync(app, async db => Assert.Equal(1, await db.Projects.ExecuteDeleteAsync(Ct)));
        await SeedAsync(app);

        await WithDbAsync(app, async db =>
        {
            Assert.False(await db.Projects.AnyAsync(Ct));
            Assert.Equal(1, await db.Events.CountAsync(e => e.Type == EventTypes.DemoProjectSeeded, Ct));
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
            Assert.Equal(1, await db.Projects.CountAsync(Ct));
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
            Assert.Equal(DevSeeder.DemoProjectName, (await db.Projects.SingleAsync(Ct)).Name);
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
