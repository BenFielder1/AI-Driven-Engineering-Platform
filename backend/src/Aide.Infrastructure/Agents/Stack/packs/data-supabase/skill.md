<!-- Adapted from stack-packs data-supabase@1.0.0 skills/data-supabase/SKILL.md, snapshot 2026-10-25 -->

# Supabase

| Mode | Use when | Schema owner |
| --- | --- | --- |
| **BaaS** | No backend; the frontend uses Supabase Auth, the database (RLS and grants), Storage | `supabase/migrations/*.sql`, applied by AIDE's runner |
| **Host** | A backend or Next.js full-stack plus an ORM data pack | The ORM pack; never Supabase migrations too |

## Detect
Mirror `supabase/config.toml`, `supabase/migrations/`, `@supabase/supabase-js`/`@supabase/ssr`, `database.types.ts` and env names.

## Compatible with
BaaS: every frontend pack with Backend None (`fe-nextjs` via `@supabase/ssr`). Host: database host for the ORM data packs.

## Setup
- `supabase/config.toml` (local ports, `[auth]` local redirect URLs), `migrations/`, `seed.sql`, `tests/`. AIDE manages the remote project, keys and Auth URLs.
- `@supabase/supabase-js` (+ `@supabase/ssr`); current stable versions you are confident exist, Node pinned.
- Public env with the frontend prefix: `<prefix>SUPABASE_URL`, `<prefix>SUPABASE_PUBLISHABLE_KEY` (`sb_publishable_…`). The secret key (`sb_secret_…`) never reaches frontend code; Next.js server code reads `SUPABASE_SECRET_KEY` only for spec-required admin actions.
- One typed client `createClient<Database>` in `src/lib/supabase.ts`. Next.js: SSR clients with cookies; authorise with `auth.getUser()`, never `getSession()`.

## Modelling (BaaS)
- `public` tables with explicit types and constraints, `timestamptz`, `citext`, owner columns referencing `auth.users(id)`, indexes on foreign keys and policy predicates.
- **RLS on every table** plus one policy per role and action from the permission table (`using ((select auth.uid()) = owner_id)`). RPC-only tables get no write policies.
- **Grants:** new tables aren't exposed to the Data API by default: `grant … on table public.<t> to authenticated` (`anon` only where allowed); column grants limit updates.
- `security definer` functions: `set search_path = ''`, permissions re-checked, execute revoked from `public`, called via `rpc()`, errors carrying the errorCode.
- Views with `security_invoker = true`; Realtime publication only for subscribed tables.

## Migrations
- **One migration per step:** `supabase/migrations/<yyyyMMddHHmmss>_<name>.sql`, later than every existing version. Each file runs in one transaction (no `CONCURRENTLY`).
- AIDE's runner applies them over the session pooler (preview during rollout, production just before merge) and records CLI-compatible history. Agents never `link` or `db push`.
- **Never edit a base-commit migration** (`MIGRATION_HISTORY_DIVERGED`). **Expand/contract**: the schema reaches production before the code.
- Update `<frontend>/src/lib/database.types.ts` in the same step, in the `supabase gen types typescript` shape (`Tables` with `Row`/`Insert`/`Update`/`Relationships`, `Views`, `Functions`, `Enums`).
- `seed.sql` is local only. Host mode: nothing in `supabase/migrations/`.
- **CI:** Supabase CLI with Docker: `supabase db start`, `supabase test db`; optionally regenerate types with `--local` and diff.

## Local database
BaaS: the CLI local stack from `config.toml`, URL and keys in the git-ignored `.env`. Host: plain `postgres:<major>` matching the project.

## Testing
BaaS: pgTAP in `supabase/tests/` (or supabase-js integration tests) as `anon`, user A and user B, asserting allow and deny. Mock supabase-js in frontend unit tests. Host: the ORM pack's tests.

## Production notes
- Never the direct host `db.<ref>.supabase.co` (IPv6-only). Long-lived servers and migrations: session pooler `:5432`. Serverless apps: transaction pooler `:6543` with prepared statements off, plus `DIRECT_URL` = session pooler. SSL required.
- Host mode: the ORM's migrate command runs on the platform (pre-deploy, or the build when `AIDE_RUN_MIGRATIONS=true`); without a preview project, preview skips migrations.
- AIDE sets Auth `site_url` and redirect URLs; code builds redirects from the current origin. Free projects pause when idle.

## Checklist
- [ ] Mode matches the composition; one migrations owner.
- [ ] BaaS: RLS and grants on every table, definer functions re-check, types updated, no secret key in the frontend, RLS allow/deny tests.
- [ ] BaaS: one transactional, expand/contract-safe migration per step; base-commit migrations untouched.
- [ ] Host: pooler URLs correct, SSL on, ORM migrations on the platform.
