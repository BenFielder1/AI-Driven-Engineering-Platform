using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BuildChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_build_fix_attempts",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<Guid>(
                name: "build_check_id",
                table: "execution_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "execution_steps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "plan");

            migrationBuilder.CreateTable(
                name: "build_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_cycle = table.Column<int>(type: "integer", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sources = table.Column<string>(type: "jsonb", nullable: false),
                    failures = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_checks", x => x.id);
                    table.ForeignKey(
                        name: "fk_build_checks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_build_checks_release_versions_release_version_id",
                        column: x => x.release_version_id,
                        principalTable: "release_versions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_execution_steps_build_check_id",
                table: "execution_steps",
                column: "build_check_id");

            migrationBuilder.CreateIndex(
                name: "ix_build_checks_project_id_release_version_id",
                table: "build_checks",
                columns: new[] { "project_id", "release_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_build_checks_project_id_status",
                table: "build_checks",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_build_checks_release_version_id_review_cycle_attempt",
                table: "build_checks",
                columns: new[] { "release_version_id", "review_cycle", "attempt" },
                unique: true,
                filter: "superseded_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_execution_steps_build_checks_build_check_id",
                table: "execution_steps",
                column: "build_check_id",
                principalTable: "build_checks",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_execution_steps_build_checks_build_check_id",
                table: "execution_steps");

            migrationBuilder.DropTable(
                name: "build_checks");

            migrationBuilder.DropIndex(
                name: "ix_execution_steps_build_check_id",
                table: "execution_steps");

            migrationBuilder.DropColumn(
                name: "max_build_fix_attempts",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "build_check_id",
                table: "execution_steps");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "execution_steps");
        }
    }
}
