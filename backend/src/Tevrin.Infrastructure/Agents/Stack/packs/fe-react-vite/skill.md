<!-- Adapted from stack-packs fe-react-vite@1.0.0 skills/fe-react-vite/SKILL.md, snapshot 2026-10-25 -->

# React + Vite (TypeScript SPA)

## Detect

Read `package.json` (react, vite, router, state, styling, tests), `vite.config.*`, `tsconfig*.json` and `src/`. Mirror the closest existing feature and reuse its components.

## Scaffold

Write what Vite's `react-ts` template creates, under `{fe}/`, plus:
- TS `strict` + `noUncheckedIndexedAccess`, ESLint flat config, `.nvmrc` (Node LTS). Use current stable versions you are confident exist; no lockfile (CI creates it).
- Deps: `react-router` or TanStack Router (one), `@tanstack/react-query`, `react-hook-form` + `zod` + `@hookform/resolvers`, Tailwind unless the spec says otherwise, `openapi-fetch`. Dev: `vitest`, `jsdom`, Testing Library (`react`, `user-event`, `jest-dom`), `msw`, `@playwright/test`, `openapi-typescript`.
- Scripts: `dev`, `build`, `preview`, `typecheck` (`tsc -b --noEmit`), `lint`, `test` (`vitest run`), `test:e2e`, `gen:api`.
- Dev proxy in `vite.config.ts`: `server.proxy['/api'] = { target: 'http://localhost:<backend port>', changeOrigin: true }`.

## Conventions

- Folders: `src/features/<feature>/` (pages, components, hooks, tests), `src/components/ui/`, `src/lib/` (errors, permissions, config, queryClient), `src/api/` (client, `generated/`).
- Server state in TanStack Query with key factories (`orderKeys.detail(id)`); invalidate after mutations; never copy server data into `useState`.
- Forms: React Hook Form + Zod. Routes lazy-loaded, each with an error boundary.
- Accessible (semantic HTML, labels, dialog focus, keyboard, AA contrast) and mobile-first.

## API consumption

- `gen:api` = `openapi-typescript ../backend/openapi/api.json -o src/api/generated/schema.ts`. When the contract changes, hand-write that file in openapi-typescript's shape; CI regenerates and diffs it.
- `src/api/client.ts`: `createClient<paths>({ baseUrl })`. Same-origin/proxy → `''` (calls `/api/...`); cross-origin → `import.meta.env.VITE_API_BASE_URL` plus `credentials: 'include'` with cookies. Feature hooks wrap it.
- MSW handlers in `src/mocks/handlers/*.ts`, typed from the schema; always in tests, in dev only when `VITE_USE_MOCKS=true`.
- Supabase BaaS mode: `@supabase/supabase-js` with generated DB types (see `data-supabase`).

## Errors, permissions, states

- `src/lib/errors.ts` maps problem-details `errorCode` to copy and `errors` onto form fields.
- `src/lib/permissions.ts`: pure `can*` helpers from the spec. Handle 401 (sign-in with `?next=`), 403, 409.
- Every data view has loading, empty, error and forbidden states (read-only where specified).

## Config

`VITE_*` is public and baked in at build. Cross-origin: `VITE_API_BASE_URL` per environment. Supabase BaaS: `VITE_SUPABASE_URL`, `VITE_SUPABASE_PUBLISHABLE_KEY`. No secrets in the frontend.

## Testing

Vitest + Testing Library (by role/label, assert what users see) + MSW, named after FR IDs: visible behaviour, validation, permissions, error copy. E2E (`e2e/`, Playwright, `BASE_URL`) belongs to the test-engineer.

## Gate

In `{fe}`: `npm ci && npm run typecheck && npm run lint && npm test && npm run build` (`npm install` until a lockfile exists). With a backend: `npm run gen:api && git diff --exit-code src/api/generated`.

## Containerise

Multi-stage: `node:<lts>` runs `npm ci && npm run build`; `nginxinc/nginx-unprivileged` serves `dist/` on 8080. `nginx.conf`: `location /api/ { proxy_pass http://<backend service>:8080; }` (same-origin), long cache on `/assets/`, `/` with `try_files $uri /index.html` and `no-cache`.

## Deploy notes

Output `{fe}/dist`, with an SPA fallback to `index.html`. Static hosts proxy `/api` via rewrites (`proxy`) or use cross-origin.

## Checklist

- [ ] Only the generated client is used, its origin follows the composition, and generated files match the contract.
- [ ] Query keys and invalidation are correct, and forms validate with Zod.
- [ ] Every state is present, error codes map to copy, and permissions come from the spec.
- [ ] Accessibility basics are met, and it works at 360px.
- [ ] Tests cover the FRs, and the gate passes.
