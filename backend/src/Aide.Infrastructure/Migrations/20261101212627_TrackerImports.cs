using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackerImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracker_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    platform_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    git_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    github_user_id = table.Column<long>(type: "bigint", nullable: true),
                    query = table.Column<string>(type: "jsonb", nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    rendered_bytes = table.Column<int>(type: "integer", nullable: false),
                    drift = table.Column<string>(type: "jsonb", nullable: true),
                    drift_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supersedes_import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracker_imports", x => x.id);
                    table.ForeignKey(
                        name: "fk_tracker_imports_artifacts_source_artifact_id",
                        column: x => x.source_artifact_id,
                        principalTable: "artifacts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tracker_imports_git_connections_git_connection_id",
                        column: x => x.git_connection_id,
                        principalTable: "git_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tracker_imports_platform_connections_platform_connection_id",
                        column: x => x.platform_connection_id,
                        principalTable: "platform_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tracker_imports_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tracker_imports_release_versions_release_version_id",
                        column: x => x.release_version_id,
                        principalTable: "release_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tracker_imports_tracker_imports_supersedes_import_id",
                        column: x => x.supersedes_import_id,
                        principalTable: "tracker_imports",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_imports_git_connection_id",
                table: "tracker_imports",
                column: "git_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_imports_platform_connection_id",
                table: "tracker_imports",
                column: "platform_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_imports_project_id",
                table: "tracker_imports",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_imports_release_version_id_status",
                table: "tracker_imports",
                columns: new[] { "release_version_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_imports_source_artifact_id",
                table: "tracker_imports",
                column: "source_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ux_tracker_imports_supersedes_import_id",
                table: "tracker_imports",
                column: "supersedes_import_id",
                unique: true,
                filter: "supersedes_import_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracker_imports");
        }
    }
}
