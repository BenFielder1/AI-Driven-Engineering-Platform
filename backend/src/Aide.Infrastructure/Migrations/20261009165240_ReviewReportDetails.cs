using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewReportDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "optimization_plan_artifact_id",
                table: "compliance_reports",
                type: "uuid",
                nullable: true);

            // Threshold snapshot (FR-39): add nullable, backfill existing reports from their project's current
            // threshold (the best record available), then make it required.
            migrationBuilder.AddColumn<decimal>(
                name: "threshold",
                table: "compliance_reports",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE compliance_reports AS r
                SET threshold = p.compliance_threshold
                FROM projects AS p
                WHERE p.id = r.project_id AND r.threshold IS NULL;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "threshold",
                table: "compliance_reports",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(4,3)",
                oldPrecision: 4,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_compliance_reports_optimization_plan_artifact_id",
                table: "compliance_reports",
                column: "optimization_plan_artifact_id");

            migrationBuilder.AddForeignKey(
                name: "fk_compliance_reports_artifacts_optimization_plan_artifact_id",
                table: "compliance_reports",
                column: "optimization_plan_artifact_id",
                principalTable: "artifacts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_compliance_reports_artifacts_optimization_plan_artifact_id",
                table: "compliance_reports");

            migrationBuilder.DropIndex(
                name: "ix_compliance_reports_optimization_plan_artifact_id",
                table: "compliance_reports");

            migrationBuilder.DropColumn(
                name: "optimization_plan_artifact_id",
                table: "compliance_reports");

            migrationBuilder.DropColumn(
                name: "threshold",
                table: "compliance_reports");
        }
    }
}
