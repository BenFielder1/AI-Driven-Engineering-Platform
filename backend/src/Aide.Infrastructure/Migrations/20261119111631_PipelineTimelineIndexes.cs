using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PipelineTimelineIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_state_transitions_pipeline_run_id_created_at",
                table: "state_transitions",
                columns: new[] { "pipeline_run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_pipeline_run_id_created_at",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_gates_pipeline_run_id_created_at",
                table: "approval_gates",
                columns: new[] { "pipeline_run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_pipeline_run_id_created_at",
                table: "agent_runs",
                columns: new[] { "pipeline_run_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_state_transitions_pipeline_run_id_created_at",
                table: "state_transitions");

            migrationBuilder.DropIndex(
                name: "ix_artifacts_pipeline_run_id_created_at",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ix_approval_gates_pipeline_run_id_created_at",
                table: "approval_gates");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_pipeline_run_id_created_at",
                table: "agent_runs");
        }
    }
}
