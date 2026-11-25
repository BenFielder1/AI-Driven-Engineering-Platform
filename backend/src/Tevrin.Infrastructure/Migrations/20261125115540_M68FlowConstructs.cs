using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M68FlowConstructs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "stage_executions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "item_index",
                table: "stage_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "variant",
                table: "stage_executions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "veto_findings",
                table: "stage_executions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "pipeline_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revisions",
                table: "pipeline_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "variant",
                table: "artifacts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload",
                table: "approval_gates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_pipeline_run_id_stage_id_branch_item_index",
                table: "stage_executions",
                columns: new[] { "pipeline_run_id", "stage_id", "branch", "item_index" });

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version", "variant" },
                unique: true,
                filter: "release_version_id IS NULL AND scope = 'run'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stage_executions_pipeline_run_id_stage_id_branch_item_index",
                table: "stage_executions");

            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "stage_executions");

            migrationBuilder.DropColumn(
                name: "item_index",
                table: "stage_executions");

            migrationBuilder.DropColumn(
                name: "variant",
                table: "stage_executions");

            migrationBuilder.DropColumn(
                name: "veto_findings",
                table: "stage_executions");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "pipeline_runs");

            migrationBuilder.DropColumn(
                name: "revisions",
                table: "pipeline_runs");

            migrationBuilder.DropColumn(
                name: "variant",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "payload",
                table: "approval_gates");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version" },
                unique: true,
                filter: "release_version_id IS NULL AND scope = 'run'");
        }
    }
}
