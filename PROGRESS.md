# Build progress (v3)

Spec: `SPEC.md` (v3; v1 and v2 in `docs/`) · Stack: `STACK.md` · Branch: `build/aide-v3` · Started: 2026-10-23

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

## Environment
- Tool versions: node 24.14.1 · dotnet SDK 10.0.402 · Docker 28.3.2 (installed, but not used by agents: commit gate) · Railway CLI: **not installed**
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben)
- VCS CLI auth: gh yes (BenFielder1) · Deploy CLI/MCP auth: Railway no (needed for M21: CLI install + `railway login` + project tokens by Ben)

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-94 | Next.js App Router app replaces Vite/TanStack Router | 3.1 | frontend | 1 | done | CI 39998009637 |
| FR-95 | Route parity: every v2 URL, search param and alias | 3.1 | frontend | 2 | done | CI 39998009637 + audit tests in CI 40088027372 |
| FR-96 | Pages ported as client components (TanStack Query + generated client) | 3.1 | frontend | 2 | done | CI 39998009637 |
| FR-97 | Auth redirects (returnUrl, safeReturnUrl) | 3.1 | frontend | 2 | done | CI 39998009637 + audit tests in CI 40088027372 |
| FR-98 | Runtime `/api` proxy incl. WebSockets to `BACKEND_URL` | 3.2 | frontend | 1 | done | CI 39998009637 |
| FR-99 | Security headers + CSP nonce | 3.2 | frontend | 1 | done | CI 39998009637 |
| FR-100 | Approval-token redaction in web logs | 3.2 | frontend | 1 | done | CI 39998009637 |
| FR-101 | First paint: theme, fonts, favicon, titles | 3.1 | frontend | 1 | done | CI 39998009637 + audit tests in CI 40088027372 |
| FR-102 | Dev loop on :3000 proxying to :5080 | 3.1 | frontend + backend config | 1 | done | CI 39998009637 |
| FR-103 | Standalone web image, non-root, :8080, `/healthz` | 3.2 | frontend + deploy | 1 | done | CI 39998009637 |
| FR-104 | Local k8s on the Next.js image | 3.3 | deploy | 3 | done (CI); manual Docker Desktop check pending | CI 39998009637 |
| FR-105 | Configurable forwarded-header hops | 3.4 | backend | 1 | done | CI 39998009637 |
| FR-106 | API entrypoint: volume ownership, drop to `$APP_UID` | 3.4 | backend + deploy | 1 | done | CI 39998009637 |
| FR-107 | `.railway/railway.ts` IaC | 3.5 | deploy | 4 | written; first `config plan` pending (Ben) | static review |
| FR-108 | `preview` + `production` environments | 3.5 | deploy | 4 | documented; environments created by Ben | static review |
| FR-109 | Pre-deploy migrations | 3.5 | deploy + data | 4 | in IaC; verified on first deploy | static review |
| FR-110 | GitHub Actions deploy workflow | 3.5 | deploy + CI | 4 | written; runs once on main + Railway secrets | static review |
| FR-111 | `smoke-remote.sh` | 3.5 | deploy | 4 | written; runs on first deploy | static review |
| FR-112 | Railway operations docs | 3.5 | docs | 5 | done | 4bad5f8, CI 40088027372 |

v1 (FR-1–FR-52) and v2 (FR-53–FR-93) requirements stay in force. Their tests must stay green on CI.

## Phases
- [x] 0 Baseline (M0, base: e385c52): CI green on e385c52 (all 9 jobs): https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/39948638817
  - v2 Vite bundle (CI build), for the §8 "≤ 10% larger" check:
    - entry `index.js` 347.44 kB (gzip 109.95 kB)
    - `app-shell.js` 142.49 kB (gzip 42.94 kB)
    - CSS 62.35 kB (gzip 11.12 kB)
    - initial JS on `/projects` ≈ entry + app-shell ≈ 489.9 kB (gzip ≈ 152.9 kB), plus the projects route chunk
