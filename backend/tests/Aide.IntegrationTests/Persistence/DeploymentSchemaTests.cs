using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>FR-87, FR-88, FR-91, §4.15, §5: the VercelDeployments schema against real PostgreSQL 17.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentSchemaTests(AideApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR91_S4_15_auto_production_deployment_is_unique_per_release_and_redeploy_adds_a_row()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var (project, versionId, release) = await ProjectWithReleaseAsync(db);

        var first = NewDeployment(project.Id, versionId, release.Id, DeploymentTarget.Production, DeploymentTrigger.Auto);
        db.Deployments.Add(first);
        await db.SaveChangesAsync(Ct);

        var duplicate = NewDeployment(project.Id, versionId, release.Id, DeploymentTarget.Production, DeploymentTrigger.Auto);
        db.Deployments.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_deployments_release_id_target_auto_production"), (pg.SqlState, pg.ConstraintName));
        db.Entry(duplicate).State = EntityState.Detached;

        // Redeploy and promote of the same release are new rows; so are previews.
        db.Deployments.Add(NewDeployment(project.Id, versionId, release.Id, DeploymentTarget.Production, DeploymentTrigger.Redeploy));
        db.Deployments.Add(NewDeployment(project.Id, versionId, release.Id, DeploymentTarget.Production, DeploymentTrigger.Redeploy));
        var promote = NewDeployment(project.Id, versionId, release.Id, DeploymentTarget.Production, DeploymentTrigger.Promote);
        promote.PromotedFromDeploymentId = first.Id;
        db.Deployments.Add(promote);
        db.Deployments.Add(NewDeployment(project.Id, versionId, null, DeploymentTarget.Preview, DeploymentTrigger.Auto));
        db.Deployments.Add(NewDeployment(project.Id, versionId, null, DeploymentTarget.Preview, DeploymentTrigger.Auto));
        await db.SaveChangesAsync(Ct);

        var rows = await db.Deployments.AsNoTracking().Where(d => d.ProjectId == project.Id).ToListAsync(Ct);
        Assert.Equal(6, rows.Count);
        Assert.Equal(
            "production|auto|queued|fake",
            await db.Database.SqlQuery<string>(
                $"SELECT target || '|' || trigger || '|' || status || '|' || provider AS \"Value\" FROM deployments WHERE id = {first.Id}")
                .SingleAsync(Ct));

        // Deleting the release keeps its deployments (release_id SET NULL); the project cascade removes them.
        await db.Releases.Where(r => r.Id == release.Id).ExecuteDeleteAsync(Ct);
        Assert.All(await db.Deployments.AsNoTracking().Where(d => d.ProjectId == project.Id).ToListAsync(Ct), d => Assert.Null(d.ReleaseId));
        await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(Ct);
        Assert.False(await db.Deployments.AnyAsync(d => d.ProjectId == project.Id, Ct));
    }

    [Fact]
    public async Task FR88_env_var_key_is_unique_per_project_and_targets_are_preview_or_production()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var project = NewProject();
        var other = NewProject();
        db.Projects.AddRange(project, other);
        var stored = NewEnvVar(project.Id, "NEXT_PUBLIC_SUPABASE_URL", ["preview", "production"]);
        stored.VercelEnvIds = new Dictionary<string, string> { ["preview"] = "env_a", ["production"] = "env_b" };
        db.DeploymentEnvVars.AddRange(stored, NewEnvVar(other.Id, "NEXT_PUBLIC_SUPABASE_URL", ["production"]));
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
        var reloaded = await db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.Id == stored.Id, Ct);
        Assert.Equal(["preview", "production"], reloaded.Targets);
        Assert.Equal("env_b", reloaded.VercelEnvIds!["production"]);

        db.DeploymentEnvVars.Add(NewEnvVar(project.Id, "NEXT_PUBLIC_SUPABASE_URL", ["preview"]));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ix_deployment_env_vars_project_id_key"), (pg.SqlState, pg.ConstraintName));

        // Keys are case-sensitive, like Vercel's.
        db.ChangeTracker.Clear();
        db.DeploymentEnvVars.Add(NewEnvVar(project.Id, "next_public_supabase_url", ["preview"]));
        await db.SaveChangesAsync(Ct);

        foreach (IReadOnlyList<string> targets in new[] { new[] { "development" }, Array.Empty<string>() })
        {
            db.ChangeTracker.Clear();
            db.DeploymentEnvVars.Add(NewEnvVar(project.Id, $"KEY_{targets.Count}", targets));
            error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("ck_deployment_env_vars_targets", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        }
    }

    [Fact]
    public async Task FR87_team_token_needs_a_team_and_deleting_a_connection_clears_the_project_binding()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var user = NewUser();
        var project = NewProject();
        db.Users.Add(user);
        db.Projects.Add(project);
        var connection = NewConnection(user.Id);
        connection.Scope = VercelTokenScope.Team;
        db.VercelConnections.Add(connection);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal("ck_vercel_connections_team_scope", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);

        (connection.TeamId, connection.TeamSlug) = ("team_123", "acme");
        db.ProjectDeploymentSettings.Add(new ProjectDeploymentSettings
        {
            ProjectId = project.Id,
            ConnectionId = connection.Id,
            BoundByUserId = user.Id,
            VercelProjectId = "prj_1",
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
        var settings = await db.ProjectDeploymentSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct);
        Assert.Equal((DeploymentProvider.Vercel, ".", "nextjs"), (settings.Provider, settings.RootDirectory, settings.Framework));

        await db.VercelConnections.Where(c => c.Id == connection.Id).ExecuteDeleteAsync(Ct);
        settings = await db.ProjectDeploymentSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct);
        Assert.Equal(((Guid?)null, (Guid?)user.Id), (settings.ConnectionId, settings.BoundByUserId));

        // A user's connections go with the user.
        db.VercelConnections.Add(NewConnection(user.Id));
        await db.SaveChangesAsync(Ct);
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(Ct);
        Assert.False(await db.VercelConnections.AnyAsync(c => c.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task S4_15_stale_deployment_update_throws_concurrency_exception()
    {
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var (project, versionId, _) = await ProjectWithReleaseAsync(db);
            var deployment = NewDeployment(project.Id, versionId, null, DeploymentTarget.Preview, DeploymentTrigger.Auto);
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync(Ct);
            id = deployment.Id;
        }

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var db1 = first.ServiceProvider.GetRequiredService<AideDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<AideDbContext>();
        var a = await db1.Deployments.SingleAsync(d => d.Id == id, Ct);
        var b = await db2.Deployments.SingleAsync(d => d.Id == id, Ct);
        a.Status = DeploymentStatus.Ready;
        await db1.SaveChangesAsync(Ct);
        b.Status = DeploymentStatus.Canceled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync(Ct));
    }

    [Fact]
    public void S4_15_build_log_tail_keeps_the_end_of_a_long_log()
    {
        var log = new string('a', 10) + new string('b', Deployment.BuildLogTailMaxLength);
        Assert.Equal(new string('b', Deployment.BuildLogTailMaxLength), Deployment.TailOf(log));
        Assert.Equal("short", Deployment.TailOf("short"));
    }

    private static async Task<(Project Project, Guid VersionId, Release Release)> ProjectWithReleaseAsync(AideDbContext db)
    {
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
            MergeCommitSha = new string('c', 40),
            Status = ReleaseStatus.Succeeded,
            CreatedAt = Now,
            CompletedAt = Now,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync(Ct);
        return (project, versionId, release);
    }

    private static Deployment NewDeployment(Guid projectId, Guid versionId, Guid? releaseId, DeploymentTarget target, DeploymentTrigger trigger) => new()
    {
        ProjectId = projectId,
        ReleaseVersionId = versionId,
        ReleaseId = releaseId,
        Provider = DeploymentProvider.Fake,
        Target = target,
        Trigger = trigger,
        ProviderDeploymentId = $"dpl_{Guid.NewGuid():N}",
        GitRef = target == DeploymentTarget.Production ? "main" : "aide/demo",
        GitSha = new string('c', 40),
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static DeploymentEnvVar NewEnvVar(Guid projectId, string key, IReadOnlyList<string> targets) => new()
    {
        ProjectId = projectId,
        Key = key,
        Targets = targets,
        ValueEncrypted = "cipher-value",
        Sensitive = true,
        UpdatedAt = Now,
    };

    private static VercelConnection NewConnection(Guid userId) => new()
    {
        UserId = userId,
        Label = "Personal",
        TokenEncrypted = "cipher-token",
        Last4 = "a1b2",
        VercelUserId = "usr_1",
        CreatedAt = Now,
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
