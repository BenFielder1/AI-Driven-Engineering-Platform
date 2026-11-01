using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StackCompositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "catalog_hash",
                table: "release_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "catalog_version",
                table: "release_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stack_composition",
                table: "release_versions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "stack_packs",
                table: "projects",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateIndex(
                name: "ix_projects_stack_packs",
                table: "projects",
                column: "stack_packs")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_projects_stack_packs",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "catalog_hash",
                table: "release_versions");

            migrationBuilder.DropColumn(
                name: "catalog_version",
                table: "release_versions");

            migrationBuilder.DropColumn(
                name: "stack_composition",
                table: "release_versions");

            migrationBuilder.DropColumn(
                name: "stack_packs",
                table: "projects");
        }
    }
}
