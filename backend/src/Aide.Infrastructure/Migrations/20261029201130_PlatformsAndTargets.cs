using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// FR-142, FR-144, FR-145, FR-151 (§5, M29): generic platform connections and deployment targets.
    /// <list type="bullet">
    /// <item><c>platform_connections</c> replaces <c>vercel_connections</c>: every v2 row is copied with the <b>same id</b>
    /// (so target bindings survive), provider <c>vercel</c>, the ciphertext unchanged (same Data Protection purpose), the
    /// Vercel user as the account and the team in <c>metadata</c>.</item>
    /// <item><c>deployment_targets</c> replaces <c>project_deployment_settings</c>: each v2 row becomes the project's
    /// <c>frontend</c> target (Vercel project id/name → resource id/name, framework → <c>build_config</c>).</item>
    /// <item><c>deployment_env_vars</c> gains <c>applies_to</c> (v2: <c>{frontend}</c>), <c>public</c> (v2: key starts with
    /// <c>NEXT_PUBLIC_</c>), <c>source</c> (v2: <c>user</c>) and <c>derived_from</c>.</item>
    /// <item><c>deployments</c>: <c>target</c> is renamed <c>environment</c> (values kept); + <c>target_id</c> (the project's
    /// frontend target), <c>component</c> (<c>frontend</c>), <c>kind</c> (<c>promote</c> for promote rows, else
    /// <c>deploy</c>) and <c>rollout_id</c>. The automatic-production unique index moves from <c>(release_id, target)</c> to
    /// <c>(release_id, component)</c> with the same filter (every v2 row is a frontend row, so no new conflicts).</item>
    /// </list>
    /// Not safe for rolling updates (§5 migration notes: Recreate on Kubernetes, one replica migrating in pre-deploy on
    /// Railway). Down restores the v2 tables while every connection is Vercel's and every target a frontend one.
    /// </summary>
    public partial class PlatformsAndTargets : Migration
    {
        /// <summary>Time-ordered UUIDv7 ids for backfilled rows (as in GitHosts); session-local, dropped after use.</summary>
        private const string CreateUuid7 =
            """
            CREATE FUNCTION pg_temp.aide_uuid7(ts timestamptz) RETURNS uuid LANGUAGE sql VOLATILE AS $f$
                SELECT (lpad(to_hex(floor(extract(epoch FROM ts) * 1000)::bigint), 12, '0')
                    || '7' || substr(md5(random()::text), 1, 3)
                    || substr('89ab', floor(random() * 4)::int + 1, 1)
                    || substr(md5(random()::text), 1, 15))::uuid
            $f$;
            """;

        /// <summary>v2 Vercel connections, same ids; the team goes into metadata (camelCase, as the app writes it).</summary>
        public const string CopyConnectionsSql =
            """
            INSERT INTO platform_connections (id, user_id, provider, label, secret_encrypted, last4, account_id, account_name,
                                              metadata, scopes, expires_at, status, last_validated_at, created_at)
            SELECT c.id, c.user_id, 'vercel', c.label, c.token_encrypted, c.last4, NULLIF(c.vercel_user_id, ''), c.vercel_username,
                   jsonb_strip_nulls(jsonb_build_object('teamId', c.team_id, 'teamSlug', c.team_slug)),
                   '{}'::text[], c.expires_at, c.status, c.last_validated_at, c.created_at
            FROM vercel_connections AS c;
            """;

        /// <summary>
        /// v2 settings → the project's frontend target. Status as <c>DeploymentTargetRules.StatusOf</c>: ready with a binding
        /// and a Vercel project, attention (<c>connection_missing</c>) with a project but no binding, else not_configured.
        /// </summary>
        public const string CopyTargetsSql =
            """
            INSERT INTO deployment_targets (id, project_id, component, provider, connection_id, bound_by_user_id, resource_id,
                                            preview_resource_id, name, root_directory, build_config, production_url, preview_url,
                                            status, status_reason, updated_at)
            SELECT pg_temp.aide_uuid7(s.updated_at), s.project_id, 'frontend', s.provider, s.connection_id, s.bound_by_user_id,
                   s.vercel_project_id, NULL, s.vercel_project_name, s.root_directory,
                   jsonb_build_object('framework', s.framework), s.production_url, NULL,
                   CASE
                       WHEN s.vercel_project_id IS NOT NULL AND s.connection_id IS NOT NULL THEN 'ready'
                       WHEN s.vercel_project_id IS NOT NULL THEN 'attention'
                       ELSE 'not_configured'
                   END,
                   CASE WHEN s.vercel_project_id IS NOT NULL AND s.connection_id IS NULL THEN 'connection_missing' END,
                   s.updated_at
            FROM project_deployment_settings AS s;
            """;

        /// <summary>v2 env vars: Owner-entered Next.js frontend variables; <c>NEXT_PUBLIC_</c> ones are public.</summary>
        public const string BackfillEnvVarsSql =
            """
            UPDATE deployment_env_vars SET public = (key LIKE 'NEXT\_PUBLIC\_%');
            ALTER TABLE deployment_env_vars ALTER COLUMN applies_to DROP DEFAULT;
            ALTER TABLE deployment_env_vars ALTER COLUMN source DROP DEFAULT;
            """;

        /// <summary>v2 deployments: frontend rows of the project's (only) target; promote rows are <c>promote</c>.</summary>
        public const string BackfillDeploymentsSql =
            """
            UPDATE deployments AS d SET target_id = t.id
            FROM deployment_targets AS t
            WHERE t.project_id = d.project_id AND t.component = 'frontend';
            UPDATE deployments SET kind = CASE WHEN trigger = 'promote' THEN 'promote' ELSE 'deploy' END;
            ALTER TABLE deployments ALTER COLUMN component DROP DEFAULT;
            ALTER TABLE deployments ALTER COLUMN kind DROP DEFAULT;
            """;

        /// <summary>Down can only restore what v2 can hold: Vercel connections, frontend targets of Vercel or the Fake, frontend
        /// deployments and Owner-entered frontend env vars.</summary>
        private const string GuardDownSql =
            """
            DO $g$
            BEGIN
                IF EXISTS (SELECT 1 FROM platform_connections WHERE provider <> 'vercel')
                   OR EXISTS (SELECT 1 FROM deployment_targets WHERE component <> 'frontend' OR provider NOT IN ('vercel', 'fake'))
                   OR EXISTS (SELECT 1 FROM deployments WHERE component <> 'frontend' OR kind = 'rollback')
                   OR EXISTS (SELECT 1 FROM deployment_env_vars WHERE source = 'derived' OR NOT ('frontend' = ANY (applies_to))) THEN
                    RAISE EXCEPTION 'PlatformsAndTargets Down: non-Vercel connections, backend targets, rollback deployments, derived or backend-only env vars exist; remove them first';
                END IF;
            END
            $g$;
            """;

        private const string RestoreConnectionsSql =
            """
            INSERT INTO vercel_connections (id, user_id, label, token_encrypted, last4, vercel_user_id, vercel_username, team_id,
                                            team_slug, scope, expires_at, status, last_validated_at, created_at)
            SELECT c.id, c.user_id, c.label, c.secret_encrypted, c.last4, COALESCE(c.account_id, ''), c.account_name,
                   c.metadata ->> 'teamId', c.metadata ->> 'teamSlug',
                   CASE WHEN c.metadata ->> 'teamId' IS NULL THEN 'account' ELSE 'team' END,
                   c.expires_at, CASE WHEN c.status = 'insufficient_scope' THEN 'invalid' ELSE c.status END,
                   c.last_validated_at, c.created_at
            FROM platform_connections AS c;
            """;

        private const string RestoreSettingsSql =
            """
            INSERT INTO project_deployment_settings (project_id, provider, connection_id, bound_by_user_id, vercel_project_id,
                                                     vercel_project_name, root_directory, framework, production_url, updated_at)
            SELECT t.project_id, t.provider, t.connection_id, t.bound_by_user_id, t.resource_id, t.name, t.root_directory,
                   COALESCE(t.build_config ->> 'framework', 'nextjs'), t.production_url, t.updated_at
            FROM deployment_targets AS t
            WHERE t.component = 'frontend';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. platform_connections, filled from vercel_connections (same ids).
            migrationBuilder.CreateTable(
                name: "platform_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    secret_encrypted = table.Column<string>(type: "text", nullable: false),
                    last4 = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: true),
                    account_name = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_connections", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_connections_user_id_provider",
                table: "platform_connections",
                columns: new[] { "user_id", "provider" });

            migrationBuilder.Sql(CopyConnectionsSql);

            // 2. deployment_targets, filled from project_deployment_settings (frontend targets).
            migrationBuilder.CreateTable(
                name: "deployment_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    preview_resource_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    root_directory = table.Column<string>(type: "text", nullable: false, defaultValue: "."),
                    build_config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    production_url = table.Column<string>(type: "text", nullable: true),
                    preview_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_targets_platform_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "platform_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployment_targets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_targets_users_bound_by_user_id",
                        column: x => x.bound_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_targets_bound_by_user_id",
                table: "deployment_targets",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_targets_connection_id",
                table: "deployment_targets",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ux_deployment_targets_project_id_component",
                table: "deployment_targets",
                columns: new[] { "project_id", "component" },
                unique: true);

            migrationBuilder.Sql(CreateUuid7);
            migrationBuilder.Sql(CopyTargetsSql);
            migrationBuilder.Sql("DROP FUNCTION pg_temp.aide_uuid7(timestamptz);");

            // 3. deployment_env_vars: inventory columns (FR-145).
            migrationBuilder.AddColumn<string[]>(
                name: "applies_to",
                table: "deployment_env_vars",
                type: "text[]",
                nullable: false,
                defaultValue: new[] { "frontend" });

            migrationBuilder.AddColumn<string>(
                name: "derived_from",
                table: "deployment_env_vars",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "public",
                table: "deployment_env_vars",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "deployment_env_vars",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "user");

            migrationBuilder.Sql(BackfillEnvVarsSql);

            migrationBuilder.AddCheckConstraint(
                name: "ck_deployment_env_vars_applies_to",
                table: "deployment_env_vars",
                sql: "cardinality(applies_to) > 0 AND applies_to <@ ARRAY['frontend', 'backend']::text[]");

            // 4. deployments: environment (renamed), target, component, kind, rollout.
            migrationBuilder.DropIndex(
                name: "ux_deployments_release_id_target_auto_production",
                table: "deployments");

            migrationBuilder.RenameColumn(
                name: "target",
                table: "deployments",
                newName: "environment");

            migrationBuilder.AddColumn<string>(
                name: "component",
                table: "deployments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "frontend");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "deployments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "deploy");

            migrationBuilder.AddColumn<Guid>(
                name: "rollout_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(BackfillDeploymentsSql);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_target_id",
                table: "deployments",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ux_deployments_release_id_component_auto_production",
                table: "deployments",
                columns: new[] { "release_id", "component" },
                unique: true,
                filter: "environment = 'production' AND trigger = 'auto'");

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_deployment_targets_target_id",
                table: "deployments",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // 5. The v2 tables go (their rows are copied above).
            migrationBuilder.DropTable(
                name: "project_deployment_settings");

            migrationBuilder.DropTable(
                name: "vercel_connections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardDownSql);

            migrationBuilder.CreateTable(
                name: "vercel_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    label = table.Column<string>(type: "text", nullable: false),
                    last4 = table.Column<string>(type: "text", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: true),
                    team_slug = table.Column<string>(type: "text", nullable: true),
                    token_encrypted = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vercel_user_id = table.Column<string>(type: "text", nullable: false),
                    vercel_username = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vercel_connections", x => x.id);
                    table.CheckConstraint("ck_vercel_connections_team_scope", "scope <> 'team' OR team_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_vercel_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vercel_connections_user_id",
                table: "vercel_connections",
                column: "user_id");

            migrationBuilder.Sql(RestoreConnectionsSql);

            migrationBuilder.CreateTable(
                name: "project_deployment_settings",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bound_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    framework = table.Column<string>(type: "text", nullable: false, defaultValue: "nextjs"),
                    production_url = table.Column<string>(type: "text", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "vercel"),
                    root_directory = table.Column<string>(type: "text", nullable: false, defaultValue: "."),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vercel_project_id = table.Column<string>(type: "text", nullable: true),
                    vercel_project_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_deployment_settings", x => x.project_id);
                    table.ForeignKey(
                        name: "fk_project_deployment_settings_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_deployment_settings_users_bound_by_user_id",
                        column: x => x.bound_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_project_deployment_settings_vercel_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "vercel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_deployment_settings_bound_by_user_id",
                table: "project_deployment_settings",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_deployment_settings_connection_id",
                table: "project_deployment_settings",
                column: "connection_id");

            migrationBuilder.Sql(RestoreSettingsSql);

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_deployment_targets_target_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ix_deployments_target_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ux_deployments_release_id_component_auto_production",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "component",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "rollout_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "deployments");

            migrationBuilder.RenameColumn(
                name: "environment",
                table: "deployments",
                newName: "target");

            migrationBuilder.CreateIndex(
                name: "ux_deployments_release_id_target_auto_production",
                table: "deployments",
                columns: new[] { "release_id", "target" },
                unique: true,
                filter: "target = 'production' AND trigger = 'auto'");

            migrationBuilder.DropCheckConstraint(
                name: "ck_deployment_env_vars_applies_to",
                table: "deployment_env_vars");

            migrationBuilder.DropColumn(
                name: "applies_to",
                table: "deployment_env_vars");

            migrationBuilder.DropColumn(
                name: "derived_from",
                table: "deployment_env_vars");

            migrationBuilder.DropColumn(
                name: "public",
                table: "deployment_env_vars");

            migrationBuilder.DropColumn(
                name: "source",
                table: "deployment_env_vars");

            migrationBuilder.DropTable(
                name: "deployment_targets");

            migrationBuilder.DropTable(
                name: "platform_connections");
        }
    }
}
