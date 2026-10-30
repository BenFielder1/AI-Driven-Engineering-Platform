using Aide.Domain.Entities;
using Aide.Infrastructure;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>
/// FR-142, FR-144, FR-145, FR-151 (§5): the PlatformsAndTargets migration applied to a database at ReleaseMergeSubmittedAt
/// holding v2 Vercel connections, deployment settings, env vars and deployments.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlatformsAndTargetsMigrationTests(AideApiFactory factory)
{
    private const string PreviousMigration = "20261029174717_ReleaseMergeSubmittedAt";

    private static readonly Guid Ana = Guid.Parse("01990000-0000-7000-8000-0000000003e1");
    private static readonly Guid Ben = Guid.Parse("01990000-0000-7000-8000-0000000003e2");
    private static readonly Guid Bound = Guid.Parse("01990000-0000-7000-8000-0000000003a1");
    private static readonly Guid Cleared = Guid.Parse("01990000-0000-7000-8000-0000000003a2");
    private static readonly Guid Unlinked = Guid.Parse("01990000-0000-7000-8000-0000000003a3");
    private static readonly Guid TeamConnection = Guid.Parse("01990000-0000-7000-8000-0000000003c1");
    private static readonly Guid InvalidConnection = Guid.Parse("01990000-0000-7000-8000-0000000003c2");
    private static readonly Guid Version = Guid.Parse("01990000-0000-7000-8000-0000000003b1");
    private static readonly Guid ReleaseId = Guid.Parse("01990000-0000-7000-8000-0000000003d1");
    private static readonly Guid Production = Guid.Parse("01990000-0000-7000-8000-0000000003f1");
    private static readonly Guid Promote = Guid.Parse("01990000-0000-7000-8000-0000000003f2");
    private static readonly Guid Preview = Guid.Parse("01990000-0000-7000-8000-0000000003f3");

    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR142_v2_Vercel_connections_are_copied_with_the_same_id_and_ciphertext()
    {
        await using var db = await MigratedDatabaseAsync();

        var team = await db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == TeamConnection, Ct);
        Assert.Equal(
            (Ana, PlatformProvider.Vercel, "Acme", "v2-cipher-team", "a1b2", "usr_ana", "ana-dev", PlatformConnectionStatus.Valid),
            (team.UserId, team.Provider, team.Label, team.SecretEncrypted, team.Last4, team.AccountId, team.AccountName, team.Status));
        Assert.Equal(("team_acme", "acme"), (team.Metadata.TeamId, team.Metadata.TeamSlug));
        Assert.Equal((T0.AddDays(90), T0, T0), (team.ExpiresAt!.Value, team.LastValidatedAt!.Value, team.CreatedAt));
        Assert.Empty(team.Scopes);

        var invalid = await db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == InvalidConnection, Ct);
        Assert.Equal((PlatformConnectionStatus.Invalid, (string?)null), (invalid.Status, invalid.Metadata.TeamId));
        Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name IN ('vercel_connections', 'project_deployment_settings')"));
    }

    [Fact]
    public async Task FR144_v2_settings_become_frontend_targets_with_status_from_binding_and_project()
    {
        await using var db = await MigratedDatabaseAsync();
        var targets = await db.DeploymentTargets.AsNoTracking().ToDictionaryAsync(t => t.ProjectId, Ct);

        var bound = targets[Bound];
        Assert.Equal(
            (DeploymentComponent.Frontend, DeploymentProvider.Vercel, (Guid?)TeamConnection, (Guid?)Ana, "prj_1", "todo", "apps/web", "https://todo.vercel.app"),
            (bound.Component, bound.Provider, bound.ConnectionId, bound.BoundByUserId, bound.ResourceId, bound.Name, bound.RootDirectory, bound.ProductionUrl));
        Assert.Equal(("nextjs", DeploymentTargetStatus.Ready, (string?)null), (bound.BuildConfig.Framework, bound.Status, bound.StatusReason));
        Assert.Equal(7, bound.Id.Version);

        Assert.Equal((DeploymentTargetStatus.Attention, "connection_missing"), (targets[Cleared].Status, targets[Cleared].StatusReason));
        Assert.Equal((DeploymentTargetStatus.NotConfigured, (Guid?)InvalidConnection), (targets[Unlinked].Status, targets[Unlinked].ConnectionId));
        Assert.Equal(DeploymentProvider.Fake, targets[Cleared].Provider);

        // The binding FK now points at platform_connections (same ids).
        await db.PlatformConnections.Where(c => c.Id == TeamConnection).ExecuteDeleteAsync(Ct);
        Assert.Null((await db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == Bound, Ct)).ConnectionId);
    }

    [Fact]
    public async Task FR145_v2_env_vars_apply_to_the_frontend_are_user_variables_and_NEXT_PUBLIC_ones_are_public()
    {
        await using var db = await MigratedDatabaseAsync();
        var vars = await db.DeploymentEnvVars.AsNoTracking().Where(v => v.ProjectId == Bound).ToDictionaryAsync(v => v.Key, Ct);

        Assert.True(vars["NEXT_PUBLIC_API_URL"].Public);
        Assert.False(vars["SECRET_KEY"].Public);
        Assert.False(vars["NEXT_PUBLICITY"].Public);
        Assert.All(vars.Values, v =>
        {
            Assert.Equal(["frontend"], v.AppliesTo);
            Assert.Equal((DeploymentEnvSource.User, (string?)null), (v.Source, v.DerivedFrom));
        });
    }

    [Fact]
    public async Task FR151_v2_deployments_get_their_target_component_environment_and_kind()
    {
        await using var db = await MigratedDatabaseAsync();
        var target = await db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == Bound, Ct);
        var rows = await db.Deployments.AsNoTracking().Where(d => d.ProjectId == Bound).ToDictionaryAsync(d => d.Id, Ct);

        Assert.All(rows.Values, d => Assert.Equal(((Guid?)target.Id, DeploymentComponent.Frontend, (Guid?)null), (d.TargetId, d.Component, d.RolloutId)));
        Assert.Equal((DeploymentEnvironment.Production, DeploymentKind.Deploy), (rows[Production].Environment, rows[Production].Kind));
        Assert.Equal((DeploymentEnvironment.Production, DeploymentKind.Promote), (rows[Promote].Environment, rows[Promote].Kind));
        Assert.Equal((DeploymentEnvironment.Preview, DeploymentKind.Deploy), (rows[Preview].Environment, rows[Preview].Kind));

        // The automatic production deployment stays unique per (release, component).
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO deployments (id, project_id, release_version_id, release_id, provider, environment, component, kind, provider_deployment_id,
                                     git_ref, git_sha, status, trigger, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {Bound}, {Version}, {ReleaseId}, 'vercel', 'production', 'frontend', 'deploy', 'dpl_dup',
                    'main', {new string('c', 40)}, 'queued', 'auto', {T0}, {T0})
            """, Ct));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_deployments_release_id_component_auto_production"), (error.SqlState, error.ConstraintName));
    }

    private static Task<int> CountAsync(AideDbContext db, string sql) =>
        db.Database.SqlQueryRaw<int>(sql).SingleAsync(Ct);

    /// <summary>A new database at ReleaseMergeSubmittedAt, seeded with v2 deployment data, then migrated to the latest version.</summary>
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
            command.Parameters.AddWithValue("expires", T0.AddDays(90));
            await command.ExecuteNonQueryAsync(Ct);
        }

        await db.Database.MigrateAsync(Ct);
        return db;
    }

    /// <summary>
    /// Ana (team token, valid) and Ben (account token, invalid). Project 3a1 is bound to Ana's connection with a Vercel project,
    /// 3a2 has a project but its binding was cleared (Fake provider), 3a3 is bound to Ben's connection without a project.
    /// 3a1 has env vars, a release and three deployments (auto production, promote, preview).
    /// </summary>
    private const string Seed =
        """
        INSERT INTO users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, name, system_role, created_at,
                           phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
        VALUES ('01990000-0000-7000-8000-0000000003e1', 'ana@aide.test', 'ANA@AIDE.TEST', 'ana@aide.test', 'ANA@AIDE.TEST', true, 'Ana', 'user', @t0, false, false, false, 0),
               ('01990000-0000-7000-8000-0000000003e2', 'ben@aide.test', 'BEN@AIDE.TEST', 'ben@aide.test', 'BEN@AIDE.TEST', true, 'Ben', 'user', @t0, false, false, false, 0);
        INSERT INTO projects (id, name, slug, idea, current_phase, status, checkpoint, approval_mode, compliance_threshold,
                              max_files_per_step, max_review_cycles, tokens_used, execution_paused, created_at, updated_at)
        VALUES ('01990000-0000-7000-8000-0000000003a1', 'Bound', 'bound', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0),
               ('01990000-0000-7000-8000-0000000003a2', 'Cleared', 'cleared', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0),
               ('01990000-0000-7000-8000-0000000003a3', 'Unlinked', 'unlinked', 'i', 'refinement', 'active', 1, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0);
        INSERT INTO vercel_connections (id, user_id, label, token_encrypted, last4, vercel_user_id, vercel_username, team_id, team_slug,
                                        scope, expires_at, status, last_validated_at, created_at)
        VALUES ('01990000-0000-7000-8000-0000000003c1', '01990000-0000-7000-8000-0000000003e1', 'Acme', 'v2-cipher-team', 'a1b2', 'usr_ana', 'ana-dev',
                'team_acme', 'acme', 'team', @expires, 'valid', @t0, @t0),
               ('01990000-0000-7000-8000-0000000003c2', '01990000-0000-7000-8000-0000000003e2', 'Mine', 'v2-cipher-ben', 'c3d4', 'usr_ben', NULL,
                NULL, NULL, 'account', NULL, 'invalid', @t0, @t0);
        INSERT INTO project_deployment_settings (project_id, provider, connection_id, bound_by_user_id, vercel_project_id, vercel_project_name,
                                                 root_directory, framework, production_url, updated_at)
        VALUES ('01990000-0000-7000-8000-0000000003a1', 'vercel', '01990000-0000-7000-8000-0000000003c1', '01990000-0000-7000-8000-0000000003e1',
                'prj_1', 'todo', 'apps/web', 'nextjs', 'https://todo.vercel.app', @t0),
               ('01990000-0000-7000-8000-0000000003a2', 'fake', NULL, NULL, 'prj_2', 'cleared', '.', 'nextjs', NULL, @t0),
               ('01990000-0000-7000-8000-0000000003a3', 'vercel', '01990000-0000-7000-8000-0000000003c2', '01990000-0000-7000-8000-0000000003e2',
                NULL, NULL, '.', 'nextjs', NULL, @t0);
        INSERT INTO deployment_env_vars (id, project_id, key, targets, value_encrypted, sensitive, updated_at)
        VALUES ('01990000-0000-7000-8000-000000000361', '01990000-0000-7000-8000-0000000003a1', 'NEXT_PUBLIC_API_URL', '{preview,production}', 'c1', false, @t0),
               ('01990000-0000-7000-8000-000000000362', '01990000-0000-7000-8000-0000000003a1', 'SECRET_KEY', '{production}', 'c2', true, @t0),
               ('01990000-0000-7000-8000-000000000363', '01990000-0000-7000-8000-0000000003a1', 'NEXT_PUBLICITY', '{production}', 'c3', false, @t0);
        INSERT INTO release_versions (id, project_id, version, status, request, created_at)
        VALUES ('01990000-0000-7000-8000-0000000003b1', '01990000-0000-7000-8000-0000000003a1', '0.1.0', 'released', 'r', @t0);
        INSERT INTO releases (id, project_id, release_version_id, checkpoint, tag, status, conflicting_files, created_at)
        VALUES ('01990000-0000-7000-8000-0000000003d1', '01990000-0000-7000-8000-0000000003a1', '01990000-0000-7000-8000-0000000003b1', 1, 'v0.1.0', 'succeeded', '{}', @t0);
        INSERT INTO deployments (id, project_id, release_version_id, release_id, provider, target, provider_deployment_id, git_ref, git_sha,
                                 status, trigger, promoted_from_deployment_id, created_at, updated_at)
        VALUES ('01990000-0000-7000-8000-0000000003f1', '01990000-0000-7000-8000-0000000003a1', '01990000-0000-7000-8000-0000000003b1',
                '01990000-0000-7000-8000-0000000003d1', 'vercel', 'production', 'dpl_1', 'main', repeat('a', 40), 'ready', 'auto', NULL, @t0, @t0),
               ('01990000-0000-7000-8000-0000000003f2', '01990000-0000-7000-8000-0000000003a1', '01990000-0000-7000-8000-0000000003b1',
                '01990000-0000-7000-8000-0000000003d1', 'vercel', 'production', 'dpl_1', 'main', repeat('a', 40), 'ready', 'promote',
                '01990000-0000-7000-8000-0000000003f1', @t0, @t0),
               ('01990000-0000-7000-8000-0000000003f3', '01990000-0000-7000-8000-0000000003a1', '01990000-0000-7000-8000-0000000003b1',
                NULL, 'vercel', 'preview', 'dpl_2', 'aide/0.1.0', repeat('b', 40), 'ready', 'auto', NULL, @t0, @t0);
        """;
}
