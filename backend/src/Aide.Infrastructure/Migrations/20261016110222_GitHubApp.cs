using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// FR-80–FR-83: the AIDE GitHub App (<c>github_apps</c>, at most one row), per-user GitHub connections
    /// (<c>github_connections</c>) and the <c>github_app</c> Git provider (installation id, repo id and full name on
    /// <c>project_git_settings</c>). v1's provider value <c>github</c> (a per-project PAT) is renamed to <c>github_pat</c>.
    /// v1 GitHub sign-in links stay in <c>users.github_id</c>; no connection rows are created (there are no tokens).
    /// </summary>
    public partial class GitHubApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "installation_id",
                table: "project_git_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repo_full_name",
                table: "project_git_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "repo_id",
                table: "project_git_settings",
                type: "bigint",
                nullable: true);

            // Data: v1's PAT-backed provider value "github" is now "github_pat" (FR-82, FR-83).
            migrationBuilder.Sql(RenameGithubToGithubPat);

            migrationBuilder.CreateTable(
                name: "github_apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    owner_login = table.Column<string>(type: "text", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    client_secret_encrypted = table.Column<string>(type: "text", nullable: false),
                    private_key_encrypted = table.Column<string>(type: "text", nullable: false),
                    permissions = table.Column<string>(type: "jsonb", nullable: false),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    singleton = table.Column<bool>(type: "boolean", nullable: false, computedColumnSql: "true", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_apps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "github_connections",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_user_id = table.Column<long>(type: "bigint", nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    access_token_encrypted = table.Column<string>(type: "text", nullable: false),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_token_encrypted = table.Column<string>(type: "text", nullable: true),
                    refresh_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_connections", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_github_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_git_settings_installation_id",
                table: "project_git_settings",
                column: "installation_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_git_settings_github_app_repo",
                table: "project_git_settings",
                sql: "provider <> 'github_app' OR (installation_id IS NOT NULL AND repo_id IS NOT NULL AND repo_full_name IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_github_apps_singleton",
                table: "github_apps",
                column: "singleton",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_connections_github_user_id",
                table: "github_connections",
                column: "github_user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy: App-backed projects have no v1 equivalent and fall back to local (their repo ids are dropped below).
            migrationBuilder.Sql(RestoreV1Providers);

            migrationBuilder.DropTable(
                name: "github_apps");

            migrationBuilder.DropTable(
                name: "github_connections");

            migrationBuilder.DropIndex(
                name: "ix_project_git_settings_installation_id",
                table: "project_git_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_git_settings_github_app_repo",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "repo_full_name",
                table: "project_git_settings");

            migrationBuilder.DropColumn(
                name: "repo_id",
                table: "project_git_settings");
        }

        private const string RenameGithubToGithubPat =
            """
            UPDATE project_git_settings SET provider = 'github_pat' WHERE provider = 'github';
            """;

        private const string RestoreV1Providers =
            """
            UPDATE project_git_settings SET provider = 'github' WHERE provider = 'github_pat';
            UPDATE project_git_settings SET provider = 'local', repo_url = NULL, token_encrypted = NULL WHERE provider = 'github_app';
            """;
    }
}
