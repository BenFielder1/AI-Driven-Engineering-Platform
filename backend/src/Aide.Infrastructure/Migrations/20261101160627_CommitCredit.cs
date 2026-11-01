using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommitCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "commit_credit_opt_out",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "commit_credit_enabled",
                table: "project_git_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_email_encrypted",
                table: "git_connections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "commit_credit_opt_out",
                table: "users");

            migrationBuilder.DropColumn(
                name: "commit_credit_enabled",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "commit_email_encrypted",
                table: "git_connections");
        }
    }
}
