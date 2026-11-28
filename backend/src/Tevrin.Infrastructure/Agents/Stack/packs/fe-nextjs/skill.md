<!-- Adapted from stack-packs fe-nextjs@1.0.0 skills/fe-nextjs/SKILL.md, snapshot 2026-10-25 -->

# Next.js (App Router, TypeScript)

## Detect

Read the `next` version in `package.json`, `next.config.*`, `app/` vs `pages/`, auth library, styling, data layer and tests. Follow the installed major: recent majors renamed `middleware.ts` to `proxy.ts`, default to Turbopack, removed `next lint` (use the ESLint CLI) and changed caching defaults.

## Scaffold

Write what `create-next-app` creates (TypeScript, ESLint, App Router, `src/`, `@/*` alias, Tailwind if wanted) under `{fe}/`, plus `.nvmrc`. Use current stable versions you are confident exist; no lockfile (CI creates it).
- Scripts: `dev`, `build`, `start`, `typecheck` (`tsc --noEmit`), `lint` (`eslint .`), `test` (`vitest run`), `test:e2e`, plus `gen:api` with a backend.
- Dev: `vitest`, `@vitejs/plugin-react`, `jsdom`, Testing Library, `msw`, `@playwright/test`; with a backend also `openapi-typescript` and `openapi-fetch`.

## Conventions

- Server Components by default; `"use client"` only for interactivity. Fetch on the server, pass serialisable props.
- Mutations: Server Actions (Zod, then `revalidatePath`/`revalidateTag`), or `app/api/**/route.ts` when a real HTTP API is needed.
- Folders: `src/app/` (routes, `loading.tsx`, `error.tsx`, `not-found.tsx`), `src/features/<feature>/`, `src/components/ui/`, `src/lib/` (errors, permissions, auth, db or api client), `src/server/` (`import 'server-only'`).
- Secrets only in server code; only `NEXT_PUBLIC_*` reaches the browser. Accessible and mobile-first.

## API consumption (modes)

**Full-stack (no backend pack)**
- The data pack provides the DB client in `src/server/db.ts`.
- Business rules in `src/server/domain/` (pure, unit-tested); handlers and actions stay thin.
- Actions return `{ ok: false, errorCode, fieldErrors? }`; route handlers return problem+json with the spec's `errorCode`.
- Auth per the spec (e.g. Auth.js, Supabase Auth), checked server-side in every action and handler, not only in `proxy.ts`/`middleware.ts`.

**UI for a separate backend**
- `gen:api` = `openapi-typescript ../backend/openapi/api.json -o src/api/generated/schema.ts` (`stack-api-contract`). Hand-write that file in the generator's shape when the contract changes; CI regenerates and diffs it.
- Call the backend from the server with `BACKEND_URL`, forwarding auth cookies or tokens, or `rewrites()` `/api/*` to it for client calls.
- `src/lib/errors.ts` maps problem-details `errorCode` to copy.

## Errors, permissions, states

`error.tsx` and `not-found.tsx` per segment, `loading.tsx` or Suspense for loading. Pure permission helpers from the spec, enforced on the server; the UI only hides controls.

## Config

- Server env (runtime): `DATABASE_URL` (full-stack), `BACKEND_URL` (ui mode), auth secrets.
- Public env, inlined at build: `NEXT_PUBLIC_API_BASE_URL` (cross-origin), `NEXT_PUBLIC_SUPABASE_URL` and `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY` (Supabase). Never read a non-public name in a `"use client"` file.
- Validate env at startup with Zod in `src/lib/env.ts`.

## Testing

Vitest for domain modules, action logic (DB mocked or test DB) and client components (Testing Library + MSW); integration tests for route handlers against a test DB per the data pack; tests named after FR IDs. E2E (Playwright) belongs to the test-engineer.

## Gate

In `{fe}`: `npm ci && npm run typecheck && npm run lint && npm test && npm run build` (`npm install` until a lockfile exists). In ui mode also `npm run gen:api && git diff --exit-code src/api/generated`.

## Containerise

`output: 'standalone'`. Multi-stage: `node:<lts>` builds; the runtime image copies `.next/standalone`, `.next/static` and `public`, runs `node server.js` as a non-root user on `PORT=8080`. Migrations run as a separate one-off step (data pack), never at server start.

## Deploy notes

Vercel natively; Netlify via its adapter; Cloudflare via its Next.js adapter (see `deploy-cloudflare`); Render or Railway as a Node service (`npm run build`, `npm start`, or the standalone image). GitHub Pages only with `output: 'export'`: no actions, route handlers or dynamic SSR.

## Checklist

- [ ] Server/client boundaries are right, and secrets stay server-only.
- [ ] Every action and handler validates input and checks auth and permissions on the server.
- [ ] `loading`, `error` and `not-found` exist per route, and error codes map to copy.
- [ ] The mode (full-stack or ui) matches the composition, and client components never access the DB.
- [ ] Tests cover the FRs, and the gate passes.
