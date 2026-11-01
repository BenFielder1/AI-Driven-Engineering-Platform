<!-- Adapted from stack-packs data-prisma@1.0.0 skills/data-prisma/SKILL.md, snapshot 2026-10-25 -->

# Prisma (PostgreSQL)

## Detect
Mirror the installed major: `schema.prisma`, `prisma.config.ts`, generator (`prisma-client` with `output` on recent majors), driver adapter (`@prisma/adapter-pg`) and `prisma/migrations/`.

## Compatible with
`be-express`, `be-nestjs`, `fe-nextjs` full-stack (`backend/prisma/` or `frontend/prisma/`). Also Supabase host mode.

## Setup
- `prisma` (dev), `@prisma/client`, the adapter if the major uses one; current stable versions you are confident exist, Node pinned in `.nvmrc`.
- App connects with `DATABASE_URL`; the CLI uses `DIRECT_URL` (`directUrl` in the datasource, or the CLI URL in `prisma.config.ts`, per major).
- `prisma generate` in `postinstall`/build; generated output never edited, git-ignored. Scripts: `db:migrate` (`prisma migrate deploy`), `db:seed`; no `db push`.
- One client: Express via `createApp(deps)`; NestJS `PrismaService` (`OnModuleInit`/`OnModuleDestroy`); Next.js `src/server/db.ts` (`server-only`, global singleton in dev).

## Modelling
- PascalCase models with `@@map`/`@map` to snake_case; `@id @default(uuid(7))` where supported.
- `@db.VarChar(n)`, `@db.Decimal(p, s)`, `@unique`/`@@unique` from rules, `@@index` on foreign keys and predicates, `onDelete` on relations.
- `@db.Citext` (extension created in a migration), `DateTime @db.Timestamptz` in UTC, Prisma enums or validated strings.
- `version Int` updated with `where: { id, version }`; zero rows → 409.
- `select` only needed fields, deliberate `include`, `$transaction` for multi-step writes, tagged `$queryRaw` only.

## Migrations
- **One migration per step**, hand-written in the `prisma migrate dev` shape: the schema change plus `prisma/migrations/<yyyyMMddHHmmss>_<name>/migration.sql` with the SQL Prisma would emit (quoted identifiers, `"<table>_<col>_fkey"`, `"<table>_<col>_key"`).
- Write renames as `RENAME`, plus `CREATE EXTENSION` and data moves by hand.
- **Expand/contract** for breaking changes. **Never edit a base-commit migration**; never `db push`.
- **Migrate command:** `npx prisma migrate deploy` over `DIRECT_URL`, the platform's pre-deploy step (CLI in the image); on serverless hosts the build runs it only when `AIDE_RUN_MIGRATIONS=true`.
- **CI:** `prisma validate`, `prisma generate`, `prisma migrate diff --from-migrations prisma/migrations --to-schema prisma/schema.prisma --exit-code` (shadow database from the CI Postgres service; flags per major).

## Local database
`postgres:<major>` pinned to match production, `POSTGRES_*`, `PGDATA` in a volume subdirectory, `pg_isready`, port 5432; `DATABASE_URL` and `DIRECT_URL` both `postgresql://…@localhost:5432/<db>`.

## Testing
`@testcontainers/postgresql` in global setup: set both URLs, `prisma migrate deploy`, share; `TRUNCATE … RESTART IDENTITY CASCADE` between tests.

## Production notes
- Long-lived servers: the platform's internal URL (Railway `${{Postgres.DATABASE_URL}}`) or the Supabase session pooler (`:5432`).
- Serverless: transaction pooler (`:6543`, `pgbouncer=true`) for the app, session pooler as `DIRECT_URL`. Never `db.<ref>.supabase.co`.
- `sslmode=require` on managed hosts; the generated client (and any engine) must match the runtime OS and architecture.

## Checklist
- [ ] Setup matches the installed major; one client instance.
- [ ] Explicit constraints, UTC, version locking where edits race.
- [ ] One expand/contract-safe migration per step matching the schema; base-commit migrations untouched; no `db push`.
- [ ] `migrate deploy` over `DIRECT_URL` is the pre-start step; tests use migrated Postgres.
