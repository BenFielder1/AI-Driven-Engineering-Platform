<!-- Adapted from stack-packs data-drizzle@1.0.0 skills/data-drizzle/SKILL.md, snapshot 2026-10-25 -->

# Drizzle ORM (PostgreSQL)

## Detect
Mirror the installed `drizzle-orm`/`drizzle-kit` versions, `drizzle.config.ts`, schema files, `drizzle/` (with `meta/_journal.json`) and driver (`pg` or `postgres`).

## Compatible with
`be-express`, `be-nestjs`, `fe-nextjs` full-stack. Also Supabase host mode.

## Setup
- `drizzle-orm` + `pg` (or `postgres`); dev `drizzle-kit`, `@types/pg`; current stable versions you are confident exist, Node pinned in `.nvmrc`.
- `drizzle.config.ts`: `dialect: 'postgresql'`, `schema: './src/db/schema/*.ts'`, `out: './drizzle'`, URL `DIRECT_URL ?? DATABASE_URL`, `casing: 'snake_case'`.
- `src/db/client.ts`: one `Pool` + `drizzle({ client, schema, casing: 'snake_case' })`, injected (Nest provider, `createApp(deps)`) or a server-only Next.js singleton.
- `src/db/migrate.ts`: `migrate()` from `drizzle-orm/node-postgres/migrator` over `DIRECT_URL ?? DATABASE_URL`, then `pool.end()`; built to `dist/db/migrate.js`. Script `db:migrate` = `node dist/db/migrate.js`. No `push` script.

## Modelling
- `pgTable('orders', { id: uuid().primaryKey().defaultRandom(), name: varchar({ length: 120 }).notNull() }, (t) => [uniqueIndex(…).on(…)])`.
- `numeric({ precision, scale })`, `timestamp({ withTimezone: true })` in UTC, `.references(() => x.id, { onDelete })`, indexes on foreign keys and predicates.
- Case-insensitive uniqueness: unique index on `lower(col)` or a `citext` custom type. `pgEnum` or checked text.
- `version` column, update `where(and(eq(id), eq(version)))`; zero rows → 409.
- Select only needed columns, `db.transaction` for multi-step writes, parameterised `sql` only.

## Migrations
- **One migration per step**, hand-written in the `drizzle-kit generate` shape: `drizzle/<idx>_<name>.sql` (`--> statement-breakpoint` between statements), a new `meta/_journal.json` entry, and `meta/<idx>_snapshot.json` (previous snapshot plus the change, new `id`, `prevId` = previous `id`).
- Write renames as `RENAME COLUMN`, plus `CREATE EXTENSION` and data moves by hand.
- **Expand/contract** for breaking changes. **Never edit base-commit migrations or snapshots**; only the journal grows. Never `drizzle-kit push`.
- **Migrate command:** `npm run db:migrate`, the platform's pre-deploy step; on serverless hosts the build runs it only when `AIDE_RUN_MIGRATIONS=true`.
- **CI:** `drizzle-kit check`, then `drizzle-kit generate` + `git diff --exit-code -- drizzle` (no unmigrated schema change).

## Local database
`postgres:<major>` pinned to match production, `POSTGRES_*`, `PGDATA` in a volume subdirectory, `pg_isready`, port 5432, named volume.

## Testing
`@testcontainers/postgresql` in global setup, run the migrator, share; truncate between tests. Domain logic without the DB.

## Production notes
- Long-lived servers: the platform's internal URL or the Supabase session pooler (`:5432`).
- Serverless: transaction pooler (`:6543`; `prepare: false` with postgres.js) for the app, `DIRECT_URL` (session pooler) for the migrator. Never `db.<ref>.supabase.co`.
- SSL on managed hosts; containers ship `dist/db/`, `drizzle/` and runtime dependencies.

## Checklist
- [ ] Explicit schema with snake_case casing.
- [ ] One expand/contract-safe migration per step (SQL, journal, snapshot); base-commit files untouched; no `push`.
- [ ] `db:migrate` is the pre-start step; tests use migrated Postgres.
