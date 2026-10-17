using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VercelDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deployment_env_vars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    targets = table.Column<string[]>(type: "text[]", nullable: false),
                    value_encrypted = table.Column<string>(type: "text", nullable: false),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    vercel_env_ids = table.Column<string>(type: "jsonb", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sync_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_env_vars", x => x.id);
                    table.CheckConstraint("ck_deployment_env_vars_targets", "cardinality(targets) > 0 AND targets <@ ARRAY['preview', 'production']::text[]");
                    table.ForeignKey(
                        name: "fk_deployment_env_vars_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_deployment_id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    inspector_url = table.Column<string>(type: "text", nullable: true),
                    git_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    git_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    build_log_tail = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    triggered_by = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    promoted_from_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployments_deployments_promoted_from_deployment_id",
                        column: x => x.promoted_from_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployments_release_versions_release_version_id",
                        column: x => x.release_version_id,
                        principalTable: "release_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_deployments_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_users_triggered_by",
                        column: x => x.triggered_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "vercel_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    token_encrypted = table.Column<string>(type: "text", nullable: false),
                    last4 = table.Column<string>(type: "text", nullable: false),
                    vercel_user_id = table.Column<string>(type: "text", nullable: false),
                    vercel_username = table.Column<string>(type: "text", nullable: true),
                    team_id = table.Column<string>(type: "text", nullable: true),
                    team_slug = table.Column<string>(type: "text", nullable: true),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "project_deployment_settings",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "vercel"),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vercel_project_id = table.Column<string>(type: "text", nullable: true),
                    vercel_project_name = table.Column<string>(type: "text", nullable: true),
                    root_directory = table.Column<string>(type: "text", nullable: false, defaultValue: "."),
                    framework = table.Column<string>(type: "text", nullable: false, defaultValue: "nextjs"),
                    production_url = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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
                name: "ix_deployment_env_vars_project_id_key",
                table: "deployment_env_vars",
                columns: new[] { "project_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_project_id_created_at",
                table: "deployments",
                columns: new[] { "project_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_project_id_git_sha",
                table: "deployments",
                columns: new[] { "project_id", "git_sha" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_promoted_from_deployment_id",
                table: "deployments",
                column: "promoted_from_deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_id",
                table: "deployments",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_version_id",
                table: "deployments",
                column: "release_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_status_active",
                table: "deployments",
                column: "status",
                filter: "status IN ('queued', 'building')");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_triggered_by",
                table: "deployments",
                column: "triggered_by");

            migrationBuilder.CreateIndex(
                name: "ux_deployments_release_id_target_auto_production",
                table: "deployments",
                columns: new[] { "release_id", "target" },
                unique: true,
                filter: "target = 'production' AND trigger = 'auto'");

            migrationBuilder.CreateIndex(
                name: "ix_project_deployment_settings_bound_by_user_id",
                table: "project_deployment_settings",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_deployment_settings_connection_id",
                table: "project_deployment_settings",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_vercel_connections_user_id",
                table: "vercel_connections",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_env_vars");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "project_deployment_settings");

            migrationBuilder.DropTable(
                name: "vercel_connections");
        }
    }
}
