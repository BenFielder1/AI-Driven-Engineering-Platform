using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "input",
                table: "agent_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "input_truncated",
                table: "agent_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "latency_ms",
                table: "agent_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stage_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    iteration = table.Column<int>(type: "integer", nullable: false),
                    checkpoint = table.Column<int>(type: "integer", nullable: false),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_stage_executions_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_pipeline_run_id_entered_at",
                table: "stage_executions",
                columns: new[] { "pipeline_run_id", "entered_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stage_executions");

            migrationBuilder.DropColumn(
                name: "input",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "input_truncated",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "latency_ms",
                table: "agent_runs");
        }
    }
}
