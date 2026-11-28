using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M69QualityAndLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "feedback_ids",
                table: "agent_runs",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validator_findings",
                table: "agent_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    agent_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_feedback", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_feedback_agent_runs_agent_run_id",
                        column: x => x.agent_run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_agent_feedback_pipeline_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "pipeline_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_agent_feedback_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_feedback_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "outcome_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metric = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    prediction = table.Column<double>(type: "double precision", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outcome_scores", x => x.id);
                    table.CheckConstraint("ck_outcome_scores_one_target", "(agent_run_id IS NULL) <> (artifact_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_outcome_scores_agent_runs_agent_run_id",
                        column: x => x.agent_run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_outcome_scores_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalTable: "artifacts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_outcome_scores_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outcome_scores_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_feedback_agent_run_id",
                table: "agent_feedback",
                column: "agent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_feedback_created_by",
                table: "agent_feedback",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_agent_feedback_project_id_pipeline_id_agent_key_created_at",
                table: "agent_feedback",
                columns: new[] { "project_id", "pipeline_id", "agent_key", "created_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_agent_feedback_run_id",
                table: "agent_feedback",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_outcome_scores_agent_run_id_metric",
                table: "outcome_scores",
                columns: new[] { "agent_run_id", "metric" });

            migrationBuilder.CreateIndex(
                name: "ix_outcome_scores_artifact_id",
                table: "outcome_scores",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_outcome_scores_created_by",
                table: "outcome_scores",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_outcome_scores_pipeline_run_id",
                table: "outcome_scores",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_outcome_scores_pipeline_run_id_observed_at",
                table: "outcome_scores",
                columns: new[] { "pipeline_run_id", "observed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_feedback");

            migrationBuilder.DropTable(
                name: "outcome_scores");

            migrationBuilder.DropColumn(
                name: "feedback_ids",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "validator_findings",
                table: "agent_runs");
        }
    }
}
