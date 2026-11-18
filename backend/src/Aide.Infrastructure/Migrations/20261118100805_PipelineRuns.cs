using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PipelineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "state_transitions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "state_transitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "approval_gates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "agent_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pipeline_definitions",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    pipeline_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    pack_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    canonical_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_definitions", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    definition_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_stage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    pack_status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    checkpoint = table.Column<int>(type: "integer", nullable: false),
                    approval_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    iterations = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_runs_pipeline_definitions_definition_hash",
                        column: x => x.definition_hash,
                        principalTable: "pipeline_definitions",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pipeline_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_state_transitions_pipeline_run_id",
                table: "state_transitions",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_pipeline_run_id",
                table: "artifacts",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_gates_pipeline_run_id",
                table: "approval_gates",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_pipeline_run_id",
                table: "agent_runs",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_runs_definition_hash",
                table: "pipeline_runs",
                column: "definition_hash");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_runs_project_id",
                table: "pipeline_runs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_runs_pipeline_id_subject_id",
                table: "pipeline_runs",
                columns: new[] { "pipeline_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_runs_project_id_pipeline_id_open",
                table: "pipeline_runs",
                columns: new[] { "project_id", "pipeline_id" },
                unique: true,
                filter: "status NOT IN ('completed', 'failed', 'cancelled')");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_runs_pipeline_runs_pipeline_run_id",
                table: "agent_runs",
                column: "pipeline_run_id",
                principalTable: "pipeline_runs",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_approval_gates_pipeline_runs_pipeline_run_id",
                table: "approval_gates",
                column: "pipeline_run_id",
                principalTable: "pipeline_runs",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_artifacts_pipeline_runs_pipeline_run_id",
                table: "artifacts",
                column: "pipeline_run_id",
                principalTable: "pipeline_runs",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_state_transitions_pipeline_runs_pipeline_run_id",
                table: "state_transitions",
                column: "pipeline_run_id",
                principalTable: "pipeline_runs",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_runs_pipeline_runs_pipeline_run_id",
                table: "agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_approval_gates_pipeline_runs_pipeline_run_id",
                table: "approval_gates");

            migrationBuilder.DropForeignKey(
                name: "fk_artifacts_pipeline_runs_pipeline_run_id",
                table: "artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_state_transitions_pipeline_runs_pipeline_run_id",
                table: "state_transitions");

            migrationBuilder.DropTable(
                name: "pipeline_runs");

            migrationBuilder.DropTable(
                name: "pipeline_definitions");

            migrationBuilder.DropIndex(
                name: "ix_state_transitions_pipeline_run_id",
                table: "state_transitions");

            migrationBuilder.DropIndex(
                name: "ix_artifacts_pipeline_run_id",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ix_approval_gates_pipeline_run_id",
                table: "approval_gates");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_pipeline_run_id",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "state_transitions");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "state_transitions");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "approval_gates");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "agent_runs");
        }
    }
}
