using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "notification_key",
                table: "notifications",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "notification_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    muted_types = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_notification_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_created_at_id",
                table: "notifications",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_user_id_notification_key",
                table: "notifications",
                columns: new[] { "user_id", "notification_key" },
                unique: true,
                filter: "notification_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_status_next_attempt_at",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_notification_preferences");

            migrationBuilder.DropIndex(
                name: "ix_notifications_user_id_created_at_id",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ux_notifications_user_id_notification_key",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_deliveries_status_next_attempt_at",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "notification_key",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "notification_deliveries");
        }
    }
}
