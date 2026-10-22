using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// FR-78/FR-79, D-V2-5: per-user LLM connections (<c>llm_credentials</c>) bound to projects. v1 per-project keys move to a
    /// connection of the project's earliest Owner and stay bound; the ciphertext is copied unchanged (the same Data
    /// Protection purpose, <c>SecretPurposes.LlmApiKey</c> = <c>Aide.Secrets.v1</c>), and <c>last4</c> is <c>????</c>
    /// until <c>LlmCredentialLast4Backfill</c> decrypts it on start-up.
    /// </summary>
    public partial class LlmCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    secret_encrypted = table.Column<string>(type: "text", nullable: false),
                    last4 = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_credentials", x => x.id);
                    table.CheckConstraint("ck_llm_credentials_provider", "provider IN ('anthropic', 'openai')");
                    table.ForeignKey(
                        name: "fk_llm_credentials_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_credentials_user_id",
                table: "llm_credentials",
                column: "user_id");

            migrationBuilder.AddColumn<Guid>(
                name: "bound_by_user_id",
                table: "project_llm_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "credential_id",
                table: "project_llm_settings",
                type: "uuid",
                nullable: true);

            // Data: v1 project keys → connections of the earliest Owner, bound to the project. Before the column is dropped.
            migrationBuilder.Sql(CreateUuid7);
            migrationBuilder.Sql(CollectV1Keys);
            migrationBuilder.Sql(InsertCredentials);
            migrationBuilder.Sql(BindCredentials);
            migrationBuilder.Sql("DROP TABLE aide_v1_llm_keys;");
            migrationBuilder.Sql("DROP FUNCTION pg_temp.aide_uuid7(timestamptz);");

            migrationBuilder.DropColumn(
                name: "api_key_encrypted",
                table: "project_llm_settings");

            migrationBuilder.CreateIndex(
                name: "ix_project_llm_settings_bound_by_user_id",
                table: "project_llm_settings",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_llm_settings_credential_id",
                table: "project_llm_settings",
                column: "credential_id");

            migrationBuilder.AddForeignKey(
                name: "fk_project_llm_settings_llm_credentials_credential_id",
                table: "project_llm_settings",
                column: "credential_id",
                principalTable: "llm_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_project_llm_settings_users_bound_by_user_id",
                table: "project_llm_settings",
                column: "bound_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // agent_runs.credential_id (added by ReleaseVersions, never written yet) gets its FK; any stray value is cleared
            // first so the constraint can't fail.
            migrationBuilder.Sql(
                """
                UPDATE agent_runs r SET credential_id = NULL
                WHERE r.credential_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM llm_credentials c WHERE c.id = r.credential_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_credential_id",
                table: "agent_runs",
                column: "credential_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_runs_llm_credentials_credential_id",
                table: "agent_runs",
                column: "credential_id",
                principalTable: "llm_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_runs_llm_credentials_credential_id",
                table: "agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_project_llm_settings_llm_credentials_credential_id",
                table: "project_llm_settings");

            migrationBuilder.DropForeignKey(
                name: "fk_project_llm_settings_users_bound_by_user_id",
                table: "project_llm_settings");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_credential_id",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ix_project_llm_settings_bound_by_user_id",
                table: "project_llm_settings");

            migrationBuilder.DropIndex(
                name: "ix_project_llm_settings_credential_id",
                table: "project_llm_settings");

            migrationBuilder.AddColumn<string>(
                name: "api_key_encrypted",
                table: "project_llm_settings",
                type: "text",
                nullable: true);

            // The bound connection's key goes back to the project (same ciphertext and purpose); other connections are lost.
            migrationBuilder.Sql(
                """
                UPDATE project_llm_settings s SET api_key_encrypted = c.secret_encrypted
                FROM llm_credentials c
                WHERE c.id = s.credential_id AND c.provider = s.provider;
                """);

            migrationBuilder.Sql("UPDATE agent_runs SET credential_id = NULL WHERE credential_id IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "bound_by_user_id",
                table: "project_llm_settings");

            migrationBuilder.DropColumn(
                name: "credential_id",
                table: "project_llm_settings");

            migrationBuilder.DropTable(
                name: "llm_credentials");
        }

        /// <summary>Time-ordered UUIDv7 ids for migrated rows (PostgreSQL 17 has no uuidv7()); session-local, dropped after use.</summary>
        private const string CreateUuid7 =
            """
            CREATE FUNCTION pg_temp.aide_uuid7(ts timestamptz) RETURNS uuid LANGUAGE sql VOLATILE AS $f$
                SELECT (lpad(to_hex(floor(extract(epoch FROM ts) * 1000)::bigint), 12, '0')
                    || '7' || substr(md5(random()::text), 1, 3)
                    || substr('89ab', floor(random() * 4)::int + 1, 1)
                    || substr(md5(random()::text), 1, 15))::uuid
            $f$;
            """;

        /// <summary>
        /// Every v1 project key of a provider that takes one, with the project's earliest Owner (by membership time, then
        /// user id). The new credential id is fixed here so the insert and the binding agree. A project without an Owner
        /// (prevented by MembershipRules) has no one to own the key, so its key is not migrated.
        /// </summary>
        private const string CollectV1Keys =
            """
            CREATE TEMP TABLE aide_v1_llm_keys AS
            SELECT pg_temp.aide_uuid7(now()) AS credential_id,
                   s.project_id,
                   owner.user_id,
                   s.provider,
                   'Migrated from ' || p.name AS label,
                   s.api_key_encrypted
            FROM project_llm_settings s
            JOIN projects p ON p.id = s.project_id
            CROSS JOIN LATERAL (
                SELECT m.user_id
                FROM project_members m
                WHERE m.project_id = s.project_id AND m.role = 'owner'
                ORDER BY m.created_at, m.user_id
                LIMIT 1
            ) AS owner
            WHERE s.api_key_encrypted IS NOT NULL
              AND s.api_key_encrypted <> ''
              AND s.provider IN ('anthropic', 'openai');
            """;

        private const string InsertCredentials =
            """
            INSERT INTO llm_credentials (id, user_id, provider, label, secret_encrypted, last4, status, last_validated_at, created_at)
            SELECT k.credential_id, k.user_id, k.provider, k.label, k.api_key_encrypted, '????', 'unchecked', NULL, now()
            FROM aide_v1_llm_keys k;
            """;

        private const string BindCredentials =
            """
            UPDATE project_llm_settings s
            SET credential_id = k.credential_id, bound_by_user_id = k.user_id
            FROM aide_v1_llm_keys k
            WHERE s.project_id = k.project_id;
            """;
    }
}
