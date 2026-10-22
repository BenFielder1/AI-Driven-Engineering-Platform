using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// SPEC §4.10 / FR-67: release versions and per-version checkpoints, with the v1 backfill.
    /// <list type="number">
    /// <item>v1 <c>project_version*</c> columns become <c>checkpoint*</c> (renamed, data kept).</item>
    /// <item>New tables <c>release_versions</c> and <c>execution_step_attempts</c>; new columns on projects, artifacts,
    /// steps, gates, transitions, reports, rollbacks, releases and agent runs.</item>
    /// <item>Backfill: the i-th succeeded v1 release of a project becomes released version <c>0.i.0</c> (its v1 tag kept);
    /// a project not currently released also gets <c>0.(k+1).0</c> in development, holding its current state. Every row is
    /// assigned to a version by time window, and the live state of a project rolled back after a release moves to the
    /// version in development. Branch and PR fields move from project_git_settings to the versions.</item>
    /// <item>Contract: release_version_id becomes NOT NULL (artifacts, steps, gates, transitions, reports, rollbacks,
    /// releases), and <c>rollback_events.restored_phase</c> NOT NULL (v1 known issue, backfill re-run first).</item>
    /// </list>
    /// Not rolling-update safe (renames and drops): v1 pods can't run against the new schema. The local deployment uses the
    /// Recreate strategy and runs this before the new API starts.
    /// </summary>
    public partial class ReleaseVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Renames: v1 project versions become checkpoints (values unchanged) ----
            migrationBuilder.DropIndex(
                name: "ux_releases_project_id_project_version_attempt",
                table: "releases");

            migrationBuilder.RenameColumn(name: "project_version", table: "projects", newName: "checkpoint");
            migrationBuilder.RenameColumn(name: "project_version_at_creation", table: "artifacts", newName: "checkpoint_at_creation");
            migrationBuilder.RenameColumn(name: "target_version", table: "approval_gates", newName: "target_checkpoint");
            migrationBuilder.RenameColumn(name: "project_version", table: "state_transitions", newName: "checkpoint");
            migrationBuilder.RenameColumn(name: "project_version", table: "compliance_reports", newName: "checkpoint");
            migrationBuilder.RenameColumn(name: "from_version", table: "rollback_events", newName: "from_checkpoint");
            migrationBuilder.RenameColumn(name: "target_version", table: "rollback_events", newName: "target_checkpoint");
            migrationBuilder.RenameColumn(name: "project_version", table: "releases", newName: "checkpoint");

            // ---- 2. New tables ----
            migrationBuilder.CreateTable(
                name: "release_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    tag_prefix = table.Column<string>(type: "text", nullable: false, defaultValue: "v"),
                    base_tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    base_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    merge_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    branch_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    pull_request_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    pull_request_number = table.Column<int>(type: "integer", nullable: true),
                    request = table.Column<string>(type: "text", nullable: false),
                    preset_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    preset_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    preset_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_release_versions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_release_versions_users_started_by",
                        column: x => x.started_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_release_versions_project_id_version",
                table: "release_versions",
                columns: new[] { "project_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_release_versions_started_by",
                table: "release_versions",
                column: "started_by");

            migrationBuilder.CreateIndex(
                name: "ux_release_versions_project_id_open",
                table: "release_versions",
                column: "project_id",
                unique: true,
                filter: "status IN ('in_development', 'awaiting_merge')");

            migrationBuilder.CreateTable(
                name: "execution_step_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    violations = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    produced_files = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execution_step_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_execution_step_attempts_agent_runs_agent_run_id",
                        column: x => x.agent_run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_execution_step_attempts_execution_steps_step_id",
                        column: x => x.step_id,
                        principalTable: "execution_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_execution_step_attempts_agent_run_id",
                table: "execution_step_attempts",
                column: "agent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_execution_step_attempts_step_id_attempt",
                table: "execution_step_attempts",
                columns: new[] { "step_id", "attempt" },
                unique: true);

            // ---- 3. New columns. release_version_id starts NULLABLE and is set NOT NULL after the backfill ----
            foreach (var table in VersionedTables)
            {
                migrationBuilder.AddColumn<Guid>(name: "release_version_id", table: table, type: "uuid", nullable: true);
            }

            migrationBuilder.AddColumn<Guid>(name: "release_version_id", table: "agent_runs", type: "uuid", nullable: true);

            migrationBuilder.AddColumn<Guid>(name: "current_release_version_id", table: "projects", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "development_version", table: "projects", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "released_version", table: "projects", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "merge_strategy",
                table: "projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "aide_merges");
            migrationBuilder.AddColumn<string>(name: "tag_prefix", table: "projects", type: "text", nullable: false, defaultValue: "v");

            migrationBuilder.AddColumn<int>(name: "restored_from_version", table: "artifacts", type: "integer", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "agent_key",
                table: "execution_steps",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
            migrationBuilder.AddColumn<string[]>(
                name: "covers",
                table: "execution_steps",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "agent_key",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "credential_id", table: "agent_runs", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "preset_id",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "preset_version",
                table: "agent_runs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "tool_calls",
                table: "agent_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(name: "external_checks", table: "compliance_reports", type: "jsonb", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "regressions",
                table: "compliance_reports",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "awaiting_merge_since",
                table: "releases",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "github_release_url",
                table: "releases",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
            migrationBuilder.AddColumn<string>(name: "merge_reasons", table: "releases", type: "jsonb", nullable: true);

            // ---- 4. Backfill (FR-67) ----
            migrationBuilder.Sql(CreateUuid7);
            migrationBuilder.Sql(InsertReleaseVersions);
            migrationBuilder.Sql(AssignByWindow);
            migrationBuilder.Sql(CarryLiveStateForward);
            migrationBuilder.Sql(SetProjectVersions);
            migrationBuilder.Sql(BackfillStepAttempts);
            migrationBuilder.Sql(BackfillRestoredPhase);
            migrationBuilder.Sql("DROP FUNCTION pg_temp.aide_uuid7(timestamptz);");

            // ---- 5. Contract ----
            migrationBuilder.DropColumn(name: "branch_name", table: "project_git_settings");
            migrationBuilder.DropColumn(name: "pull_request_number", table: "project_git_settings");
            migrationBuilder.DropColumn(name: "pull_request_url", table: "project_git_settings");

            migrationBuilder.Sql(
                """
                ALTER TABLE artifacts ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE execution_steps ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE approval_gates ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE state_transitions ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE compliance_reports ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE rollback_events ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE releases ALTER COLUMN release_version_id SET NOT NULL;
                ALTER TABLE rollback_events ALTER COLUMN restored_phase SET NOT NULL;
                """);

            // ---- 6. Indexes and foreign keys ----
            migrationBuilder.CreateIndex(
                name: "ux_releases_release_version_id_checkpoint_attempt",
                table: "releases",
                columns: new[] { "release_version_id", "checkpoint", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_current_release_version_id",
                table: "projects",
                column: "current_release_version_id");

            foreach (var table in VersionedTables.Append("agent_runs"))
            {
                if (table != "releases")
                {
                    migrationBuilder.CreateIndex(
                        name: $"ix_{table}_release_version_id",
                        table: table,
                        column: "release_version_id");
                }

                migrationBuilder.CreateIndex(
                    name: $"ix_{table}_project_id_release_version_id",
                    table: table,
                    columns: new[] { "project_id", "release_version_id" });

                migrationBuilder.AddForeignKey(
                    name: $"fk_{table}_release_versions_release_version_id",
                    table: table,
                    column: "release_version_id",
                    principalTable: "release_versions",
                    principalColumn: "id");
            }

            // Not mapped as an EF relationship: a project and its first version reference each other and are inserted in
            // one SaveChanges, so the check is deferred to commit.
            migrationBuilder.Sql(
                """
                ALTER TABLE projects
                    ADD CONSTRAINT fk_projects_release_versions_current_release_version_id
                    FOREIGN KEY (current_release_version_id) REFERENCES release_versions (id)
                    ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE projects DROP CONSTRAINT fk_projects_release_versions_current_release_version_id;");

            foreach (var table in VersionedTables.Append("agent_runs"))
            {
                migrationBuilder.DropForeignKey(name: $"fk_{table}_release_versions_release_version_id", table: table);
                migrationBuilder.DropIndex(name: $"ix_{table}_project_id_release_version_id", table: table);
                if (table != "releases")
                {
                    migrationBuilder.DropIndex(name: $"ix_{table}_release_version_id", table: table);
                }
            }

            migrationBuilder.DropIndex(name: "ix_projects_current_release_version_id", table: "projects");
            migrationBuilder.DropIndex(name: "ux_releases_release_version_id_checkpoint_attempt", table: "releases");

            // Branch and PR fields go back to the Git settings, from the project's current version.
            migrationBuilder.AddColumn<string>(
                name: "branch_name",
                table: "project_git_settings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
            migrationBuilder.AddColumn<int>(name: "pull_request_number", table: "project_git_settings", type: "integer", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "pull_request_url",
                table: "project_git_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
            migrationBuilder.Sql(
                """
                UPDATE project_git_settings AS g
                SET branch_name = v.branch_name, pull_request_number = v.pull_request_number, pull_request_url = v.pull_request_url
                FROM projects AS p
                JOIN release_versions AS v ON v.id = p.current_release_version_id
                WHERE p.id = g.project_id;
                """);

            migrationBuilder.DropTable(name: "execution_step_attempts");

            foreach (var table in VersionedTables.Append("agent_runs"))
            {
                migrationBuilder.DropColumn(name: "release_version_id", table: table);
            }

            migrationBuilder.DropTable(name: "release_versions");

            migrationBuilder.DropColumn(name: "current_release_version_id", table: "projects");
            migrationBuilder.DropColumn(name: "development_version", table: "projects");
            migrationBuilder.DropColumn(name: "released_version", table: "projects");
            migrationBuilder.DropColumn(name: "merge_strategy", table: "projects");
            migrationBuilder.DropColumn(name: "tag_prefix", table: "projects");
            migrationBuilder.DropColumn(name: "restored_from_version", table: "artifacts");
            migrationBuilder.DropColumn(name: "agent_key", table: "execution_steps");
            migrationBuilder.DropColumn(name: "covers", table: "execution_steps");
            migrationBuilder.DropColumn(name: "agent_key", table: "agent_runs");
            migrationBuilder.DropColumn(name: "credential_id", table: "agent_runs");
            migrationBuilder.DropColumn(name: "preset_id", table: "agent_runs");
            migrationBuilder.DropColumn(name: "preset_version", table: "agent_runs");
            migrationBuilder.DropColumn(name: "tool_calls", table: "agent_runs");
            migrationBuilder.DropColumn(name: "external_checks", table: "compliance_reports");
            migrationBuilder.DropColumn(name: "regressions", table: "compliance_reports");
            migrationBuilder.DropColumn(name: "awaiting_merge_since", table: "releases");
            migrationBuilder.DropColumn(name: "github_release_url", table: "releases");
            migrationBuilder.DropColumn(name: "merge_reasons", table: "releases");

            // v1 rows can't hold v2 states: awaiting_merge attempts and codebase summaries have no v1 meaning.
            migrationBuilder.Sql(
                """
                UPDATE releases SET status = 'failed', error_code = 'RELEASE_CANCELLED', completed_at = COALESCE(completed_at, created_at)
                WHERE status = 'awaiting_merge';
                ALTER TABLE rollback_events ALTER COLUMN restored_phase DROP NOT NULL;
                """);

            migrationBuilder.RenameColumn(name: "checkpoint", table: "projects", newName: "project_version");
            migrationBuilder.RenameColumn(name: "checkpoint_at_creation", table: "artifacts", newName: "project_version_at_creation");
            migrationBuilder.RenameColumn(name: "target_checkpoint", table: "approval_gates", newName: "target_version");
            migrationBuilder.RenameColumn(name: "checkpoint", table: "state_transitions", newName: "project_version");
            migrationBuilder.RenameColumn(name: "checkpoint", table: "compliance_reports", newName: "project_version");
            migrationBuilder.RenameColumn(name: "from_checkpoint", table: "rollback_events", newName: "from_version");
            migrationBuilder.RenameColumn(name: "target_checkpoint", table: "rollback_events", newName: "target_version");
            migrationBuilder.RenameColumn(name: "checkpoint", table: "releases", newName: "project_version");

            // Checkpoints repeat across release versions; v1 numbers attempts per project version, so renumber first.
            migrationBuilder.Sql(
                """
                UPDATE releases AS r
                SET attempt = n.attempt
                FROM (
                    SELECT id, row_number() OVER (PARTITION BY project_id, project_version ORDER BY created_at, id) AS attempt
                    FROM releases) AS n
                WHERE r.id = n.id AND r.attempt <> n.attempt;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_releases_project_id_project_version_attempt",
                table: "releases",
                columns: new[] { "project_id", "project_version", "attempt" },
                unique: true);
        }

        /// <summary>Tables whose rows belong to a release version (§4.10), release_version_id NOT NULL.</summary>
        private static readonly string[] VersionedTables =
        [
            "artifacts", "execution_steps", "approval_gates", "state_transitions", "compliance_reports", "rollback_events", "releases",
        ];

        /// <summary>Time-ordered UUIDv7 ids for backfilled rows (PostgreSQL 17 has no uuidv7()); session-local, dropped after use.</summary>
        private const string CreateUuid7 =
            """
            CREATE FUNCTION pg_temp.aide_uuid7(ts timestamptz) RETURNS uuid LANGUAGE sql VOLATILE AS $f$
                SELECT (lpad(to_hex(floor(extract(epoch FROM ts) * 1000)::bigint), 12, '0')
                    || '7' || substr(md5(random()::text), 1, 3)
                    || substr('89ab', floor(random() * 4)::int + 1, 1)
                    || substr(md5(random()::text), 1, 15))::uuid
            $f$;
            """;

        /// <summary>
        /// FR-67 release versions. The i-th succeeded release of a project (in completion order) becomes <c>0.i.0</c>
        /// <c>released</c> with its v1 tag as-is, its merge commit and PR; base = the previous release. A project that isn't
        /// <c>released</c> (or, defensively, has no succeeded release) gets <c>0.(k+1).0</c> <c>in_development</c> with the v1
        /// branch and the Git settings' PR. A released project's latest version takes the Git settings' PR fields.
        /// </summary>
        private const string InsertReleaseVersions =
            """
            WITH released AS (
                SELECT r.project_id, r.tag, r.merge_commit_sha, r.pr_url,
                       COALESCE(r.completed_at, r.created_at) AS released_at,
                       row_number() OVER w AS i,
                       lag(r.tag) OVER w AS base_tag,
                       lag(r.merge_commit_sha) OVER w AS base_commit_sha,
                       lag(COALESCE(r.completed_at, r.created_at)) OVER w AS previous_released_at
                FROM releases AS r
                WHERE r.status = 'succeeded'
                WINDOW w AS (PARTITION BY r.project_id ORDER BY COALESCE(r.completed_at, r.created_at), r.created_at, r.id)
            )
            INSERT INTO release_versions (id, project_id, version, status, tag, tag_prefix, base_tag, base_commit_sha, merge_commit_sha,
                                          branch_name, pull_request_url, pull_request_number, request, created_at, released_at)
            SELECT pg_temp.aide_uuid7(COALESCE(x.previous_released_at, p.created_at)), x.project_id, '0.' || x.i || '.0', 'released',
                   x.tag, 'v', x.base_tag, x.base_commit_sha, x.merge_commit_sha, g.branch_name, x.pr_url,
                   CASE WHEN g.pull_request_url = x.pr_url THEN g.pull_request_number END,
                   p.idea, COALESCE(x.previous_released_at, p.created_at), x.released_at
            FROM released AS x
            JOIN projects AS p ON p.id = x.project_id
            LEFT JOIN project_git_settings AS g ON g.project_id = x.project_id;

            INSERT INTO release_versions (id, project_id, version, status, tag, tag_prefix, base_tag, base_commit_sha, merge_commit_sha,
                                          branch_name, pull_request_url, pull_request_number, request, created_at, released_at)
            SELECT pg_temp.aide_uuid7(COALESCE(last.released_at, p.created_at)), p.id, '0.' || (COALESCE(last.k, 0) + 1) || '.0',
                   'in_development', NULL, 'v', last.tag, last.merge_commit_sha, NULL, g.branch_name, g.pull_request_url,
                   g.pull_request_number, p.idea, COALESCE(last.released_at, p.created_at), NULL
            FROM projects AS p
            LEFT JOIN project_git_settings AS g ON g.project_id = p.id
            LEFT JOIN LATERAL (
                SELECT v.tag, v.merge_commit_sha, v.released_at, count(*) OVER () AS k
                FROM release_versions AS v
                WHERE v.project_id = p.id
                ORDER BY v.released_at DESC
                LIMIT 1) AS last ON true
            WHERE p.status <> 'released' OR last.k IS NULL;

            UPDATE release_versions AS v
            SET pull_request_url = COALESCE(v.pull_request_url, g.pull_request_url),
                pull_request_number = COALESCE(v.pull_request_number,
                    CASE WHEN v.pull_request_url IS NULL OR v.pull_request_url = g.pull_request_url THEN g.pull_request_number END)
            FROM project_git_settings AS g
            WHERE g.project_id = v.project_id
              AND v.status = 'released'
              AND NOT EXISTS (SELECT 1 FROM release_versions AS d WHERE d.project_id = v.project_id AND d.status = 'in_development')
              AND v.released_at = (SELECT max(o.released_at) FROM release_versions AS o WHERE o.project_id = v.project_id);
            """;

        /// <summary>
        /// Every row goes to the version whose time window holds its <c>created_at</c>: version i covers
        /// (released_at of i-1, released_at of i]; the project's last version is open-ended. A succeeded release, its gate
        /// and its completion transition (same timestamp) therefore land in the version they released. Steps follow their
        /// plan artifact (done after the carry-over), agent runs their window.
        /// </summary>
        private const string AssignByWindow =
            """
            CREATE TABLE _aide_release_version_windows AS
            SELECT v.id, v.project_id,
                   lag(v.released_at) OVER w AS starts_after,
                   CASE WHEN lead(v.id) OVER w IS NULL THEN NULL ELSE v.released_at END AS ends_at
            FROM release_versions AS v
            WINDOW w AS (PARTITION BY v.project_id ORDER BY v.released_at NULLS LAST, v.created_at, v.id);

            UPDATE artifacts AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE approval_gates AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE state_transitions AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE compliance_reports AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE rollback_events AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE releases AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);
            UPDATE agent_runs AS x SET release_version_id = w.id FROM _aide_release_version_windows AS w
            WHERE w.project_id = x.project_id AND (w.starts_after IS NULL OR x.created_at > w.starts_after) AND (w.ends_at IS NULL OR x.created_at <= w.ends_at);

            DROP TABLE _aide_release_version_windows;
            """;

        /// <summary>
        /// A v1 project rolled back after a release (status no longer <c>released</c>) continues from checkpoint T, the target
        /// of its first rollback after that release; its live state was created before the release. So that the version in
        /// development holds that state (FR-67), these move to it: non-invalidated artifacts at checkpoint ≤ T, transitions
        /// and reports at checkpoint ≤ T (rollback targets need the transitions), gates on moved artifacts and runs that
        /// produced them. Releases and rollback events stay where they happened. Then steps follow their plan, and agent
        /// runs of a step follow the step.
        /// </summary>
        private const string CarryLiveStateForward =
            """
            CREATE TABLE _aide_release_version_carry AS
            SELECT d.project_id, d.id AS version_id, (
                       SELECT rb.target_checkpoint FROM rollback_events AS rb
                       WHERE rb.project_id = d.project_id AND rb.release_version_id = d.id
                       ORDER BY rb.created_at, rb.id
                       LIMIT 1) AS target
            FROM release_versions AS d
            WHERE d.status = 'in_development'
              AND EXISTS (SELECT 1 FROM release_versions AS r WHERE r.project_id = d.project_id AND r.status = 'released');

            DELETE FROM _aide_release_version_carry WHERE target IS NULL;

            UPDATE artifacts AS a SET release_version_id = c.version_id FROM _aide_release_version_carry AS c
            WHERE a.project_id = c.project_id AND a.checkpoint_at_creation <= c.target AND a.status <> 'invalidated'
              AND a.release_version_id <> c.version_id;
            UPDATE state_transitions AS t SET release_version_id = c.version_id FROM _aide_release_version_carry AS c
            WHERE t.project_id = c.project_id AND t.checkpoint <= c.target AND t.release_version_id <> c.version_id;
            UPDATE compliance_reports AS r SET release_version_id = c.version_id FROM _aide_release_version_carry AS c
            WHERE r.project_id = c.project_id AND r.checkpoint <= c.target AND r.release_version_id <> c.version_id;
            UPDATE approval_gates AS g SET release_version_id = a.release_version_id
            FROM artifacts AS a JOIN _aide_release_version_carry AS c ON c.version_id = a.release_version_id
            WHERE g.artifact_id = a.id AND g.release_version_id <> a.release_version_id;
            UPDATE agent_runs AS r SET release_version_id = a.release_version_id
            FROM artifacts AS a JOIN _aide_release_version_carry AS c ON c.version_id = a.release_version_id
            WHERE a.agent_run_id = r.id AND r.release_version_id IS DISTINCT FROM a.release_version_id;

            DROP TABLE _aide_release_version_carry;

            UPDATE execution_steps AS s SET release_version_id = a.release_version_id
            FROM artifacts AS a WHERE a.id = s.plan_artifact_id;
            UPDATE agent_runs AS r SET release_version_id = s.release_version_id
            FROM execution_steps AS s WHERE r.step_id = s.id AND r.release_version_id IS DISTINCT FROM s.release_version_id;
            """;

        /// <summary>Denormalised project fields (§5): current = the version in development, else the latest released.</summary>
        private const string SetProjectVersions =
            """
            UPDATE projects AS p
            SET development_version = (
                    SELECT v.version FROM release_versions AS v WHERE v.project_id = p.id AND v.status = 'in_development'),
                released_version = (
                    SELECT v.version FROM release_versions AS v WHERE v.project_id = p.id AND v.status = 'released'
                    ORDER BY v.released_at DESC, v.created_at DESC LIMIT 1),
                current_release_version_id = (
                    SELECT v.id FROM release_versions AS v WHERE v.project_id = p.id
                    ORDER BY v.released_at DESC NULLS FIRST, v.created_at DESC LIMIT 1);
            """;

        /// <summary>
        /// FR-57 attempt history from the v1 step events: one attempt per (step, attempt) from its latest
        /// <c>STEP_COMPLETED</c> (accepted) or <c>STEP_REJECTED</c> (rejected; <c>interrupted</c> → error), linked to the
        /// step's latest agent run created during that attempt. Violations, warnings and produced files are only known for a
        /// step's current attempt (from the step row); earlier attempts get empty lists.
        /// </summary>
        private const string BackfillStepAttempts =
            """
            WITH outcomes AS (
                SELECT DISTINCT ON ((e.payload->>'stepId')::uuid, (e.payload->>'attempt')::int)
                       (e.payload->>'stepId')::uuid AS step_id,
                       (e.payload->>'attempt')::int AS attempt,
                       e.type,
                       COALESCE(e.payload->'interrupted' = 'true'::jsonb, false) AS interrupted,
                       e.created_at
                FROM events AS e
                WHERE e.type IN ('STEP_COMPLETED', 'STEP_REJECTED')
                  AND e.payload->>'stepId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                  AND jsonb_typeof(e.payload->'attempt') = 'number'
                  AND (e.payload->>'attempt') ~ '^[0-9]{1,9}$'
                ORDER BY (e.payload->>'stepId')::uuid, (e.payload->>'attempt')::int, e.created_at DESC, e.id DESC
            ), ordered AS (
                SELECT o.*, lag(o.created_at) OVER (PARTITION BY o.step_id ORDER BY o.created_at) AS previous_at
                FROM outcomes AS o
            )
            INSERT INTO execution_step_attempts (id, step_id, attempt, agent_run_id, outcome, violations, warnings, produced_files, created_at)
            SELECT pg_temp.aide_uuid7(o.created_at), s.id, o.attempt, run.id,
                   CASE WHEN o.type = 'STEP_COMPLETED' THEN 'accepted' WHEN o.interrupted THEN 'error' ELSE 'rejected' END,
                   CASE WHEN o.type = 'STEP_REJECTED' AND NOT o.interrupted AND o.attempt = s.attempt AND jsonb_typeof(s.violations) = 'array'
                        THEN COALESCE((SELECT jsonb_agg(v) FROM jsonb_array_elements(s.violations) AS v WHERE v->>'severity' = 'error'), '[]'::jsonb)
                        ELSE '[]'::jsonb END,
                   CASE WHEN NOT o.interrupted AND o.attempt = s.attempt AND jsonb_typeof(s.violations) = 'array'
                        THEN COALESCE((SELECT jsonb_agg(v) FROM jsonb_array_elements(s.violations) AS v WHERE v->>'severity' = 'warning'), '[]'::jsonb)
                        ELSE '[]'::jsonb END,
                   CASE WHEN o.type = 'STEP_COMPLETED' AND o.attempt = s.attempt AND s.status = 'completed' AND jsonb_typeof(s.file_manifest) = 'array'
                        THEN s.file_manifest ELSE '[]'::jsonb END,
                   o.created_at
            FROM ordered AS o
            JOIN execution_steps AS s ON s.id = o.step_id
            LEFT JOIN LATERAL (
                SELECT r.id FROM agent_runs AS r
                WHERE r.step_id = s.id AND r.created_at <= o.created_at AND (o.previous_at IS NULL OR r.created_at > o.previous_at)
                ORDER BY r.created_at DESC, r.id DESC
                LIMIT 1) AS run ON true;
            """;

        /// <summary>v1 known issue: re-run the ReleaseAndRollbackDetails backfill for rows written without restored_phase.</summary>
        private const string BackfillRestoredPhase =
            """
            UPDATE rollback_events AS r
            SET restored_phase = e.payload->>'phase'
            FROM events AS e
            WHERE e.type = 'ROLLBACK_COMPLETED'
              AND e.project_id = r.project_id
              AND e.payload->>'rollbackId' = r.id::text
              AND jsonb_typeof(e.payload->'phase') = 'string'
              AND r.restored_phase IS NULL;

            UPDATE rollback_events AS r
            SET restored_phase = (
                SELECT t.to_phase FROM state_transitions AS t
                WHERE t.project_id = r.project_id AND t.created_at = r.created_at
                ORDER BY t.id DESC LIMIT 1)
            WHERE r.restored_phase IS NULL;

            UPDATE rollback_events AS r
            SET restored_phase = p.current_phase
            FROM projects AS p
            WHERE p.id = r.project_id AND r.restored_phase IS NULL;
            """;
    }
}
