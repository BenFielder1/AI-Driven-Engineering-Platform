This project uses the `nextjs-supabase` preset. These conventions apply to every step, whichever specialist writes it.
Where the technical specification is more specific, the specification wins.

### Stack
- Next.js (App Router) with TypeScript in strict mode (`strict`, `noUncheckedIndexedAccess`) and the `@/*` import alias.
- Tailwind CSS for all styling. No other CSS framework or component library unless the specification lists it.
- Supabase: Postgres, Auth, Row Level Security and Realtime, accessed through `@supabase/ssr` and `@supabase/supabase-js`.
- Zod for every input boundary (route params, query, body, forms, environment parsing).
- Vitest + Testing Library for unit and component tests, Playwright for end-to-end tests.
- GitHub Actions for CI, Vercel for hosting. Node is pinned in `.nvmrc` and `package.json` `engines`.

### Layout
```
app/                    pages and layouts (ui-engineer); app/api/** route handlers (api-engineer)
components/ui/          shared primitives (Button, Input, Dialog, Tabs, Toast, EmptyState)
components/<feature>/   feature components
lib/<domain>/           pure business rules + *.test.ts (domain-logic-engineer)
lib/api/                errors.ts (error-code -> status map, fromPostgresError), auth.ts (requireUser)
lib/supabase/           server.ts (createServerClient), client.ts (createBrowserClient)
lib/ui/                 error-messages.ts (error code -> copy), permission helpers (can*)
lib/types.ts            camelCase domain types and row -> domain mappers
lib/database.types.ts   GENERATED from the migrations (supabase gen types), never edited by hand otherwise
supabase/migrations/    one SQL migration per change; committed migrations are never edited
supabase/seed.sql       a user for every role, data in every important state
e2e/                    Playwright tests
proxy.ts                session refresh (middleware.ts on Next.js 15 and earlier)
```

### Rules every step follows
1. **Permissions are enforced in the database.** Every table has RLS enabled. Privileged or multi-row writes go through
   `security definer` RPCs that set `search_path` and re-check permissions. The UI and API checks are a convenience,
   never the only check.
2. **The service-role key never reaches the browser.** `SUPABASE_SERVICE_ROLE_KEY` (and any other server secret) is
   used only in server-only webhook or cron handlers the specification requires. It never appears in a `"use client"`
   file, a component, or a `NEXT_PUBLIC_` variable. Only `NEXT_PUBLIC_SUPABASE_URL` and `NEXT_PUBLIC_SUPABASE_ANON_KEY`
   are public.
3. **Authorisation uses `supabase.auth.getUser()`**, which verifies the JWT. Never authorise with `getSession()`.
4. **Business rules live in `lib/<domain>/`** as pure, deterministic functions. Routes and components call them;
   they never re-implement formulas, limits or constants.
5. **One error vocabulary.** Error codes come from the specification. `lib/api/errors.ts` maps code -> HTTP status;
   `lib/ui/error-messages.ts` maps code -> user copy. API errors have the shape
   `{ "error": { "code": "...", "message": "...", "details": ... } }`.
6. **Domain objects are camelCase.** Routes return mapped domain types from `lib/types.ts`, never raw rows.
7. **Server Components first.** Client components are leaf-level and only for interactivity, forms and Realtime.
8. **Accessible and mobile-first.** Designed at 360px, tap targets at least 44px, a label on every control, WCAG AA.
9. **Tests sit next to the code** (`*.test.ts(x)`) and name the requirement IDs they cover (e.g. `describe('FR-3 ...')`).
10. **No secrets in the repository.** Configuration comes from environment variables listed in `.env.example`.

### Scripts (package.json)
`dev`, `build`, `start`, `lint`, `typecheck` (`tsc --noEmit`), `test` (`vitest run`), `test:e2e` (`playwright test`),
`db:types` (`supabase gen types typescript --local > lib/database.types.ts`). CI and reviewers rely on these names.
