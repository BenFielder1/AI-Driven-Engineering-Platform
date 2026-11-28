using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M67SourceOfTruthArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.AddColumn<string>(
                name: "pinned_artifacts",
                table: "pipeline_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "artifacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "run");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version" },
                unique: true,
                filter: "release_version_id IS NULL AND scope = 'run'");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_project_id_type_version_project_scope",
                table: "artifacts",
                columns: new[] { "project_id", "type", "version" },
                unique: true,
                filter: "scope = 'project'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ux_artifacts_project_id_type_version_project_scope",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "pinned_artifacts",
                table: "pipeline_runs");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "artifacts");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version" },
                unique: true,
                filter: "release_version_id IS NULL");
        }
    }
}
