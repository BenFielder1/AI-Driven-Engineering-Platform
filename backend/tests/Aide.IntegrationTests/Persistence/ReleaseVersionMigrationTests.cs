using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>
/// FR-67 / §4.10 "Migration from v1": the ReleaseVersions migration applied to a database holding v1-shaped data
/// (migrated to AdminListIndexes, seeded with SQL in the v1 schema, then migrated to the latest version).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReleaseVersionMigrationTests(AideApiFactory factory)
{
    private const string LastV1Migration = "20261010121510_AdminListIndexes";

    private static readonly Guid Released = Guid.Parse("01990000-0000-7000-8000-00000000000a");
    private static readonly Guid InProgress = Guid.Parse("01990000-0000-7000-8000-00000000000b");
    private static readonly Guid RolledBack = Guid.Parse("01990000-0000-7000-8000-00000000000c");
    private static readonly Guid ReleasedPlan = Guid.Parse("01990000-0000-7000-8000-0000000000a3");
    private static readonly Guid ReleasedStep = Guid.Parse("01990000-0000-7000-8000-0000000000a9");
    private static readonly Guid RunAttempt1 = Guid.Parse("01990000-0000-7000-8000-0000000000b1");
    private static readonly Guid RunAttempt2 = Guid.Parse("01990000-0000-7000-8000-0000000000b2");
    private static readonly Guid RolledBackOptimization = Guid.Parse("01990000-0000-7000-8000-0000000000c5");
    private static readonly Guid RolledBackRollback = Guid.Parse("01990000-0000-7000-8000-0000000000cf");

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR67_project_released_once_as_v7_aide_becomes_released_0_1_0_with_that_tag()
    {
        await using var db = await MigratedV1DatabaseAsync();

        var version = Assert.Single(await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == Released).ToListAsync(Ct));
        Assert.Equal(
            ("0.1.0", ReleaseVersionStatus.Released, "v7-aide", "abc1234", "aide/released-0199000a", T0.AddHours(7)),
            (version.Version, version.Status, version.Tag, version.MergeCommitSha, version.BranchName, version.ReleasedAt));
        Assert.Equal(("https://github.com/acme/released/pull/12", 12), (version.PullRequestUrl, version.PullRequestNumber));
        Assert.Null(version.BaseTag);
        Assert.Equal("Released idea", version.Request);

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == Released, Ct);
        Assert.Equal((8, "0.1.0", (string?)null, version.Id), (project.Checkpoint, project.ReleasedVersion, project.DevelopmentVersion, project.CurrentReleaseVersionId));
        Assert.Equal((MergeStrategy.AideMerges, "v"), (project.MergeStrategy, project.TagPrefix));

        // Every v1 row of the project belongs to 0.1.0; checkpoints keep the v1 project version numbers.
        Assert.All(await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == Released).ToListAsync(Ct), a => Assert.Equal(version.Id, a.ReleaseVersionId));
        Assert.All(await db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == Released).ToListAsync(Ct), s => Assert.Equal(version.Id, s.ReleaseVersionId));
        Assert.All(await db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == Released).ToListAsync(Ct), r => Assert.Equal(version.Id, r.ReleaseVersionId));
        var transitions = await db.StateTransitions.AsNoTracking().Where(t => t.ProjectId == Released).OrderBy(t => t.CreatedAt).ToListAsync(Ct);
        Assert.Equal([1, 2, 3, 4, 5, 5, 6, 7, 8], transitions.Select(t => t.Checkpoint));
        Assert.All(transitions, t => Assert.Equal(version.Id, t.ReleaseVersionId));
        var release = await db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == Released, Ct);
        Assert.Equal((version.Id, 7, "v7-aide"), (release.ReleaseVersionId, release.Checkpoint, release.Tag));
        Assert.All(await db.ApprovalGates.AsNoTracking().Where(g => g.ProjectId == Released).ToListAsync(Ct), g => Assert.Equal(version.Id, g.ReleaseVersionId));

        // Nothing in development, so the project can start a new version (the partial unique index allows one).
        Assert.False(await db.ReleaseVersions.AnyAsync(
            v => v.ProjectId == Released && (v.Status == ReleaseVersionStatus.InDevelopment || v.Status == ReleaseVersionStatus.AwaitingMerge), Ct));

        // The PR fields moved off the Git settings.
        Assert.Equal(0, await ColumnCountAsync(db, "project_git_settings", "pull_request_url", "pull_request_number", "branch_name"));
    }

    [Fact]
    public async Task FR67_project_never_released_and_in_progress_gets_only_0_1_0_in_development()
    {
        await using var db = await MigratedV1DatabaseAsync();

        var version = Assert.Single(await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == InProgress).ToListAsync(Ct));
        Assert.Equal(("0.1.0", ReleaseVersionStatus.InDevelopment, (string?)null, (string?)null, "aide/progress-0199000b"),
            (version.Version, version.Status, version.Tag, version.BaseTag, version.BranchName));
        Assert.Null(version.ReleasedAt);
        Assert.Equal(T0, version.CreatedAt);

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == InProgress, Ct);
        Assert.Equal((2, (string?)null, "0.1.0", version.Id), (project.Checkpoint, project.ReleasedVersion, project.DevelopmentVersion, project.CurrentReleaseVersionId));
        Assert.All(await db.StateTransitions.AsNoTracking().Where(t => t.ProjectId == InProgress).ToListAsync(Ct), t => Assert.Equal(version.Id, t.ReleaseVersionId));
        Assert.All(await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == InProgress).ToListAsync(Ct), a => Assert.Equal(version.Id, a.ReleaseVersionId));
    }

    [Fact]
    public async Task FR67_project_rolled_back_after_its_release_keeps_released_0_1_0_and_develops_0_2_0_from_its_live_state()
    {
        await using var db = await MigratedV1DatabaseAsync();

        var versions = await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == RolledBack).OrderBy(v => v.Version).ToListAsync(Ct);
        Assert.Equal(
            [("0.1.0", ReleaseVersionStatus.Released, "v7-aide", (string?)null), ("0.2.0", ReleaseVersionStatus.InDevelopment, null, "v7-aide")],
            versions.Select(v => (v.Version, v.Status, v.Tag, v.BaseTag)));
        var (released, development) = (versions[0], versions[1]);
        Assert.Equal("def5678", development.BaseCommitSha);

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == RolledBack, Ct);
        Assert.Equal((3, "0.1.0", "0.2.0", development.Id), (project.Checkpoint, project.ReleasedVersion, project.DevelopmentVersion, project.CurrentReleaseVersionId));

        // The restored state (live artifacts and transitions at checkpoints <= 3) belongs to the version in development;
        // what the rollback invalidated, and the release itself, stay in 0.1.0.
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == RolledBack).ToListAsync(Ct);
        Assert.All(artifacts.Where(a => a.Id != RolledBackOptimization), a => Assert.Equal(development.Id, a.ReleaseVersionId));
        Assert.Equal(released.Id, artifacts.Single(a => a.Id == RolledBackOptimization).ReleaseVersionId);
        var transitions = await db.StateTransitions.AsNoTracking().Where(t => t.ProjectId == RolledBack).ToListAsync(Ct);
        Assert.All(transitions.Where(t => t.Checkpoint <= 3), t => Assert.Equal(development.Id, t.ReleaseVersionId));
        Assert.All(transitions.Where(t => t.Checkpoint > 3), t => Assert.Equal(released.Id, t.ReleaseVersionId));
        Assert.Equal(released.Id, (await db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == RolledBack, Ct)).ReleaseVersionId);

        // v1 known issue folded in: the rollback written without restored_phase is backfilled (column now NOT NULL).
        var rollback = await db.RollbackEvents.AsNoTracking().SingleAsync(r => r.Id == RolledBackRollback, Ct);
        Assert.Equal((development.Id, 8, 3, Phase.Planning), (rollback.ReleaseVersionId, rollback.FromCheckpoint, rollback.TargetCheckpoint, rollback.RestoredPhase));
        Assert.Equal("NO", await IsNullableAsync(db, "rollback_events", "restored_phase"));
    }

    [Fact]
    public async Task FR67_step_attempts_are_backfilled_from_v1_step_events_and_agent_runs()
    {
        await using var db = await MigratedV1DatabaseAsync();

        var attempts = await db.ExecutionStepAttempts.AsNoTracking().Where(a => a.StepId == ReleasedStep).OrderBy(a => a.Attempt).ToListAsync(Ct);
        Assert.Equal(
            [(1, StepAttemptOutcome.Rejected, (Guid?)RunAttempt1), (2, StepAttemptOutcome.Accepted, RunAttempt2)],
            attempts.Select(a => (a.Attempt, a.Outcome, a.AgentRunId)));
        Assert.Equal(("a.txt", "W1"), (Assert.Single(attempts[1].ProducedFiles).Path, Assert.Single(attempts[1].Warnings).Code));
        Assert.Empty(attempts[1].Violations);
        Assert.Empty(attempts[0].ProducedFiles);

        var step = await db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == ReleasedStep, Ct);
        Assert.Null(step.AgentKey);
        Assert.Empty(step.Covers);
    }

    private static Task<int> ColumnCountAsync(AideDbContext db, string table, params string[] columns) =>
        db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = ANY({columns})")
            .SingleAsync(Ct);

    private static Task<string> IsNullableAsync(AideDbContext db, string table, string column) =>
        db.Database.SqlQuery<string>(
            $"SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = {column}")
            .SingleAsync(Ct);

    /// <summary>A new database at the last v1 migration, seeded with v1 rows, then migrated to the latest version.</summary>
    private async Task<AideDbContext> MigratedV1DatabaseAsync()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new AideDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(LastV1Migration, Ct);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = V1Seed;
            command.Parameters.AddWithValue("t0", T0);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await db.Database.MigrateAsync(Ct);
        return db;
    }

    /// <summary>
    /// Three v1 projects (ids end in a, b, c; timestamps are hours after @t0):
    /// <list type="bullet">
    /// <item>a: released once at checkpoint 7 as <c>v7-aide</c> (completion → checkpoint 8), GitHub PR #12, one step with a
    /// rejected then an accepted attempt.</item>
    /// <item>b: never released, in specification at checkpoint 2.</item>
    /// <item>c: released as <c>v7-aide</c>, then rolled back to checkpoint 3 (planning); the rollback row has no
    /// restored_phase (written by an old pod during a v1 deploy).</item>
    /// </list>
    /// </summary>
    private const string V1Seed =
        """
        INSERT INTO projects (id, name, slug, idea, current_phase, status, project_version, approval_mode, compliance_threshold,
                              max_files_per_step, max_review_cycles, tokens_used, execution_paused, created_at, updated_at)
        VALUES ('01990000-0000-7000-8000-00000000000a', 'Released', 'released', 'Released idea', 'release', 'released', 8, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0 + interval '7 hours'),
               ('01990000-0000-7000-8000-00000000000b', 'Progress', 'progress', 'Progress idea', 'specification', 'active', 2, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0 + interval '1 hour'),
               ('01990000-0000-7000-8000-00000000000c', 'Rolled back', 'rolled', 'Rolled idea', 'planning', 'rolled_back', 3, 'advisory', 0.9, 20, 3, 0, false, @t0, @t0 + interval '9 hours');

        INSERT INTO project_git_settings (project_id, provider, repo_url, default_branch, branch_name, pull_request_url, pull_request_number)
        VALUES ('01990000-0000-7000-8000-00000000000a', 'github', 'https://github.com/acme/released', 'main', 'aide/released-0199000a', 'https://github.com/acme/released/pull/12', 12),
               ('01990000-0000-7000-8000-00000000000b', 'local', NULL, 'main', 'aide/progress-0199000b', NULL, NULL),
               ('01990000-0000-7000-8000-00000000000c', 'local', NULL, 'main', 'aide/rolled-0199000c', NULL, NULL);

        -- Transitions: a and c went 1..7 (release at 7) and completed at 8; c then rolled back to 3; b reached 2.
        INSERT INTO state_transitions (id, project_id, from_phase, to_phase, actor, reason, project_version, created_at)
        SELECT gen_random_uuid(), p.id, t.from_phase, t.to_phase, 'aide-system', 'r', t.v, @t0 + make_interval(hours => t.h)
        FROM (VALUES ('01990000-0000-7000-8000-00000000000a'::uuid), ('01990000-0000-7000-8000-00000000000c'::uuid)) AS p(id)
        CROSS JOIN (VALUES (NULL, 'refinement', 1, 0), ('refinement', 'specification', 2, 1), ('specification', 'planning', 3, 2),
                           ('planning', 'execution', 4, 3), ('execution', 'review', 5, 4), ('review', 'execution', 5, 5),
                           ('execution', 'review', 6, 5), ('review', 'release', 7, 6), ('release', 'release', 8, 7)) AS t(from_phase, to_phase, v, h);
        INSERT INTO state_transitions (id, project_id, from_phase, to_phase, actor, reason, project_version, created_at)
        VALUES (gen_random_uuid(), '01990000-0000-7000-8000-00000000000b', NULL, 'refinement', 'aide-system', 'created', 1, @t0),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000b', 'refinement', 'specification', 'u', 'r', 2, @t0 + interval '1 hour'),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000c', 'release', 'planning', 'u', 'rollback', 3, @t0 + interval '9 hours');

        INSERT INTO artifacts (id, project_id, type, version, content, content_hash, status, project_version_at_creation, created_at)
        VALUES ('01990000-0000-7000-8000-0000000000a1', '01990000-0000-7000-8000-00000000000a', 'project_request', 1, 'r', 'h', 'locked', 1, @t0),
               ('01990000-0000-7000-8000-0000000000a2', '01990000-0000-7000-8000-00000000000a', 'technical_specification', 1, 's', 'h', 'locked', 2, @t0 + interval '1 hour'),
               ('01990000-0000-7000-8000-0000000000a3', '01990000-0000-7000-8000-00000000000a', 'implementation_plan', 1, 'p', 'h', 'locked', 3, @t0 + interval '2 hours'),
               ('01990000-0000-7000-8000-0000000000b1', '01990000-0000-7000-8000-00000000000b', 'project_request', 1, 'r', 'h', 'locked', 1, @t0),
               ('01990000-0000-7000-8000-0000000000c1', '01990000-0000-7000-8000-00000000000c', 'project_request', 1, 'r', 'h', 'locked', 1, @t0),
               ('01990000-0000-7000-8000-0000000000c2', '01990000-0000-7000-8000-00000000000c', 'technical_specification', 1, 's', 'h', 'locked', 2, @t0 + interval '1 hour'),
               ('01990000-0000-7000-8000-0000000000c3', '01990000-0000-7000-8000-00000000000c', 'implementation_plan', 1, 'p', 'h', 'locked', 3, @t0 + interval '2 hours'),
               ('01990000-0000-7000-8000-0000000000c5', '01990000-0000-7000-8000-00000000000c', 'optimization_plan', 1, 'o', 'h', 'invalidated', 5, @t0 + interval '5 hours');

        INSERT INTO approval_gates (id, project_id, type, artifact_id, status, overdue, created_at)
        VALUES (gen_random_uuid(), '01990000-0000-7000-8000-00000000000a', 'spec_approval', '01990000-0000-7000-8000-0000000000a2', 'approved', false, @t0 + interval '1 hour'),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000a', 'pre_release', NULL, 'approved', false, @t0 + interval '6 hours'),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000c', 'spec_approval', '01990000-0000-7000-8000-0000000000c2', 'approved', false, @t0 + interval '1 hour');

        INSERT INTO execution_steps (id, project_id, plan_artifact_id, step_number, title, task, declared_files, dependencies, status, attempt,
                                     violations, file_manifest, branch_name, commit_sha, started_at, completed_at)
        VALUES ('01990000-0000-7000-8000-0000000000a9', '01990000-0000-7000-8000-00000000000a', '01990000-0000-7000-8000-0000000000a3', 1, 'Step', 'Task',
                '[{"path":"a.txt","action":"create"}]', '{}', 'completed', 2,
                '[{"check":"scope_creep","code":"W1","severity":"warning","message":"w"}]',
                '[{"path":"a.txt","action":"create","contentHash":"abc"}]', 'aide/released-0199000a', 'c0ffee', @t0 + interval '3 hours', @t0 + interval '3 hours 30 minutes');

        INSERT INTO agent_runs (id, project_id, agent, phase, step_id, provider, model, prompt_version, input_artifacts, status, attempts,
                                input_tokens, output_tokens, cost_estimate, duration_ms, created_at)
        VALUES ('01990000-0000-7000-8000-0000000000b1', '01990000-0000-7000-8000-00000000000a', 'code_generator', 'execution', '01990000-0000-7000-8000-0000000000a9',
                'fake', 'm', 'code-generator@1', '[]', 'succeeded', 1, 1, 1, 0, 1, @t0 + interval '3 hours 5 minutes'),
               ('01990000-0000-7000-8000-0000000000b2', '01990000-0000-7000-8000-00000000000a', 'code_generator', 'execution', '01990000-0000-7000-8000-0000000000a9',
                'fake', 'm', 'code-generator@1', '[]', 'succeeded', 1, 1, 1, 0, 1, @t0 + interval '3 hours 20 minutes');

        INSERT INTO events (id, project_id, type, payload, actor, created_at)
        VALUES (gen_random_uuid(), '01990000-0000-7000-8000-00000000000a', 'STEP_REJECTED',
                '{"stepId":"01990000-0000-7000-8000-0000000000a9","attempt":1,"retry":true}', 'aide-system', @t0 + interval '3 hours 10 minutes'),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000a', 'STEP_COMPLETED',
                '{"stepId":"01990000-0000-7000-8000-0000000000a9","attempt":2}', 'aide-system', @t0 + interval '3 hours 30 minutes');

        INSERT INTO releases (id, project_id, project_version, tag, merge_commit_sha, pr_url, status, conflicting_files, attempt, created_at, completed_at)
        VALUES (gen_random_uuid(), '01990000-0000-7000-8000-00000000000a', 7, 'v7-aide', 'abc1234', 'https://github.com/acme/released/pull/12', 'succeeded', '{}', 1,
                @t0 + interval '6 hours', @t0 + interval '7 hours'),
               (gen_random_uuid(), '01990000-0000-7000-8000-00000000000c', 7, 'v7-aide', 'def5678', NULL, 'succeeded', '{}', 1,
                @t0 + interval '6 hours', @t0 + interval '7 hours');

        INSERT INTO rollback_events (id, project_id, from_version, target_version, restored_phase, reason, invalidated_artifact_ids, reset_step_ids,
                                     triggered_by, created_at)
        VALUES ('01990000-0000-7000-8000-0000000000cf', '01990000-0000-7000-8000-00000000000c', 8, 3, NULL, 'redo', '{01990000-0000-7000-8000-0000000000c5}', '{}',
                'u', @t0 + interval '9 hours');
        """;
}