- [x] 1 Next.js foundation (M17) ‖ backend hosting (M20) (base 37fcaca): d4d47ac + fix d10c96f. CI green on 31ba6da: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/39998009637
- [x] 2 Page port (M18): gated together with phase 1 (d4d47ac); see Decisions
- [x] 3 Local Kubernetes + CI (M19): 31ba6da, CI green: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/39998009637. The Docker Desktop run is a manual check (see below)
- [~] 4 Railway (M21): IaC, deploy workflow, smoke-remote and docs written and reviewed (static). First real `railway config plan`/apply, preview deploy and smoke need Ben's Railway setup; production only on request
- [x] 5 Final (M22): acceptance audit over v1+v2+v3 (no regressions; 0 Critical). Audit fixes, Railway CI checks and docs sync in 4bad5f8. CI green (10 jobs): https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/40088027372

## Manual checks (Ben, Docker Desktop)
- FR-104: run `./deploy/scripts/deploy-local.sh`. The web rollout should become ready on `/healthz`, and `smoke.sh` should pass (`/healthz`, `/` 307/200, `/login` CSP nonce + XFO DENY + nosniff, `/api/health/ready`).
- FR-104: run `./deploy/scripts/deploy-local.sh --e2e`. Smoke should pass and the import repo should be seeded.
- FR-104: run `BASE_URL=http://localhost:8080 npm --prefix frontend run test:e2e --workers=1` in Chromium, Firefox and WebKit, including a SignalR live update through `web`.
- FR-105 on Docker Desktop: the LoadBalancer (externalTrafficPolicy Cluster) probably SNATs the browser address, so all browsers share one login/register rate-limit bucket. This was the same with nginx in v2. Per-client IP is covered by the FR-105 integration tests and will be checked on Railway.
- FR-104/FR-103: `kubectl --context docker-desktop -n aide logs deploy/web` should show JSON request lines and no EROFS/cache errors.
- FR-106: build the API image and start it on a root-owned empty `/data`. Check that keys are written to `/data/keys`, the app runs as uid 1654 (`ps`), and `/api/health/ready` returns 200.

## Follow-ups queued
- All phase 1–4 follow-ups are done. The Railway variables they named (`TRUST_EDGE_FORWARDED=true` on web, `ForwardedHeaders__ForwardLimit=2` on api, no `RAILWAY_RUN_UID`) are in `.railway/railway.ts`.

## First Railway deploy (Ben, then agents within `preview` autonomy)
Setup steps are in `deploy/railway/README.md`. Things only a real run can prove (from the reviews and audit):
- `railway config plan` shows no drift after apply, and `preserve()` on unset optional secrets plans no change. *(2026-10-24: the first plan on `preview` evaluated cleanly: 4 to add (postgres, volume api-data, services api and web), 0 to change or destroy, and no error from the 19 unset `preserve()` variables. Drift after apply is still to confirm.)*
- The pre-deploy `/bin/sh -c '…'` quoting works through the image ENTRYPOINT, and the migration runs before `api` starts.
- `railway up` from the repo root honours each service's root directory (fallback: `railway up backend --path-as-root`).
- The JSON shapes of `deployment list`, `domain list` and `tcp-proxy list`, and that a project token may call them. `up --ci` fails when the build fails.
- Railway's edge appends exactly one `X-Forwarded-For` entry, so `ForwardLimit=2` resolves the client IP (FR-105). WebSockets work through `web` (FR-98).
- `smoke-remote.sh` passes, including the sign-in and WebSocket round trip with the smoke account (FR-111).
- §8: Railway p95 for the project list is < 500 ms. The `aide` CLI works against the Railway web URL (D-V3-12).

## Decisions
- 2026-10-23 — The gate runs on GitHub Actions CI, not locally. CI now also triggers on pushes to `build/**` — the machine can't run Docker + integration tests + E2E — SPEC updated: yes (M0, FR-104, §8)

