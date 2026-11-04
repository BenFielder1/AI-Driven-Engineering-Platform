---
title: Testing
parent: Reference
nav_order: 4
---

# Testing

| Suite | Command | Notes |
| --- | --- | --- |
| Backend unit + integration | `cd backend && dotnet test` | Integration tests use Testcontainers, so Docker must run |
| Frontend unit | `cd frontend && npm test` | Vitest, Testing Library, MSW |
| E2E | `cd frontend && npm run test:e2e` | Playwright. Default `BASE_URL` is `http://localhost:3000`; its `webServer` starts `npm run dev` unless `BASE_URL` is set |
| E2E on Kubernetes | `BASE_URL=http://localhost:8080 npm run test:e2e` | Needs a deployed app |
| One browser | `npx playwright test --project=chromium` | Projects: `chromium`, `firefox`, `webkit`, `msedge` (only if Edge is installed or `E2E_MSEDGE=1`), `a11y-setup`, `a11y` |
| Cross-browser | `BASE_URL=http://localhost:8080 npx playwright test --project=firefox --workers=1` | Also `webkit`, `msedge`. Runs the smoke, auth, lifecycle and permissions specs. Use `--workers=1`: registrations are limited to 5 per minute per IP |
| Accessibility | `npx playwright test --project=a11y` | axe, both themes. Admin pages need `E2E_ADMIN_EMAIL` and `E2E_ADMIN_PASSWORD`, otherwise that scan is skipped |
| Performance (v1) | `node scripts/perf/nfr-perf.mjs` | Targets `http://localhost:8080`; set `BASE_URL` to change. Creates a perf user and 50 projects in that database |
| Performance (v2) | `node scripts/perf/v2-perf.mjs` | Project list, execution runs and review cycles p95. Same target and the same side effect: it writes a perf user and projects |

First time: `cd frontend && npx playwright install`. For the dev loop, the API and database must already run. E2E uses the Fake provider and Local Git, so it needs no keys.

## Version-specific E2E settings

The v2 specs (`versions`, `import`, `preset`) need E2E settings. On Kubernetes, deploy with `./deploy/scripts/deploy-local.sh --e2e` first. In the dev loop, start the API with `Deployments__Provider=Fake`, `Deployments__AllowFake=true`, `Llm__DefaultProvider=fake` and `Git__Local__ImportRoot` pointing at a seeded repo (`scripts/ci/seed-e2e-repo-local.sh <dir>` creates `existing-app`; set `AIDE_IMPORT_ROOT` to the same folder for the specs). Registrations are limited to 5 per minute per IP. The specs wait for it.

## Regenerate generated files

```bash
cd backend && dotnet build src/Aide.Api      # writes backend/openapi/api.json
cd frontend && npm run gen:api               # writes src/api/generated/
```

Commit both generated outputs. There is no generated route tree: Next.js discovers routes from `src/app/`.

## The full gate

CI runs the gate on every push to `main` and `build/**` and on pull requests. See [CI and the commit gate](../operations/ci.md) for the command and the rules around it.
