using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M70ScaleAndConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_execution_id",
                table: "stage_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "item_index",
                table: "artifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "item_index",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "turn_index",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "turn_role",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_stage_executions_parent_execution_id_item_index",
                table: "stage_executions",
                columns: new[] { "parent_execution_id", "item_index" },
                unique: true,
                filter: "parent_execution_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version", "variant", "item_index" },
                unique: true,
                filter: "release_version_id IS NULL AND scope = 'run'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_stage_executions_stage_executions_parent_execution_id",
                table: "stage_executions",
                column: "parent_execution_id",
                principalTable: "stage_executions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_stage_executions_stage_executions_parent_execution_id",
                table: "stage_executions");

            migrationBuilder.DropIndex(
                name: "ux_stage_executions_parent_execution_id_item_index",
                table: "stage_executions");

            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "parent_execution_id",
                table: "stage_executions");

            migrationBuilder.DropColumn(
                name: "item_index",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "item_index",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "turn_index",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "turn_role",
                table: "agent_runs");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version", "variant" },
                unique: true,
                filter: "release_version_id IS NULL AND scope = 'run'")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
