using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tevrin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class M71ToolsAndSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tool_loop_state",
                table: "agent_runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tool_loop_state",
                table: "agent_runs");
        }
    }
}
