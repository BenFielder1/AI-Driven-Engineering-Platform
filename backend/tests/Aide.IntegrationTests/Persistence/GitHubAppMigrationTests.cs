using Aide.Domain.Entities;
using Aide.Infrastructure;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>
/// FR-80–FR-83: the GitHubApp migration applied to a database at LlmCredentials holding a v1 GitHub (PAT) project, a
/// local project and a user linked to GitHub id 42; then the github_apps / github_connections / github_app rules.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubAppMigrationTests(AideApiFactory factory)
{
    private const string PreviousMigration = "20261016084205_LlmCredentials";

    private static readonly Guid PatProject = Guid.Parse("01990000-0000-7000-8000-0000000002a0");
    private static readonly Guid LocalProject = Guid.Parse("01990000-0000-7000-8000-0000000002b0");
    private static readonly Guid LinkedUser = Guid.Parse("01990000-0000-7000-8000-0000000002e1");

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR82_v1_github_project_becomes_github_pat_and_keeps_its_repo_and_token()
    {
        await using var db = await MigratedDatabaseAsync();

        var pat = await db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == PatProject, Ct);
        Assert.Equal(
            (GitProvider.GithubPat, "https://github.com/acme/widgets", "trunk", "v1-ciphertext", (long?)null, (long?)null, (string?)null),
            (pat.Provider, pat.RepoUrl, pat.DefaultBranch, pat.TokenEncrypted, pat.InstallationId, pat.RepoId, pat.RepoFullName));
        Assert.Equal("github_pat", await ProviderValueAsync(db, PatProject));

        var local = await db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == LocalProject, Ct);
        Assert.Equal(GitProvider.Local, local.Provider);
        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM project_git_settings WHERE provider = 'github'").SingleAsync(Ct));
    }

    [Fact]
    public async Task FR80_v1_github_link_stays_on_the_user_and_no_connection_is_created()
    {
        await using var db = await MigratedDatabaseAsync();

        Assert.Equal("42", await db.Users.Where(u => u.Id == LinkedUser).Select(u => u.GithubId).SingleAsync(Ct));
        Assert.Empty(await db.GitHubConnections.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await db.GitHubApps.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task FR80_a_second_github_app_row_is_rejected()
    {
        await using var db = await MigratedDatabaseAsync();
        db.GitHubApps.Add(NewApp(1001));
        await db.SaveChangesAsync(Ct);

        var stored = await db.GitHubApps.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((1001L, GitHubOwnerType.Organization, "write"), (stored.AppId, stored.OwnerType, stored.Permissions["contents"]));

        db.ChangeTracker.Clear();
        db.GitHubApps.Add(NewApp(1002));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_github_apps_singleton"), (pg.SqlState, pg.ConstraintName));
    }

    [Fact]
    public async Task FR81_github_user_id_is_unique_and_the_connection_goes_with_the_user()
    {
        await using var db = await MigratedDatabaseAsync();
        var other = Guid.Parse("01990000-0000-7000-8000-0000000002e2");
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, name, system_role, created_at,
                               phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
            VALUES ({other}, 'other@aide.test', 'OTHER@AIDE.TEST', 'other@aide.test', 'OTHER@AIDE.TEST', true, 'Other', 'user', {T0}, false, false, false, 0)
            """, Ct);

        db.GitHubConnections.Add(NewConnection(LinkedUser, 42));
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
        db.GitHubConnections.Add(NewConnection(other, 42));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal("ix_github_connections_github_user_id", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);

        db.ChangeTracker.Clear();
        await db.Users.Where(u => u.Id == LinkedUser).ExecuteDeleteAsync(Ct);
        Assert.Empty(await db.GitHubConnections.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task FR82_github_app_provider_requires_installation_and_repo()
    {
        await using var db = await MigratedDatabaseAsync();
        var settings = await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == LocalProject, Ct);
        settings.Provider = GitProvider.GithubApp;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal("ck_project_git_settings_github_app_repo", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);

        (settings.InstallationId, settings.RepoId, settings.RepoFullName) = (77, 9001, "acme/todo-app");
        await db.SaveChangesAsync(Ct);
        Assert.Equal("github_app", await ProviderValueAsync(db, LocalProject));
    }

    private static GitHubApp NewApp(long appId) => new()
    {
        AppId = appId,
        Slug = $"aide-localhost-{appId}",
        OwnerLogin = "acme",
        OwnerType = GitHubOwnerType.Organization,
        ClientId = "Iv23li000",
        ClientSecretEncrypted = "cipher-secret",
        PrivateKeyEncrypted = "cipher-key",
        Permissions = new Dictionary<string, string> { ["contents"] = "write", ["metadata"] = "read" },
        CreatedAt = T0,
    };

    private static GitHubConnection NewConnection(Guid userId, long githubUserId) => new()
    {
        UserId = userId,
        GithubUserId = githubUserId,
        Login = "octo",
        AccessTokenEncrypted = "cipher-access",
        AccessExpiresAt = T0.AddHours(8),
        RefreshTokenEncrypted = "cipher-refresh",
        RefreshExpiresAt = T0.AddMonths(6),
        UpdatedAt = T0,
    };

    private static Task<string> ProviderValueAsync(AideDbContext db, Guid projectId) =>
        db.Database.SqlQuery<string>($"SELECT provider AS \"Value\" FROM project_git_settings WHERE project_id = {projectId}").SingleAsync(Ct);

    /// <summary>A new database at LlmCredentials, seeded with v1-style Git settings, then migrated to the latest version.</summary>
    private async Task<AideDbContext> MigratedDatabaseAsync()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new AideDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = Seed;
            command.Parameters.AddWithValue("t0", T0);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await db.Database.MigrateAsync(Ct);
        return db;
    }

    /// <summary>Project 2a0 is a v1 GitHub (PAT) project; 2b0 is local. User 2e1 signed in with GitHub id 42.</summary>
    private const string Seed =
        """
        INSERT INTO users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, name, system_role, github_id, created_at,
                           phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
        VALUES ('01990000-0000-7000-8000-0000000002e1', 'linked@aide.test', 'LINKED@AIDE.TEST', 'linked@aide.test', 'LINKED@AIDE.TEST', true, 'Linked', 'user', '42', @t0, false, false, false, 0);
        INSERT INTO projects (id, name, slug, idea, current_phase, status, checkpoint, approval_mode, compliance_threshold,
                              max_files_per_step, max_review_cycles, tokens_used, execution_paused, created_at, updated_at)
        VALUES ('01990000-0000-7000-8000-0000000002a0', 'Pat', 'pat', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0),
               ('01990000-0000-7000-8000-0000000002b0', 'Local', 'local', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0);
        INSERT INTO project_members (project_id, user_id, role, created_at)
        VALUES ('01990000-0000-7000-8000-0000000002a0', '01990000-0000-7000-8000-0000000002e1', 'owner', @t0),
               ('01990000-0000-7000-8000-0000000002b0', '01990000-0000-7000-8000-0000000002e1', 'owner', @t0);
        INSERT INTO project_git_settings (project_id, provider, repo_url, default_branch, token_encrypted)
        VALUES ('01990000-0000-7000-8000-0000000002a0', 'github', 'https://github.com/acme/widgets', 'trunk', 'v1-ciphertext'),
               ('01990000-0000-7000-8000-0000000002b0', 'local', NULL, 'main', NULL);
        """;
}