- 2026-10-23 — M17 and M18 are gated as one CI phase. Sub-steps (1a foundation, 1b route-group ports, 1c integration) are committed locally and pushed together — about 90 files import TanStack Router, so a foundation-only push can't pass E2E — SPEC updated: no
- 2026-10-23 — FR-106: the API image has no `USER` line and starts as root. `entrypoint.sh` fixes `/data` ownership and drops to `$APP_UID` with `setpriv`, so Railway needs no `RAILWAY_RUN_UID`. Kubernetes still forces uid 1654 through `securityContext`. Trade-off: a plain `docker run` starts as root until the drop — SPEC updated: no (matches D-V3-10)
- 2026-10-23 — FR-105: `X-Forwarded-Host` is now honoured too (v2 handled only For and Proto). `ForwardedHeaders:KnownNetworks` replaces the trusted-proxy lists when set — SPEC updated: no

- 2026-10-23 — FR-98: the web server is a custom Node server (`frontend/server.ts`, run by Node 24's built-in TypeScript support, helpers in `src/server/*`). It handles the `/api` HTTP and WebSocket proxy, the CSP nonce and headers, and redacted JSON logs. Next 16.4.2's built-in proxy rewrites Host, times out after 30 s, answers 500 instead of 502 problem+json, logs the upstream URL (leaking approval tokens), and fixes `rewrites()` at build time — SPEC updated: no (SPEC allows this fallback)
- 2026-10-23 — FR-98: a request body over 10 MB → 413 with `errorCode` `VALIDATION` (SPEC doesn't name a code) — SPEC updated: no
- 2026-10-23 — FR-97: there's no early redirect in Next.js middleware/`proxy.ts` when the `aide.auth` cookie is missing. The client guard in the `(app)` layout is the only redirect (SPEC makes the early redirect optional) — SPEC updated: no
- 2026-10-23 — Navigation adapter (`src/lib/navigation.ts`) serialises search values as plain strings. TanStack JSON-quoted numeric strings, so URLs can differ in quoting only — SPEC updated: no

- 2026-10-23 — §8 bundle baseline corrected: the first v2 figure (152.9 kB gzip) left out the `/projects` route's own chunks, including recharts at 88.5 kB gzip. Both builds are re-measured the same way in the phase-1 fixes — SPEC updated: no

- 2026-10-23 — §8 bundle, re-measured the same way for both builds (gzip -9 of every JS chunk loaded for the initial `/projects` document): v2 (37fcaca, Vite manifest closure of entry + `_app` + `projects/index`) **355.0 kB**; v3 (Next, after lazy-loading Sparkline, CommandPalette and SignalR) **300.0 kB**, i.e. **−15.5%** (budget ≤ +10%) — SPEC updated: no
- 2026-10-23 — FR-100: approval tokens are redacted anywhere in a path, query or Referer, after `/`, `%2F` or `%252F`. Paths like `/projects/approvals/x` get over-redacted in logs, which is acceptable — SPEC updated: no
- 2026-10-23 — FR-98: the web server overwrites `X-Forwarded-Proto` and `X-Forwarded-Host` from the connection unless `TRUST_EDGE_FORWARDED=true` (Railway web). A path that resolves outside `/api` → 400 `VALIDATION` — SPEC updated: no
- 2026-10-23 — CI E2E runs against a production Next.js server (`npm run build && npm start`, set up by playwright.config.ts when `CI` is set), not `next dev`, to avoid per-route cold compiles. The dev loop still uses `npm run dev` — SPEC updated: no

- 2026-10-23 — FR-109: the pre-deploy command is shell-wrapped (`/bin/sh -c 'exec /app/efbundle --connection "$ConnectionStrings__Aide"'`), because Railway runs commands in exec form and wouldn't expand the variable — SPEC updated: yes
- 2026-10-23 — FR-107: Postgres is pinned to 17 with `database()` and the `postgres-ssl:17` image. The `postgres()` helper now gives 18 — SPEC updated: yes
- 2026-10-23 — FR-107: `api` also sets `PORT=8080`, because Railway's healthcheck targets `$PORT`. SPEC Q4: no IPv6 change is needed. Kestrel `*:8080` (ASPNETCORE_HTTP_PORTS) is dual-stack, and Node `listen(port)` binds `::` — SPEC updated: no
- 2026-10-24 — FR-107/FR-112: a variable set in Railway but not declared in `.railway/railway.ts` is planned for deletion, which also makes CI's `config plan --detailed-exit-code` fail. So the signing key and the full optional set (`Llm__*`, `GitHub__App__*`, `GitHub__OAuth__*`, `Smtp__*`) are declared `preserve()` up front, for both environments. railway 3.16.0 only emits a `{ type: "preserve" }` marker, and the plan engine is in the CLI, so whether `preserve()` on an unset variable is a no-op can't be shown offline. It's a first-plan check in deploy/railway/README.md, with a fallback to per-environment `defineRailway((ctx) => …)` lists. The drift error now says to declare the variable or review the plan locally, and never to apply a plan that deletes secrets — SPEC updated: no
- 2026-10-24 — FR-109/FR-110: `railway up --ci` exits after the build, so deploy.yml polls `railway deployment list --json` for the deployment that upload created (the newest id not listed before it) until `SUCCESS`. It fails on `FAILED`/`CRASHED`/`REMOVED`/`SKIPPED`/`NEEDS_APPROVAL` or a timeout (api 15 min, naming a failed migration as the likely cause, then web 10 min). web deploys only after api is live — SPEC updated: no
- 2026-10-24 — FR-111: before the smoke test, deploy.yml fails if `api` has any domain (`railway domain list`) or TCP proxy (`railway tcp-proxy list`). Both are read-only, and plain `railway domain` is never called. `API_PUBLIC_URL` stays for manual smoke runs — SPEC updated: no
- 2026-10-24 — FR-109: the 600 s pre-deploy timeout is in IaC (`deploy.preDeployTimeoutSeconds`), not a dashboard step — SPEC updated: no
- 2026-10-24 — FR-110: a `workflow_run` for a commit older than the newest main commit with green CI (push or manual CI run) is skipped with a notice, so out-of-order CI completions never roll preview back — SPEC updated: no

- 2026-10-24 — §8 observability: the web server logs one JSON line per request, except `/_next/static/*` assets and `/healthz` probes (noise). Errors are always logged — SPEC updated: no
- 2026-10-24 — FR-111: the "API not public" check runs in `deploy.yml` (Railway domain and TCP-proxy listing), not in `smoke-remote.sh` unless `API_PUBLIC_URL` is set — SPEC updated: yes
- 2026-10-24 — SPEC §9: `TRUST_EDGE_FORWARDED=true` was added to the Railway web variables — SPEC updated: yes

## Status at end of agent build (2026-10-24)
- **Done on CI:** FR-94–FR-103, FR-105, FR-106, FR-112, plus all v1/v2 tests (backend 3646, Vitest 72 files, E2E v1/v2/a11y).
- **Done on CI, manual check pending (Ben):** FR-104 (Docker Desktop deploy + 3-browser E2E), FR-106 (start on a root-owned volume).
- **Written and reviewed, pending the first Railway deploy (Ben's setup, then `preview`):** FR-107–FR-111. See "First Railway deploy".
- **v3 completion:** `deploy.yml` only deploys commits on `main`, so the first preview deploy follows the merge of `build/aide-v3`. Order: set up Railway and the GitHub environments, merge (CI on `main` → preview deploy + smoke), verify the First Railway deploy checklist, then move SPEC.md and PROGRESS.md to `docs/v3/` and add the CLAUDE.md row.

- 2026-10-24 — Railway IaC on Windows: `railway/iac` 3.16 runs `process.env._ || "railway"` with `execFileSync` to check the CLI version. That can't run the npm shims, so plan reports "requires CLI 5.42.1" on 5.71. Workaround: PowerShell `$env:_ = <path to railway.exe>` (documented in deploy/railway/README.md). Linux CI is unaffected — SPEC updated: no

## Known issues
- Local Vitest at default parallelism: 15 shell/projects/components tests time out at 20 s on this machine under load, and pass with `--maxWorkers=2` or on their own. Watch for it on CI.
