using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// FR-130, FR-131, FR-133, FR-134, FR-136 (§5): Git host instances (GitLab, Bitbucket Cloud), users' external logins and
    /// GitLab/Bitbucket connections, and the GitLab/Bitbucket binding columns on <c>project_git_settings</c>. Additive: new
    /// tables, nullable columns and a check constraint that every existing row passes (no gitlab/bitbucket rows yet).
    /// Backfills one <c>github</c> external login per user with a <c>users.github_id</c> (which stays, for v2 code paths).
    /// </summary>
    public partial class GitHosts : Migration
    {
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
        /// One <c>github</c> login per user with a GitHub id (unique on <c>users</c>, so no conflicts). The login is the
        /// GitHub connection's login when there is one, else the user name, else the id.
        /// </summary>
        public const string BackfillGitHubLoginsSql =
            """
            INSERT INTO user_external_logins (id, user_id, provider, instance_id, external_user_id, login, created_at)
            SELECT pg_temp.aide_uuid7(u.created_at), u.id, 'github', NULL, u.github_id,
                   COALESCE(NULLIF(c.login, ''), NULLIF(u.user_name, ''), u.github_id), u.created_at
            FROM users AS u
            LEFT JOIN github_connections AS c ON c.user_id = u.id
            WHERE u.github_id IS NOT NULL AND u.github_id <> '';
            """;

        /// <summary>Down would leave provider values the previous code can't read, so it refuses while any exist.</summary>
        private const string GuardDownSql =
            """
            DO $g$
            BEGIN
                IF EXISTS (SELECT 1 FROM project_git_settings WHERE provider IN ('gitlab', 'bitbucket')) THEN
                    RAISE EXCEPTION 'GitHosts Down: project_git_settings has gitlab/bitbucket projects; rebind them first';
                END IF;
            END
            $g$;
            """;
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "bound_by_user_id",
                table: "project_git_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_repo_id",
                table: "project_git_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "git_connection_id",
                table: "project_git_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "instance_id",
                table: "project_git_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "git_host_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    oauth_client_id = table.Column<string>(type: "text", nullable: false),
                    oauth_client_secret_encrypted = table.Column<string>(type: "text", nullable: false),
                    allow_insecure_http = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_host_instances", x => x.id);
                    table.CheckConstraint("ck_git_host_instances_bitbucket_base_url", "kind <> 'bitbucket' OR base_url IS NULL");
                    table.ForeignKey(
                        name: "fk_git_host_instances_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "git_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auth_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    access_token_encrypted = table.Column<string>(type: "text", nullable: false),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_token_encrypted = table.Column<string>(type: "text", nullable: true),
                    token_email_encrypted = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_connections", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_connections_git_host_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "git_host_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_git_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_external_logins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_external_logins", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_external_logins_git_host_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "git_host_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_user_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_git_settings_bound_by_user_id",
                table: "project_git_settings",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_git_settings_git_connection_id",
                table: "project_git_settings",
                column: "git_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_git_settings_instance_id",
                table: "project_git_settings",
                column: "instance_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_git_settings_git_host_repo",
                table: "project_git_settings",
                sql: "provider NOT IN ('gitlab', 'bitbucket') OR (instance_id IS NOT NULL AND external_repo_id IS NOT NULL AND repo_full_name IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_git_connections_instance_id",
                table: "git_connections",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ux_git_connections_user_provider_instance_external_user",
                table: "git_connections",
                columns: new[] { "user_id", "provider", "instance_id", "external_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_git_host_instances_created_by_user_id",
                table: "git_host_instances",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_git_host_instances_kind_base_url",
                table: "git_host_instances",
                columns: new[] { "kind", "base_url" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_user_external_logins_instance_id",
                table: "user_external_logins",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_external_logins_user_id",
                table: "user_external_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_external_logins_provider_instance_external_user",
                table: "user_external_logins",
                columns: new[] { "provider", "instance_id", "external_user_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_project_git_settings_git_connections_git_connection_id",
                table: "project_git_settings",
                column: "git_connection_id",
                principalTable: "git_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_project_git_settings_git_host_instances_instance_id",
                table: "project_git_settings",
                column: "instance_id",
                principalTable: "git_host_instances",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_project_git_settings_users_bound_by_user_id",
                table: "project_git_settings",
                column: "bound_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(CreateUuid7);
            migrationBuilder.Sql(BackfillGitHubLoginsSql);
            migrationBuilder.Sql("DROP FUNCTION pg_temp.aide_uuid7(timestamptz);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardDownSql);

            migrationBuilder.DropForeignKey(
                name: "fk_project_git_settings_git_connections_git_connection_id",
                table: "project_git_settings");

            migrationBuilder.DropForeignKey(
                name: "fk_project_git_settings_git_host_instances_instance_id",
                table: "project_git_settings");

            migrationBuilder.DropForeignKey(
                name: "fk_project_git_settings_users_bound_by_user_id",
                table: "project_git_settings");

            migrationBuilder.DropTable(
                name: "git_connections");

            migrationBuilder.DropTable(
                name: "user_external_logins");

            migrationBuilder.DropTable(
                name: "git_host_instances");

            migrationBuilder.DropIndex(
                name: "ix_project_git_settings_bound_by_user_id",
                table: "project_git_settings");

            migrationBuilder.DropIndex(
                name: "ix_project_git_settings_git_connection_id",
                table: "project_git_settings");

            migrationBuilder.DropIndex(
                name: "ix_project_git_settings_instance_id",
                table: "project_git_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_git_settings_git_host_repo",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "bound_by_user_id",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "external_repo_id",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "git_connection_id",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "instance_id",
                table: "project_git_settings");
        }
    }
}
