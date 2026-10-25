<!-- Adapted from stack-packs fe-vue@1.0.0 skills/fe-vue/SKILL.md, snapshot 2026-10-25 -->

# Vue 3 + Vite (TypeScript SPA)

## Detect

Read `package.json` (vue, router, pinia, query library, UI library), `vite.config.*`, `tsconfig*.json` and `src/`. Mirror the nearest feature. SPA only (Nuxt/SSR is out of scope).

## Scaffold

Write what `create-vue` creates with TypeScript, Router, Pinia, Vitest, Playwright and ESLint, under `{fe}/`, plus `.nvmrc`. Use current stable versions you are confident exist; no lockfile (CI creates it).
- Add `@tanstack/vue-query`, `vee-validate`, `@vee-validate/zod`, `zod`, `openapi-fetch`; dev `@testing-library/vue`, `msw`, `openapi-typescript`.
- Scripts: `dev`, `build`, `preview`, `typecheck` (`vue-tsc --noEmit`), `lint`, `test` (`vitest run`), `test:e2e`, `gen:api`.
- Dev proxy: `server.proxy['/api']` in `vite.config.ts` to the backend port.

## Conventions

- `<script setup lang="ts">` with typed `defineProps`/`defineEmits`; composables (`useX`) for shared logic.
- Server state in TanStack Vue Query (key factories, invalidate after mutations). Pinia for client-only state, never cached server data.
- Vue Router with lazy routes and `beforeEach` guards using route `meta` for auth and permissions.
- Forms: VeeValidate + Zod; server validation errors via `setErrors`.
- Folders: `src/features/<feature>/`, `src/components/ui/`, `src/composables/`, `src/stores/`, `src/lib/` (errors, permissions, config), `src/api/`.
- Accessible and mobile-first.

## API consumption

- `gen:api` = `openapi-typescript ../backend/openapi/api.json -o src/api/generated/schema.ts`. Hand-write that file in the generator's shape when the contract changes; CI regenerates and diffs it.
- `openapi-fetch` client in `src/api/client.ts`: same-origin/proxy `''`; cross-origin `VITE_API_BASE_URL` (or runtime config) plus `credentials: 'include'` with cookies.
- MSW handlers typed from the schema. Supabase BaaS mode: `@supabase/supabase-js` with generated DB types.

## Errors, permissions, states

`src/lib/errors.ts` maps `errorCode` to copy; `src/lib/permissions.ts` holds pure helpers from the spec. Handle 401 (sign-in with `redirect`), 403, 409. Every data view has loading, empty, error and forbidden states.

## Config

`import.meta.env.VITE_*` is public and build-time: `VITE_API_BASE_URL` (cross-origin), `VITE_SUPABASE_URL` and `VITE_SUPABASE_PUBLISHABLE_KEY` (Supabase). Per-environment values on one build: fetch `/config.json` at startup. No secrets.

## Testing

Vitest + `@testing-library/vue` (or Vue Test Utils) + MSW, named after FR IDs: visible behaviour, forms, guards, permissions, error copy. E2E (Playwright) belongs to the test-engineer.

## Gate

In `{fe}`: `npm ci && npm run typecheck && npm run lint && npm test && npm run build` (`npm install` until a lockfile exists). With a backend: `npm run gen:api && git diff --exit-code src/api/generated`.

## Containerise

Node build stage, then `nginxinc/nginx-unprivileged` serving `dist/` on 8080 with an `/api` proxy (same-origin), SPA fallback (`try_files $uri /index.html`), long cache on hashed assets and `no-cache` on `index.html`.

## Deploy notes

Output `{fe}/dist`, with an SPA fallback. GitHub Pages project sites: `base: '/<repo>/'` and `createWebHistory(import.meta.env.BASE_URL)` (or hash history).

## Checklist

- [ ] `<script setup>` + TS, server state in Vue Query, and Pinia for client state only.
- [ ] Only the generated client is used, and it matches the current contract.
- [ ] Guards and permissions come from the spec, error codes map to copy, and every state is present.
- [ ] Accessibility basics are met, tests cover the FRs, and the gate passes.
