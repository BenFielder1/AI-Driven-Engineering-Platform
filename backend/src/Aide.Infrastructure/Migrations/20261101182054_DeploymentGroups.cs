using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "managed_databases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "deployment_targets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform_project_id",
                table: "deployment_targets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deployment_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shared_resource_id = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    plan = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_groups_platform_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "platform_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployment_groups_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_groups_users_bound_by_user_id",
                        column: x => x.bound_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_managed_databases_group_id",
                table: "managed_databases",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_targets_group_id",
                table: "deployment_targets",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_groups_bound_by_user_id",
                table: "deployment_groups",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_groups_connection_id",
                table: "deployment_groups",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ux_deployment_groups_project_id_provider",
                table: "deployment_groups",
                columns: new[] { "project_id", "provider" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_targets_deployment_groups_group_id",
                table: "deployment_targets",
                column: "group_id",
                principalTable: "deployment_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_managed_databases_deployment_groups_group_id",
                table: "managed_databases",
                column: "group_id",
                principalTable: "deployment_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployment_targets_deployment_groups_group_id",
                table: "deployment_targets");

            migrationBuilder.DropForeignKey(
                name: "fk_managed_databases_deployment_groups_group_id",
                table: "managed_databases");

            migrationBuilder.DropTable(
                name: "deployment_groups");

            migrationBuilder.DropIndex(
                name: "ix_managed_databases_group_id",
                table: "managed_databases");

            migrationBuilder.DropIndex(
                name: "ix_deployment_targets_group_id",
                table: "deployment_targets");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "managed_databases");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "deployment_targets");

            migrationBuilder.DropColumn(
                name: "platform_project_id",
                table: "deployment_targets");
        }
    }
}
