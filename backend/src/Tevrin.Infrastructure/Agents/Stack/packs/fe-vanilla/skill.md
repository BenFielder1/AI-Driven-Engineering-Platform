<!-- Adapted from stack-packs fe-vanilla@1.0.0 skills/fe-vanilla/SKILL.md, snapshot 2026-10-25 -->

# Pure HTML / CSS / JavaScript

## Detect

Is there a `package.json`? Vite or no-build? Multi-page or single page, the CSS approach and any web components. Mirror what exists.

## Scaffold

Pick one mode and record it in the README:
- **No-build:** `{fe}/` holds `index.html` and other pages, `css/`, `js/` (ES modules via `<script type="module">`); `package.json` only for dev tools.
- **Vite** (multiple pages, npm deps or env config): what Vite's `vanilla` template creates; multi-page apps list each HTML entry in `build.rollupOptions.input`.

Either way, with current stable versions you are confident exist, `.nvmrc`, and no lockfile (CI creates it):
- Dev deps: `eslint`, `prettier`, `stylelint` + `stylelint-config-standard`, `html-validate`, `typescript` (for `checkJs`), `vitest`, `jsdom`, `@testing-library/dom`, `@testing-library/user-event`, `msw`, `@playwright/test`, `openapi-typescript`.
- Scripts: `dev`, `build` (Vite, or a no-op), `typecheck` (`tsc -p jsconfig.json`), `lint` (`eslint . && stylelint "**/*.css" && html-validate "**/*.html"`), `test`, `test:e2e`, `gen:api`.
- `jsconfig.json`: `checkJs`, `strict`, `noEmit`, `module: ESNext`, `target: ES2022`, `moduleResolution: Bundler`, `include: ["js/**/*.js", "src/**/*.js"]`.

## Conventions

- HTML first: semantic elements, real `<form>`s with native validation, `<button>` for actions, `<a>` for navigation; works or degrades clearly before JS loads.
- CSS: custom-property tokens, logical properties, `clamp()`, container queries, `prefers-color-scheme` and `prefers-reduced-motion`; mobile-first, no framework unless the spec says so.
- JS: ES modules, one per feature (`js/features/<feature>.js`), pure functions plus thin DOM glue, event delegation, `fetch` with `AbortController`, no global state beyond a tiny store module.
- Types via JSDoc checked by `tsc`, e.g. `/** @typedef {import('./api/generated/schema').components['schemas']['Order']} Order */`.
- Widgets are custom elements (`extends HTMLElement`), no shadow DOM unless isolation is needed. Accessible (labels, focus styles, ARIA only where needed, keyboard, AA contrast).

## API consumption

- `gen:api` = `openapi-typescript ../backend/openapi/api.json -o src/api/generated/schema.d.ts` (Vite) or `-o js/api/generated/schema.d.ts` (no-build): types only. Hand-write it in the generator's shape when the contract changes; CI regenerates and diffs it.
- `api/client.js` wraps `fetch`: base URL from the origin model, JSON headers, credentials with cookies, problem details parsed into an `ApiError` with `errorCode`, JSDoc-typed against the schema. With Vite, `openapi-fetch` may be used instead.
- MSW in tests. Supabase BaaS mode: the `@supabase/supabase-js` ESM build with Vite.

## Errors, permissions, states

`lib/errors.js` maps `errorCode` to copy; `lib/permissions.js` holds pure helpers from the spec. Render loading, empty, error and forbidden states explicitly, with an `aria-live` region for async status.

## Config

Vite mode: public, build-time `VITE_API_BASE_URL` (cross-origin), `VITE_SUPABASE_URL` and `VITE_SUPABASE_PUBLISHABLE_KEY` (Supabase). No-build mode: a `config.js`/`config.json` per environment with the same values, written by the deploy step. No secrets.

## Testing

Vitest + jsdom + Testing Library DOM for pure functions and DOM modules mounted into a fixture, MSW for the network, named after FR IDs. `html-validate` and stylelint run in lint. Playwright journeys belong to the test-engineer.

## Gate

In `{fe}`: `npm ci && npm run typecheck && npm run lint && npm test && npm run build` (`npm install` until a lockfile exists). With a backend: `npm run gen:api && git diff --exit-code` on the generated schema.

## Containerise

`nginxinc/nginx-unprivileged` serves the static files (or Vite `dist/`) on 8080, with an `/api` proxy for same-origin and cache headers. Multi-page: `try_files $uri $uri/ $uri.html =404`; single page: fall back to `/index.html`.

## Deploy notes

Output is `{fe}/` (no-build) or `{fe}/dist` (Vite); any static host. GitHub Pages project sites: relative URLs, or Vite `base: '/<repo>/'`.

## Checklist

- [ ] Semantic HTML that works without JS where the spec allows. Native form validation, plus server error mapping.
- [ ] `checkJs` is clean, with JSDoc types from the generated schema.
- [ ] CSS uses tokens, is responsive and respects user preferences.
- [ ] Lint (ESLint, stylelint, html-validate) and tests pass, and accessibility basics are met.
