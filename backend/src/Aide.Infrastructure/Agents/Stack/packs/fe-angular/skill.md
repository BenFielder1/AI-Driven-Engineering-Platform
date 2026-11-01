<!-- Adapted from stack-packs fe-angular@1.0.0 skills/fe-angular/SKILL.md, snapshot 2026-10-25 -->

# Angular

## Detect

Read `angular.json`, `@angular/*` versions, `tsconfig*.json`, the `ng test` builder (Vitest in recent CLIs, Karma/Jasmine in older), zoneless vs zone.js, UI library (Material, PrimeNG, Tailwind) and state (signals, NgRx, services). Follow the installed major's conventions and mirror the nearest feature.

## Scaffold

Write what `ng new` creates (routing, SCSS, no SSR, the CLI's default change detection and test runner) under `{fe}/`, plus `@angular-eslint` config and `.nvmrc`. Use current stable versions you are confident exist; no lockfile (CI creates it).
- Scripts: `start` (`ng serve --proxy-config proxy.conf.json`), `build`, `test` (`ng test --watch=false`), `lint` (`ng lint`), `typecheck` (`tsc -p tsconfig.app.json --noEmit`), `test:e2e`, `gen:api` (`orval`).
- `proxy.conf.json`: `{ "/api": { "target": "http://localhost:<backend port>", "secure": false, "changeOrigin": true } }`. Dev port 4200.

## Conventions

- Standalone components, `inject()`, built-in control flow (`@if`, `@for` with `track`, `@switch`).
- Signals for local state (`signal`, `computed`, `input()`, `output()`, `model()`); RxJS for streams and HTTP, bridged with `toSignal`/`toObservable`; `async` pipe or `takeUntilDestroyed`, no manual subscriptions.
- `app.config.ts`: `provideRouter(routes, withComponentInputBinding())`, `provideHttpClient(withFetch(), withInterceptors([...]))`.
- Lazy routes (`loadComponent`/`loadChildren`), functional guards (`CanActivateFn`) for auth and permissions.
- Typed reactive forms (`FormBuilder.nonNullable`) with spec validators; server validation errors on the controls.
- Folders: `src/app/features/<feature>/`, `src/app/shared/ui/`, `src/app/core/` (interceptors, guards, errors, permissions, config), `src/app/api/generated/`.
- Accessible (Material/CDK a11y, labels, focus) and mobile-first.

## API consumption

- Orval Angular services: `orval.config.ts` with `input: ../backend/openapi/api.json`, `output: { client: 'angular', target: 'src/app/api/generated', schemas: 'src/app/api/generated/model' }`. When the contract changes, hand-write the changed services and models in Orval's output shape; CI regenerates and diffs them.
- Origin: same-origin/proxy → base path `''` (`/api/...`); cross-origin → the base URL from runtime config; cookies → an interceptor sets `withCredentials: true`.
- Mocks: MSW typed with the generated models, or `HttpTestingController` in unit tests.
- Supabase BaaS mode: a `SupabaseService` wrapping `@supabase/supabase-js` with generated DB types.

## Errors, permissions, states

- An interceptor normalises problem details; `core/errors.ts` maps `errorCode` to copy.
- 401 redirects to sign-in with `returnUrl`; 403 and 409 shown inline.
- `core/permissions.ts`: pure helpers from the spec, used by guards and templates.
- Every data view has loading, empty, error and forbidden states (signals for status).

## Config

`environment*.ts` is build-time and public. Values that vary per environment on one build load from `assets/config.json` at startup (`provideAppInitializer`), written by the container or platform from `API_BASE_URL` (cross-origin), `SUPABASE_URL` and `SUPABASE_PUBLISHABLE_KEY` (Supabase). No secrets in `environment*.ts` or `config.json`.

## Testing

The CLI's runner with `TestBed` and component harnesses, or Angular Testing Library. Cover each FR's visible behaviour, validation, guards, interceptor error mapping and permissions, named after FR IDs. E2E (Playwright) belongs to the test-engineer.

## Gate

In `{fe}`: `npm ci && npm run typecheck && npm run lint && npm test && npm run build` (`npm install` until a lockfile exists). With a backend: `npm run gen:api && git diff --exit-code src/app/api/generated`.

## Containerise

Multi-stage: `node:<lts>` runs `npm ci && npm run build`; `nginxinc/nginx-unprivileged` serves `dist/<project>/browser` on 8080 with an `/api` proxy (same-origin), long cache for hashed files, `try_files $uri /index.html`, and `no-cache` on `index.html` and `assets/config.json`.

## Deploy notes

Output `{fe}/dist/<project>/browser`, with an SPA fallback. GitHub Pages project sites build with `--base-href /<repo>/`. Split deploys set the API base URL in runtime config.

## Checklist

- [ ] Standalone components, signals, built-in control flow, `inject()`, and no leaking subscriptions.
- [ ] Only the generated Orval services are used, and they match the current contract.
- [ ] The interceptor maps problem details, and guards and permissions come from the spec.
- [ ] Every state is present, accessibility basics are met, and it works at 360px.
- [ ] Tests cover the FRs, and the gate passes.
