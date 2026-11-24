using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M66RunControlAndTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_pipeline_runs_pipeline_id_subject_id",
                table: "pipeline_runs");

            migrationBuilder.DropIndex(
                name: "ux_pipeline_runs_project_id_pipeline_id_open",
                table: "pipeline_runs");

            migrationBuilder.DropIndex(
                name: "ix_artifacts_project_id_type_version",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ux_approval_gates_project_id_type_pending",
                table: "approval_gates");

            migrationBuilder.AlterColumn<Guid>(
                name: "release_version_id",
                table: "artifacts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "release_version_id",
                table: "approval_gates",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "pipeline_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    inputs = table.Column<string>(type: "jsonb", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    next_fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_schedules_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pipeline_schedules_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    secret = table.Column<string>(type: "text", nullable: false),
                    previous_secret = table.Column<string>(type: "text", nullable: true),
                    previous_secret_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_webhooks", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_webhooks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pipeline_webhooks_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => new { x.webhook_id, x.delivery_id });
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_pipeline_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "pipeline_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_pipeline_webhooks_webhook_id",
                        column: x => x.webhook_id,
                        principalTable: "pipeline_webhooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_runs_pipeline_id_subject_id",
                table: "pipeline_runs",
                columns: new[] { "pipeline_id", "subject_id" },
                unique: true,
                filter: "subject_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_runs_project_id_pipeline_id_open",
                table: "pipeline_runs",
                columns: new[] { "project_id", "pipeline_id" },
                unique: true,
                filter: "status NOT IN ('completed', 'failed', 'cancelled') AND subject_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_project_id_type_version",
                table: "artifacts",
                columns: new[] { "project_id", "type", "version" },
                unique: true,
                filter: "release_version_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts",
                columns: new[] { "pipeline_run_id", "type", "version" },
                unique: true,
                filter: "release_version_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_approval_gates_pipeline_run_id_type_pending",
                table: "approval_gates",
                columns: new[] { "pipeline_run_id", "type" },
                unique: true,
                filter: "status = 'pending' AND release_version_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_approval_gates_project_id_type_pending",
                table: "approval_gates",
                columns: new[] { "project_id", "type" },
                unique: true,
                filter: "status = 'pending' AND release_version_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_schedules_created_by",
                table: "pipeline_schedules",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_schedules_next_fire_at",
                table: "pipeline_schedules",
                column: "next_fire_at",
                filter: "NOT paused");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_schedules_project_id",
                table: "pipeline_schedules",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_webhooks_created_by",
                table: "pipeline_webhooks",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_webhooks_project_id",
                table: "pipeline_webhooks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_received_at",
                table: "webhook_deliveries",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_run_id",
                table: "webhook_deliveries",
                column: "run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pipeline_schedules");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "pipeline_webhooks");

            migrationBuilder.DropIndex(
                name: "ux_pipeline_runs_pipeline_id_subject_id",
                table: "pipeline_runs");

            migrationBuilder.DropIndex(
                name: "ux_pipeline_runs_project_id_pipeline_id_open",
                table: "pipeline_runs");

            migrationBuilder.DropIndex(
                name: "ix_artifacts_project_id_type_version",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ux_artifacts_pipeline_run_id_type_version",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "ux_approval_gates_pipeline_run_id_type_pending",
                table: "approval_gates");

            migrationBuilder.DropIndex(
                name: "ux_approval_gates_project_id_type_pending",
                table: "approval_gates");

            migrationBuilder.AlterColumn<Guid>(
                name: "release_version_id",
                table: "artifacts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "release_version_id",
                table: "approval_gates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_project_id_type_version",
                table: "artifacts",
                columns: new[] { "project_id", "type", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_approval_gates_project_id_type_pending",
                table: "approval_gates",
                columns: new[] { "project_id", "type" },
                unique: true,
                filter: "status = 'pending'");
        }
    }
}
