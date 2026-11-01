using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SpecialistRunRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_agent_key",
                table: "execution_steps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "catalog_version",
                table: "agent_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "packs",
                table: "agent_runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "legacy_agent_key",
                table: "execution_steps");

            migrationBuilder.DropColumn(
                name: "catalog_version",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "packs",
                table: "agent_runs");
        }
    }
}
