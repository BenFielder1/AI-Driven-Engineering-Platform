using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAndRollbackDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Several attempts of one version may succeed (a version number is reused after a rollback; each release has its
            // own tag or reuses the one holding it): uniqueness moves from "one succeeded row per version" to "one row per attempt".
            migrationBuilder.DropIndex(
                name: "ux_releases_project_id_project_version_succeeded",
                table: "releases");

            migrationBuilder.RenameColumn(
                name: "error",
                table: "releases",
                newName: "error_message");

            migrationBuilder.AddColumn<Guid[]>(
                name: "cancelled_gate_ids",
                table: "rollback_events",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<Guid[]>(
                name: "cancelled_job_ids",
                table: "rollback_events",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<Guid[]>(
                name: "requeued_step_ids",
                table: "rollback_events",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<int>(
                name: "attempt",
                table: "releases",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "releases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "releases",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Existing releases are all terminal (succeeded/failed; in_progress is new), so they completed when recorded.
            // They are numbered per version in creation order, so the new unique index holds.
            migrationBuilder.Sql(
                """
                UPDATE releases SET completed_at = created_at WHERE completed_at IS NULL;

                UPDATE releases AS r
                SET attempt = n.attempt
                FROM (
                    SELECT id, row_number() OVER (PARTITION BY project_id, project_version ORDER BY created_at, id) AS attempt
                    FROM releases) AS n
                WHERE r.id = n.id AND r.attempt <> n.attempt;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_releases_project_id_project_version_attempt",
                table: "releases",
                columns: new[] { "project_id", "project_version", "attempt" },
                unique: true);

            // Restored phase. Deliberately left NULLABLE in the database (the model says required, and this code always sets
            // it): API pods of the previous version keep inserting rollback rows without it during a rolling update. Readers
            // fall back to the same sources as this backfill. A later (contract) migration re-runs the backfill and sets NOT NULL.
            // Sources, best first: the ROLLBACK_COMPLETED event's "phase" (matched on rollbackId), the rollback's state
            // transition (written with the same timestamp), and only as a last resort the project's current phase.
            migrationBuilder.AddColumn<string>(
                name: "restored_phase",
                table: "rollback_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE rollback_events AS r
                SET restored_phase = e.payload->>'phase'
                FROM events AS e
                WHERE e.type = 'ROLLBACK_COMPLETED'
                  AND e.project_id = r.project_id
                  AND e.payload->>'rollbackId' = r.id::text
                  AND jsonb_typeof(e.payload->'phase') = 'string'
                  AND r.restored_phase IS NULL;

                UPDATE rollback_events AS r
                SET restored_phase = (
                    SELECT t.to_phase FROM state_transitions AS t
                    WHERE t.project_id = r.project_id AND t.created_at = r.created_at
                    ORDER BY t.id DESC LIMIT 1)
                WHERE r.restored_phase IS NULL;

                UPDATE rollback_events AS r
                SET restored_phase = p.current_phase
                FROM projects AS p
                WHERE p.id = r.project_id AND r.restored_phase IS NULL;
                """);

            // Id lists from the ROLLBACK_COMPLETED payload. reset_step_ids used to hold reset + requeued steps together,
            // so requeued ids are removed from it (order kept).
            migrationBuilder.Sql(
                """
                UPDATE rollback_events AS r
                SET requeued_step_ids = CASE WHEN jsonb_typeof(e.payload->'requeuedStepIds') = 'array'
                        THEN ARRAY(SELECT x::uuid FROM jsonb_array_elements_text(e.payload->'requeuedStepIds') AS x)
                        ELSE r.requeued_step_ids END,
                    cancelled_gate_ids = CASE WHEN jsonb_typeof(e.payload->'cancelledGateIds') = 'array'
                        THEN ARRAY(SELECT x::uuid FROM jsonb_array_elements_text(e.payload->'cancelledGateIds') AS x)
                        ELSE r.cancelled_gate_ids END,
                    cancelled_job_ids = CASE WHEN jsonb_typeof(e.payload->'cancelledJobIds') = 'array'
                        THEN ARRAY(SELECT x::uuid FROM jsonb_array_elements_text(e.payload->'cancelledJobIds') AS x)
                        ELSE r.cancelled_job_ids END
                FROM events AS e
                WHERE e.type = 'ROLLBACK_COMPLETED'
                  AND e.project_id = r.project_id
                  AND e.payload->>'rollbackId' = r.id::text;

                UPDATE rollback_events AS r
                SET reset_step_ids = ARRAY(
                    SELECT u.x FROM unnest(r.reset_step_ids) WITH ORDINALITY AS u(x, n)
                    WHERE u.x <> ALL (r.requeued_step_ids)
                    ORDER BY u.n)
                WHERE cardinality(r.requeued_step_ids) > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the old meaning of reset_step_ids (reset + requeued together) before the split column goes.
            migrationBuilder.Sql(
                """
                UPDATE rollback_events
                SET reset_step_ids = reset_step_ids || requeued_step_ids
                WHERE cardinality(requeued_step_ids) > 0;
                """);

            migrationBuilder.DropIndex(
                name: "ux_releases_project_id_project_version_attempt",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "cancelled_gate_ids",
                table: "rollback_events");

            migrationBuilder.DropColumn(
                name: "cancelled_job_ids",
                table: "rollback_events");

            migrationBuilder.DropColumn(
                name: "requeued_step_ids",
                table: "rollback_events");

            migrationBuilder.DropColumn(
                name: "restored_phase",
                table: "rollback_events");

            migrationBuilder.DropColumn(
                name: "attempt",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "releases");

            migrationBuilder.RenameColumn(
                name: "error_message",
                table: "releases",
                newName: "error");

            // The old rule allowed one succeeded release per version: later succeeded re-releases of a version can't be
            // represented, so they are marked failed before the old index comes back.
            migrationBuilder.Sql(
                """
                UPDATE releases AS r
                SET status = 'failed'
                WHERE r.status = 'succeeded'
                  AND EXISTS (
                      SELECT 1 FROM releases AS o
                      WHERE o.project_id = r.project_id AND o.project_version = r.project_version AND o.status = 'succeeded'
                        AND (o.created_at, o.id) < (r.created_at, r.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ux_releases_project_id_project_version_succeeded",
                table: "releases",
                columns: new[] { "project_id", "project_version" },
                unique: true,
                filter: "status = 'succeeded'");
        }
    }
}
