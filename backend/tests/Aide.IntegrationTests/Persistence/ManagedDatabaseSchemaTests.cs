using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>FR-147, FR-148, FR-152, FR-153, FR-154, §4.21, §5: the M30 schema (ManagedDatabases) against real PostgreSQL 17.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ManagedDatabaseSchemaTests(AideApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR152_one_managed_database_per_project_environment_stored_as_wire_strings()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var project = NewProject();
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);

        var production = NewDatabase(project.Id, DeploymentEnvironment.Production, ManagedDatabaseProvider.Render);
        production.ExpiresAt = Now.AddDays(30);
        db.ManagedDatabases.AddRange(production, NewDatabase(project.Id, DeploymentEnvironment.Preview, ManagedDatabaseProvider.Render));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(
            "production|render|provisioning",
            await db.Database.SqlQuery<string>(
                $"SELECT environment || '|' || provider || '|' || status AS \"Value\" FROM managed_databases WHERE id = {production.Id}")
                .SingleAsync(Ct));

        db.ManagedDatabases.Add(NewDatabase(project.Id, DeploymentEnvironment.Production, ManagedDatabaseProvider.Supabase));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_managed_databases_project_id_environment"), (pg.SqlState, pg.ConstraintName));

        db.ChangeTracker.Clear();
        await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(Ct);
        Assert.False(await db.ManagedDatabases.AnyAsync(d => d.ProjectId == project.Id, Ct));
    }

    [Fact]
    public async Task FR152_deleting_the_connection_keeps_the_database_unbound_and_xmin_detects_concurrent_edits()
    {
        Guid id;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AideDbContext>();
            var user = NewUser();
            db.Users.Add(user);
            var project = NewProject();
            db.Projects.Add(project);
            await db.SaveChangesAsync(Ct);
            var connection = new PlatformConnection
            {
                UserId = user.Id,
                Provider = PlatformProvider.Supabase,
                Label = "Personal",
                SecretEncrypted = "cipher-token",
                Last4 = "a1b2",
                Status = PlatformConnectionStatus.Valid,
                CreatedAt = Now,
            };
            db.PlatformConnections.Add(connection);
            var database = NewDatabase(project.Id, DeploymentEnvironment.Production, ManagedDatabaseProvider.Supabase);
            database.ConnectionId = connection.Id;
            db.ManagedDatabases.Add(database);
            await db.SaveChangesAsync(Ct);
            id = database.Id;

            await db.PlatformConnections.Where(c => c.Id == connection.Id).ExecuteDeleteAsync(Ct);
            Assert.Null(await db.ManagedDatabases.AsNoTracking().Where(d => d.Id == id).Select(d => d.ConnectionId).SingleAsync(Ct));
        }

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var db1 = first.ServiceProvider.GetRequiredService<AideDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<AideDbContext>();
        var a = await db1.ManagedDatabases.SingleAsync(d => d.Id == id, Ct);
        var b = await db2.ManagedDatabases.SingleAsync(d => d.Id == id, Ct);
        a.Status = ManagedDatabaseStatus.Available;
        await db1.SaveChangesAsync(Ct);
        b.Status = ManagedDatabaseStatus.Error;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task FR154_migration_run_round_trips_applied_files_and_links_to_its_release()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var project = NewProject();
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var versionId = project.CurrentReleaseVersionId ?? throw new InvalidOperationException("No release version.");
        var release = new Release
        {
            ProjectId = project.Id,
            ReleaseVersionId = versionId,
            Checkpoint = 1,
            Tag = "v0.1.0",
            Status = ReleaseStatus.InProgress,
            ReleaseNotes = "## 0.1.0\n- First release",
            CreatedAt = Now,
        };
        var database = NewDatabase(project.Id, DeploymentEnvironment.Production, ManagedDatabaseProvider.Supabase);
        db.Releases.Add(release);
        db.ManagedDatabases.Add(database);
        await db.SaveChangesAsync(Ct);

        var run = new DatabaseMigrationRun
        {
            ProjectId = project.Id,
            ManagedDatabaseId = database.Id,
            ReleaseId = release.Id,
            ReleaseVersionId = versionId,
            GitSha = new string('c', 40),
            Environment = DeploymentEnvironment.Production,
            Status = MigrationRunStatus.Succeeded,
            Applied = [new AppliedMigration("20261030120000", "create_todos", 42)],
            StartedAt = Now,
            FinishedAt = Now.AddSeconds(3),
        };
        db.DatabaseMigrationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        release.MigrationRunId = run.Id;
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
        var reloaded = await db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct);
        Assert.Equal(new AppliedMigration("20261030120000", "create_todos", 42), Assert.Single(reloaded.Applied));
        Assert.Equal("succeeded", await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM database_migration_runs WHERE id = {run.Id}").SingleAsync(Ct));
        var storedRelease = await db.Releases.AsNoTracking().SingleAsync(r => r.Id == release.Id, Ct);
        Assert.Equal((run.Id, "## 0.1.0\n- First release"), (storedRelease.MigrationRunId, storedRelease.ReleaseNotes));

        // Deleting the run clears the release's link; deleting the database removes its runs.
        await db.DatabaseMigrationRuns.Where(r => r.Id == run.Id).ExecuteDeleteAsync(Ct);
        Assert.Null(await db.Releases.AsNoTracking().Where(r => r.Id == release.Id).Select(r => r.MigrationRunId).SingleAsync(Ct));
        db.DatabaseMigrationRuns.Add(new DatabaseMigrationRun
        {
            ProjectId = project.Id,
            ManagedDatabaseId = database.Id,
            ReleaseVersionId = versionId,
            GitSha = new string('d', 40),
            StartedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
        await db.ManagedDatabases.Where(d => d.Id == database.Id).ExecuteDeleteAsync(Ct);
        Assert.False(await db.DatabaseMigrationRuns.AnyAsync(r => r.ProjectId == project.Id, Ct));
    }

    private static ManagedDatabase NewDatabase(Guid projectId, DeploymentEnvironment environment, ManagedDatabaseProvider provider) => new()
    {
        ProjectId = projectId,
        Environment = environment,
        Provider = provider,
        ConnectionEncrypted = "cipher-connection",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static AppUser NewUser()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        return new AppUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = "Test User",
            SystemRole = SystemRole.User,
            CreatedAt = Now,
        };
    }

    private static Project NewProject() => new()
    {
        Name = "Demo",
        Slug = $"demo-{Guid.NewGuid():N}",
        Idea = "An idea",
        CreatedAt = Now,
        UpdatedAt = Now,
    };
}
