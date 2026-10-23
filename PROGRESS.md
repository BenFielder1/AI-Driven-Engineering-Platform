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
| FR-94 | Next.js App Router app replaces Vite/TanStack Router | 3.1 | frontend | 1 | todo | — |
| FR-95 | Route parity: every v2 URL, search param and alias | 3.1 | frontend | 2 | todo | — |
| FR-96 | Pages ported as client components (TanStack Query + generated client) | 3.1 | frontend | 2 | todo | — |
| FR-97 | Auth redirects (returnUrl, safeReturnUrl) | 3.1 | frontend | 2 | todo | — |
| FR-98 | Runtime `/api` proxy incl. WebSockets to `BACKEND_URL` | 3.2 | frontend | 1 | todo | — |
| FR-99 | Security headers + CSP nonce | 3.2 | frontend | 1 | todo | — |
| FR-100 | Approval-token redaction in web logs | 3.2 | frontend | 1 | todo | — |
| FR-101 | First paint: theme, fonts, favicon, titles | 3.1 | frontend | 1 | todo | — |
| FR-102 | Dev loop on :3000 proxying to :5080 | 3.1 | frontend + backend config | 1 | todo | — |
| FR-103 | Standalone web image, non-root, :8080, `/healthz` | 3.2 | frontend + deploy | 1 | todo | — |
| FR-104 | Local k8s on the Next.js image | 3.3 | deploy | 3 | todo | — |
| FR-105 | Configurable forwarded-header hops | 3.4 | backend | 1 | todo | — |
| FR-106 | API entrypoint: volume ownership, drop to `$APP_UID` | 3.4 | backend + deploy | 1 | todo | — |
| FR-107 | `.railway/railway.ts` IaC | 3.5 | deploy | 4 | todo | — |
| FR-108 | `preview` + `production` environments | 3.5 | deploy | 4 | todo | — |
| FR-109 | Pre-deploy migrations | 3.5 | deploy + data | 4 | todo | — |
| FR-110 | GitHub Actions deploy workflow | 3.5 | deploy + CI | 4 | todo | — |
| FR-111 | `smoke-remote.sh` | 3.5 | deploy | 4 | todo | — |
| FR-112 | Railway operations docs | 3.5 | docs | 5 | todo | — |

v1 (FR-1–FR-52) and v2 (FR-53–FR-93) requirements stay in force. Their tests must stay green on CI.

## Phases
- [x] 0 Baseline (M0, base: e385c52): CI green on e385c52 (all 9 jobs): https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/39948638817
  - v2 Vite bundle (CI build), for the §8 "≤ 10% larger" check:
    - entry `index.js` 347.44 kB (gzip 109.95 kB)
    - `app-shell.js` 142.49 kB (gzip 42.94 kB)
    - CSS 62.35 kB (gzip 11.12 kB)
    - initial JS on `/projects` ≈ entry + app-shell ≈ 489.9 kB (gzip ≈ 152.9 kB), plus the projects route chunk
- [ ] 1 Next.js foundation (M17) ‖ backend hosting (M20): FR-94, FR-98–FR-103, FR-105, FR-106
- [ ] 2 Page port (M18): FR-95–FR-97
- [ ] 3 Local Kubernetes + CI (M19): FR-104
- [ ] 4 Railway (M21): FR-107–FR-111 (needs Railway CLI/login/tokens from Ben; production only on request)
- [ ] 5 Final (M22): FR-112, docs, audit over v1+v2+v3

## Manual checks (Ben, Docker Desktop)
- FR-106: build the API image and start it on a root-owned empty `/data`. Check that keys are written to `/data/keys`, the app runs as uid 1654 (`ps`), and `/api/health/ready` returns 200.

## Follow-ups queued
- 3000 instead of 5173: CI E2E job (`App__PublicBaseUrl`, Vite dev server step), `e2e/support/api.ts`, `scripts/perf/nfr-perf.mjs`, README, CLAUDE.md (1c/CI).
- CI: the route-tree freshness check must go. The Dockerfile build needs a read-only-root-FS check (phase 1 CI / phase 3).
- k8s `web`: `BACKEND_URL` and the `/tmp` emptyDir (phase 3).
- CI: add `backend/entrypoint.sh` to the `bash -n` and shellcheck steps (phase 1 CI pass).
- Railway `web`: `TRUST_EDGE_FORWARDED=true`, so the web server keeps the X-Forwarded-Proto/Host values Railway's edge sets. Elsewhere it overwrites them (phase-1 review).
- Railway `api`: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `ForwardedHeaders__ForwardLimit=2`. **No** `RAILWAY_RUN_UID` (the image starts as root, and the entrypoint drops to `$APP_UID`).
- `deploy/k8s/base/configmap.yaml`: the comment still mentions nginx (phase 3).

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

## Known issues
- Local Vitest at default parallelism: 15 shell/projects/components tests time out at 20 s on this machine under load, and pass with `--maxWorkers=2` or on their own. Watch for it on CI.
