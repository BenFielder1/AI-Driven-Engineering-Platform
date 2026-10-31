using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ManagedDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "migration_run_id",
                table: "releases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_notes",
                table: "releases",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_databases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    plan = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_encrypted = table.Column<string>(type: "text", nullable: false),
                    db_password_encrypted = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_databases", x => x.id);
                    table.ForeignKey(
                        name: "fk_managed_databases_platform_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "platform_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_managed_databases_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "database_migration_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    managed_database_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    git_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    applied = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_database_migration_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_database_migration_runs_managed_databases_managed_database_",
                        column: x => x.managed_database_id,
                        principalTable: "managed_databases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_database_migration_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_database_migration_runs_release_versions_release_version_id",
                        column: x => x.release_version_id,
                        principalTable: "release_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_database_migration_runs_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_releases_migration_run_id",
                table: "releases",
                column: "migration_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_migration_runs_managed_database_id",
                table: "database_migration_runs",
                column: "managed_database_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_migration_runs_project_id_started_at",
                table: "database_migration_runs",
                columns: new[] { "project_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_database_migration_runs_release_id",
                table: "database_migration_runs",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_database_migration_runs_release_version_id",
                table: "database_migration_runs",
                column: "release_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_databases_connection_id",
                table: "managed_databases",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ux_managed_databases_project_id_environment",
                table: "managed_databases",
                columns: new[] { "project_id", "environment" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_database_migration_runs_migration_run_id",
                table: "releases",
                column: "migration_run_id",
                principalTable: "database_migration_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_releases_database_migration_runs_migration_run_id",
                table: "releases");

            migrationBuilder.DropTable(
                name: "database_migration_runs");

            migrationBuilder.DropTable(
                name: "managed_databases");

            migrationBuilder.DropIndex(
                name: "ix_releases_migration_run_id",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "migration_run_id",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "release_notes",
                table: "releases");
        }
    }
}
