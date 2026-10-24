# AIDE Platform — Specification (v3)

2026-10-23 · Ben Fielder · **v3**, built on the v2 codebase in this repo. Earlier versions: v1 [`docs/v1/SPEC.md`](docs/v1/SPEC.md), v2 [`docs/v2/SPEC.md`](docs/v2/SPEC.md). Stack: [`STACK.md`](STACK.md).

> **How this spec relates to v1 and v2.** v3 is a **platform version with no new product features**. Everything in
> `docs/v1/SPEC.md` and `docs/v2/SPEC.md` stays in force unless this document amends it. That covers FR-1 to FR-93,
> the domain rules, data model, API, UI, NFRs and decisions. Amendments are listed in §3.0. New requirements continue the
> numbering from **FR-94**. Users must see the same app, at the same URLs, with the same behaviour. Existing tests stay
> green. Where a test has to change because the framework changed (not the behaviour), it is ported, keeping its
> requirement ID in its name.

## 1. Overview

AIDE turns an idea into released software through a governed six-phase lifecycle, with versioned artifacts, approval
gates, rollback and human oversight. v1 and v2 run only on a local Docker Desktop Kubernetes cluster, with a React +
Vite single-page app served by nginx.

**v3 changes how AIDE is built and hosted, not what it does:**
- **The frontend moves to Next.js** (App Router). Pages are ported as client components over the same generated API
  client. The Next.js server replaces nginx as the single web entry point: it serves the app, proxies `/api` (including
  SignalR WebSockets) to the ASP.NET API, and sets the security headers.
- **AIDE can be deployed to Railway:** a public `web` service, a private `api` service with a data volume, and Railway
  Postgres. Infrastructure is defined as code, with `preview` and `production` environments, deploys from GitHub Actions,
  and smoke tests after every deploy.
- Local Docker Desktop Kubernetes stays, with the `web` workload switched to the Next.js image.

Success means:
- Every v1 and v2 E2E suite (lifecycle, permissions, two-version flow, import, preset, a11y) passes against the
  Next.js app, both in the dev loop and on Docker Desktop Kubernetes.
- A push to `main` that passes CI deploys to Railway `preview` and passes the smoke test. A manual workflow promotes the
  same commit to `production`, and the smoke test passes there too.
- A user signs in on the Railway production URL, creates a project with the Fake provider, and sees live updates over
  WebSockets.

**In scope (v3):**
- Next.js frontend migration with route and behaviour parity (§3.1)
- Next.js web server: `/api` proxy, security headers, logging and container (§3.2)
- Local Kubernetes on the Next.js image (§3.3)
- Backend hosting changes: forwarded headers, data volume ownership (§3.4)
- Railway: IaC, environments, migrations, CI deploys, smoke tests, operations docs (§3.5)

**Out of scope (v3):**
- New product features or API changes. `backend/openapi/api.json` must not change.
- Schema changes (no migrations expected).
- Server-rendered data fetching (Server Components reading the API). See D-V3-2.
- Horizontal scaling of the API (still one replica: background workers and the RWO volume).
- Custom domains, CDN, multi-region and external log/trace backends.
- The open v2 questions (Sign in with Vercel, abandoning a version, Supabase provisioning). They stay open.

## 2. Users and permissions

There are no changes to app roles or permissions (v1 §2, v2 §2). A new **operator** role sits outside the app.

| Role | Can | Cannot / behaviour when unauthorised |
| --- | --- | --- |
| Admin, User, project Owner/Editor/Viewer | Exactly as v1/v2 | Exactly as v1/v2 |
| Operator (not an app role: whoever has Railway and GitHub repo access) | Set Railway variables and secrets, run the production deploy workflow, set up and read backups, run `railway config apply` | Nothing in the app beyond their own app account |
| AI build agents (STACK.md autonomy `preview`) | Write IaC, run `railway config plan`, deploy to `preview` | Deploy or apply to `production` unless the user asks in the session. Delete services, volumes or databases |

Auth is unchanged: cookie auth plus API tokens (v1), GitHub App sign-in (v2).

## 3. Functional requirements

### 3.0 Amended earlier requirements (summary)

| Item | Amendment |
| --- | --- |
| v2 §7.1 "AIDE stays React + Vite + TanStack Router" | Replaced. The frontend is Next.js App Router (FR-94). The design system, tokens, components and UI rules are unchanged |
| v1 §9 / v2 §9 web container | `web` runs the Next.js standalone server instead of nginx (FR-103, FR-104). Everything nginx did moves to Next.js (FR-98–FR-100) |
| v1 §9 dev loop "Vite on 5173" | Next.js dev server on **3000**. `App:PublicBaseUrl` in Development becomes `http://localhost:3000` (FR-102) |
| v1 §8 "Rate limiting per IP" | The client IP is resolved correctly behind one or two proxies (FR-105) |
| v1 §9 / v2 §9 "Local deployment" | Joined by a Railway deployment (§3.5, §9). Docker Desktop Kubernetes stays |
| CI "Route tree is up to date" check | Removed with `routeTree.gen.ts`. Replaced by the route parity test (FR-95) |

### 3.1 Next.js migration (frontend)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-94 | `frontend/` is a Next.js App Router app (TypeScript strict, `src/app/`). Vite, `@vitejs/plugin-react` (except as Vitest's transform), TanStack Router, its plugin and `routeTree.gen.ts` are removed | frontend |
| FR-95 | Route parity: every route, dynamic segment, search param and alias in v2 §7.2 renders the same page at the same URL | frontend |
| FR-96 | Pages are client components using TanStack Query and the generated `openapi-fetch` client at same-origin `/api`. There are no behaviour, copy or layout changes | frontend |
| FR-97 | Auth redirects match v2: signed-out users on app routes go to `/login?returnUrl=<path+search>`, and signed-in users on `/login` and `/register` go to the safe `returnUrl` or `/projects` | frontend |
| FR-101 | First paint matches v2: the theme is set before paint (no flash), fonts are self-hosted, and the favicon and document titles are unchanged | frontend |
| FR-102 | Dev loop: `npm run dev` serves on `http://localhost:3000` and proxies `/api` (including WebSockets) to `http://localhost:5080` | frontend + backend config |

**FR-94 acceptance**
- Given the repo, when `package.json` is read, then `next` is a dependency and `vite` (as an app bundler),
  `@tanstack/react-router`, `@tanstack/router-plugin` and `@tanstack/eslint-plugin-router` are absent.
- Given `cd frontend && npm ci && npm run typecheck && npm run lint && npm test && npm run build`, then every step
  passes. ESLint stays typescript-eslint strict type-checked, plus `@next/eslint-plugin-next`.
- Given the feature folders `src/features/*`, `src/lib/*`, `src/components/*` and `src/mocks/*`, then they keep their
  structure. Only routing glue (`useNavigate`, `Link`, `useParams`, search hooks) is replaced, behind thin helpers in
  `src/lib/navigation.ts`.

**FR-95 acceptance**
- Given each path in v2 §7.2 (including `/projects/:id/settings/:section`, `/projects/:id/artifacts/:type`,
  `/approvals/:token`, `/admin/*` and the alias `/account/connections`), when it is opened directly in a fresh tab
  (deep link), then the same page renders as in v2.
- Given search params the v2 pages read (for example `returnUrl`, `error`, list filters, sort and page, the version
  switcher's version, and the review cycle picker), when a URL carrying them is opened, then the page applies them, and
  changing a control updates the URL the same way v2 did.
- Given an unknown path, then the v2 not-found page renders, with HTTP 404 from the server.
- A route parity test lists every v2 route pattern and fails if any has no matching `src/app` segment.

**FR-96 acceptance**
- Given any page, then it is a client component tree under a server layout that only renders the shell's static
  markup. No page fetches API data on the server.
- Given the 66 v2 unit/component test files, then each is ported (or deliberately merged), still covers the same FR IDs,
  and passes under Vitest with MSW.
- Given live updates (FR-46), then the SignalR client connects to `/api/hubs/projects` as in v2.

**FR-97 acceptance**
- Given no session, when `/projects/abc/review?cycle=2` is opened, then the browser ends at
  `/login?returnUrl=%2Fprojects%2Fabc%2Freview%3Fcycle%3D2`, and after signing in it lands on that URL.
- Given `returnUrl=https://evil.example`, then the user is sent to `/projects` (the v2 `safeReturnUrl` rule).
- The API stays the authority on auth. The Next.js server may redirect early when the `aide.auth` cookie is missing, but
  never decides permissions.

**FR-101 acceptance**
- Given system dark mode and an empty localStorage, when any page loads, then the first paint is dark, with no light
  flash (Playwright checks the `html` class before hydration).
- Given the network panel, then no request goes to a third-party origin (fonts come from `@fontsource`).

**FR-102 acceptance**
- Given the API on 5080 and `npm run dev`, when the app at `http://localhost:3000` signs in and opens a project, then
  the API calls and the SignalR WebSocket go through port 3000.
- `appsettings.Development.json` `App:PublicBaseUrl` is `http://localhost:3000`. CI's E2E job, Playwright's default
  `BASE_URL`, `scripts/perf/nfr-perf.mjs`, the README and CLAUDE.md are updated to match.

### 3.2 Next.js web server (proxy, headers, container)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-98 | The Next.js server proxies `/api/*`, including WebSocket upgrades on `/api/hubs/*`, to `BACKEND_URL`, which is read **at runtime** | frontend |
| FR-99 | The Next.js server sends the v2 nginx security headers, and a CSP with a per-request nonce | frontend |
| FR-100 | The web server's logs never contain an approval token (`/approvals/<token>`, `/api/approvals/<token>`) | frontend |
| FR-103 | The web image is the Next.js standalone server: non-root, port 8080, `/healthz`, and it works with a read-only root filesystem | frontend + deploy |

**FR-98 acceptance**
- Given one built image, when it runs with `BACKEND_URL=http://api:8080` (Kubernetes) and with
  `BACKEND_URL=http://api.railway.internal:8080` (Railway), then both proxy correctly. The upstream is not fixed at build
  time.
- **Requests pass through unchanged:** method, path, query, body (up to 10 MB, larger → 413), `Cookie`, `Authorization`,
  `X-XSRF-TOKEN`, and the upstream status, headers and `Set-Cookie` (no cookie path or domain rewriting).
- **Forwarded headers:** the proxy sets `X-Forwarded-Proto` and `X-Forwarded-Host` from the incoming request (or keeps
  the values the edge set), and appends the peer address to `X-Forwarded-For`.
- **Live updates:** given the dev loop, Kubernetes and Railway, when a project page is open, then SignalR negotiates the
  **WebSockets** transport (checked in E2E via the connection's transport), and the connection stays open for at least
  1 hour without a proxy timeout.
- Given the API is down, then `/api/*` returns 502 with a ProblemDetails body (`errorCode` `UPSTREAM_ERROR`), and pages show
  the v2 error state.
- If the installed Next.js can't proxy WebSocket upgrades from `proxy.ts` or rewrites, the web server is a minimal custom
  Node server (`server.ts`) that hands HTTP to Next and pipes upgrades for `/api/hubs/` to `BACKEND_URL`. Record which
  approach was taken in PROGRESS.md.

**FR-99 acceptance**
- Given any HTML response, then it has:
  - `Content-Security-Policy: default-src 'self'; img-src 'self' data:; connect-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'nonce-<random>' 'strict-dynamic'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'`.
    `'unsafe-eval'` is allowed only in `next dev`.
  - a nonce that differs on every request, and every inline script Next emits carries it
  - `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Frame-Options: DENY`
  - `Cache-Control: no-cache` (or stricter) on HTML
- Given `/_next/static/*`, then `Cache-Control: public, max-age=31536000, immutable`.
- Given `/api/*` responses, then the API's own headers pass through, and the web server adds `X-Content-Type-Options`.
- The E2E a11y and smoke suites assert the headers and find no CSP violations in the console.

**FR-100 acceptance**
- Given a request to `/approvals/abc123` or `/api/approvals/abc123`, then any access or error log line the web server
  writes shows `/approvals/[redacted]`, and the same applies to the `Referer` value. A unit test covers the redaction
  function with the v2 nginx regex cases.

**FR-103 acceptance**
- `next.config` has `output: 'standalone'`. The image is built in several stages on `node:24-slim`, runs `node server.js`
  (or the custom server) as a non-root user, and listens on `PORT` (default 8080).
- `GET /healthz` → 200 `ok`, without calling the API.
- Given `readOnlyRootFilesystem: true` with `/tmp` writable, then the server starts and serves pages.
- `frontend/nginx.conf` is deleted.

### 3.3 Local Kubernetes

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-104 | Docker Desktop Kubernetes runs the Next.js `web` image with `BACKEND_URL=http://api:8080`. Deploy, smoke and E2E keep working | deploy |

**FR-104 acceptance**
- `deploy/k8s/base/web.yaml`:
  - probes `/healthz`
  - gets `BACKEND_URL` from `app-config`
  - keeps the LoadBalancer on `localhost:8080`, the non-root user, the read-only root filesystem and the `/tmp` emptyDir
  - sets resource limits suitable for Node (at least 256 Mi)
- **Agent-verified, on CI** (CLAUDE.md "Commit gate"): the web image builds, `validate-manifests.sh` renders both
  overlays, `smoke.sh` is updated to check `/healthz` and the FR-99 headers, and the Chromium E2E suites pass.
- **Manual check for Ben** (listed in PROGRESS.md, not part of the agent gate):
  - `./deploy/scripts/deploy-local.sh` and `--e2e` succeed, and `smoke.sh` passes
  - `BASE_URL=http://localhost:8080 npm run test:e2e` passes in Chromium, Firefox and WebKit

### 3.4 Backend hosting changes

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-105 | Real client IP behind configurable proxy hops: `ForwardedHeaders:ForwardLimit` (default 1) and optional `ForwardedHeaders:KnownNetworks` | backend |
| FR-106 | The API container starts on a root-owned data volume and still runs as `$APP_UID` | backend + deploy |

**FR-105 acceptance**
- Given `ForwardLimit=2` and `X-Forwarded-For: 203.0.113.9, 10.0.0.5` from the Next.js proxy (edge → Next.js → API),
  then the client IP used by the login and register rate limits is `203.0.113.9`.
- Given `ForwardLimit=1` (Kubernetes: browser → Next.js → API), then it is the right-most entry, as in v2.
- Given a client-supplied `X-Forwarded-For: 1.2.3.4` sent through Railway's edge with `ForwardLimit=2`, then the
  resolved IP is the address the edge appended, not `1.2.3.4`.
- `X-Forwarded-Proto: https` makes `Request.IsHttps` true, so `SameAsRequest` cookies get `Secure`.
- `X-Forwarded-Host` reaches the Live origin check, so same-origin WebSockets are accepted (FR-46 tests extended).
- Integration tests cover the three cases above.

**FR-106 acceptance**
- The API image's entrypoint (`/app/entrypoint.sh`) works as follows:
  - if started as root: creates `/data/keys` and `/data/workspaces`, `chown`s them to `$APP_UID`, then `exec`s the app
    as `$APP_UID` with `setpriv`
  - if started non-root (Kubernetes `runAsNonRoot`): `exec`s the app directly
  - the migration bundle (`/app/efbundle`) is not affected
- Given a root-owned empty volume at `/data`, then the API starts, writes Data Protection keys, and
  `/api/health/ready` is 200.
- Kubernetes manifests stay unchanged (`runAsNonRoot: true`, fsGroup).

### 3.5 Railway deployment

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-107 | `.railway/railway.ts` defines the project: services `web` and `api`, database `postgres`, volumes, variables, healthchecks, watch paths | deploy |
| FR-108 | Two Railway environments, `preview` and `production`, each with its own Postgres and `/data` volume | deploy |
| FR-109 | Migrations run as the API's pre-deploy command. A failed migration stops the deploy, and the previous release keeps serving | deploy + data |
| FR-110 | GitHub Actions deploy: `preview` automatically after CI passes on `main`, `production` by manual dispatch | deploy + CI |
| FR-111 | `deploy/scripts/smoke-remote.sh <base-url>` checks a deployed environment, and runs after every Railway deploy | deploy |
| FR-112 | Operations docs: variable inventory, first-time setup, first Admin, backups, rollback | docs |

**FR-107 acceptance**
- **`web`:**
  - built from `frontend/Dockerfile` (root `frontend`), watch paths `frontend/**`
  - public domain `*.up.railway.app`
  - healthcheck `/healthz`
  - `BACKEND_URL=http://${{api.RAILWAY_PRIVATE_DOMAIN}}:8080`
  - one replica
- **`api`:**
  - built from `backend/Dockerfile` (root `backend`), watch paths `backend/**`
  - **no public domain**
  - healthcheck `/api/health/ready`
  - one replica
  - volume mounted at `/data` (5 GB target; 500 MB, the current Railway plan's maximum, until the plan is upgraded (Q2) *(build decision 2026-10-24)*)
  - pre-deploy command from FR-109
  - variables per §9
- **`postgres`:** Railway Postgres **18** (`postgres-ssl:18`, pinned with `database()`), with backups on in `production`. Railway provisions databases from its template default (18), and pinning 17 broke the data directory, so Railway runs 18 while local Kubernetes, compose, CI and Testcontainers stay on 17 (D-V3-13) *(build decision 2026-10-24, Ben)*.
- **Connection string:** built from references:
  `ConnectionStrings__Aide=Host=${{postgres.PGHOST}};Port=${{postgres.PGPORT}};Database=${{postgres.PGDATABASE}};Username=${{postgres.PGUSER}};Password=${{postgres.PGPASSWORD}}`
  (private network).
- No secret values appear in `.railway/railway.ts` or anywhere else in git. Secrets are set in Railway (§9).
- `railway config plan` against an applied environment reports no changes. CI runs `railway config plan` (read-only)
  when `RAILWAY_TOKEN` is available.
- Field names are checked against Railway's IaC reference at build time. If IaC can't express a setting, it is listed in
  `deploy/railway/README.md` as a one-time dashboard step.

**FR-108 acceptance**
- `preview` and `production` have separate Postgres instances, volumes, `Approvals__SigningKey` and Data Protection keys.
- Both run `ASPNETCORE_ENVIRONMENT=Production` and `Seed__Enabled=false`, with no Fake providers (`Deployments__AllowFake`
  unset).
- `App__PublicBaseUrl` is each environment's own web URL.

**FR-109 acceptance**
- The API's pre-deploy command is `/bin/sh -c 'exec /app/efbundle --connection "$ConnectionStrings__Aide"'`. Railway runs it in exec form, so the shell is what expands the variable *(build decision 2026-10-23)*.
- Given a pending migration, then it is applied before the new API container starts.
- Given a migration that fails, then the deploy is marked failed, the previous deployment keeps serving, and
  `/api/health/ready` on it stays 200.

**FR-110 acceptance**
- `.github/workflows/deploy.yml`:
  - `deploy-preview` runs on `workflow_run` of CI succeeding on `main`. It runs `railway up` for `api`, then `web`, to
    environment `preview` (`--ci`), waiting after each upload until Railway reports the new deployment SUCCESS (api, including its pre-deploy migration, before web). It then checks that `api` has no public domain or TCP proxy and runs FR-111 against the preview URL *(build decision 2026-10-24)*.
  - `deploy-production` runs on `workflow_dispatch` with a commit SHA (default: the latest green `main`). It uses GitHub
    environment `production` (required reviewer: the repo owner), the same steps, and the production URL.
- `RAILWAY_TOKEN` is a project token per environment, stored as GitHub environment secrets `preview` and `production`.
- `concurrency` stops two deploys to the same environment from overlapping.
- Permissions are least-privilege (`contents: read`).
- A failed smoke test fails the workflow, which shows the rollback instructions from FR-112.

**FR-111 acceptance**
- **Read-only checks (always):**
  - `GET /healthz` → 200
  - `GET /api/health/ready` (through `web`) → 200
  - `GET /login` → 200 with the FR-99 headers
  - `GET /api/auth/me` without a cookie → 401 ProblemDetails
  - the API is unreachable at a public URL (no `api` domain configured). In CI, `deploy.yml` checks this by listing the `api` service's domains and TCP proxies (read-only). For manual runs the script checks it only when `API_PUBLIC_URL` is set *(build decision 2026-10-24)*
- **Sign-in round trip (only when `SMOKE_EMAIL` and `SMOKE_PASSWORD` are set):** sign in, `GET /api/projects` → 200,
  open a SignalR WebSocket to `/api/hubs/projects` → connected, sign out.
- The script never registers accounts (FR-3: the first account becomes Admin).
- Retries for up to 3 minutes, then exits non-zero with the failing check named.

**FR-112 acceptance**
- `deploy/railway/README.md` covers:
  - **Prerequisites:** Railway account, CLI login, project tokens.
  - **First-time setup:** `railway config apply` per environment; setting secrets; generating the domain; setting
    `App__PublicBaseUrl`.
  - **First Admin:** register on a new environment straight after its first deploy, before sharing the URL.
  - **The smoke account:** create a normal user for `SMOKE_EMAIL`.
  - **GitHub App and OAuth:** registration against the Railway URL.
  - **Backups:** Postgres backups plus the `/data` volume. Losing `/data/keys` makes the stored credentials unreadable.
  - **Rollback:** redeploy the previous deployment in Railway. Migrations are forward-only, so roll back only to a
    deployment with the same schema, or restore a backup.
  - **Costs and limits** to watch.
- `deploy/railway/variables.md` lists every variable per service, its source (reference, secret or literal), and
  whether it is required. The README's configuration reference links to it.

## 4. Domain rules

There are no new domain rules. §4 of v1 and v2 is unchanged. Pure helpers added in v3 are unit-tested with these worked
examples.

**Approval-token redaction (FR-100):**

| Input path or Referer | Logged as |
| --- | --- |
| `/approvals/abc123` | `/approvals/[redacted]` |
| `/api/approvals/abc123?x=1` | `/api/approvals/[redacted]?x=1` |
| `https://aide.example/approvals/abc123#top` | `https://aide.example/approvals/[redacted]#top` |
| `/approvals/` | `/approvals/` (unchanged) |
| `/projects/1` | `/projects/1` |

**Forwarded client IP (FR-105):** see the acceptance criteria. The right-most `ForwardLimit` entries are trusted.

**Safe return URL (v2, kept):** `/projects/1?x=2` → kept. `//evil.example`, `https://evil.example` and `javascript:…` →
`/projects`.

## 5. Data

No changes. No migrations are expected. If one turns out to be needed, it follows CLAUDE.md rule 5 and gets an
amendment here.

## 6. API

There are no changes to endpoints, schemas or error codes. `backend/openapi/api.json` and
`frontend/src/api/generated/` must be byte-identical to v2 (CI's contract check). New HTTP surface lives only on the web
server and is not part of the API contract:

| Method | Route | Response | Notes |
| --- | --- | --- | --- |
| GET | /healthz | 200 `ok` (text/plain) | Web server liveness. Doesn't call the API |
| any | /api/* | proxied | FR-98. 502 `UPSTREAM_ERROR` ProblemDetails when the API is unreachable |

### Error codes
No new codes. The proxy's upstream failure uses the existing `UPSTREAM_ERROR → 502` (v2 §6), with the copy the frontend already maps for that code.

## 7. Pages and UI

Every v2 §7.2 route, dialog and state is unchanged (FR-95, FR-96). The v2 §7.1 design system applies, with Next.js
mapping:
- `src/app/layout.tsx`: html/body, the theme-init script (with nonce), fonts and global CSS. A client `Providers`
  component holds QueryClient, the theme, toasts and live updates.
- `(public)/login`, `(public)/register`: the v2 `_public` layout.
- `(app)/…`: the v2 `_app` layout and AppShell. All workspace and project routes sit under it.
- `not-found.tsx` per layout, `error.tsx` per segment group (the v2 error state), and `loading.tsx` using the v2
  skeletons.

Devices and accessibility are unchanged: desktop-first, usable at 375 px, WCAG 2.2 AA, axe in E2E.

## 8. Non-functional requirements

v1 §8 and v2 §8 apply, plus:
- **Performance:**
  - The Railway production project list p95 is < 500 ms end to end from the same region (the v2 300 ms API target is
    still measured locally).
  - Initial JS on `/projects` is no more than 10% larger than the v2 Vite build (measure both in PROGRESS.md).
- **Security:**
  - FR-99 headers.
  - `Auth__Cookie__SecurePolicy=Always` on Railway.
  - The API has no public domain.
  - Secrets only in Railway variables or GitHub environment secrets.
  - `BACKEND_URL` is server-only (never `NEXT_PUBLIC_*`).
  - Dependabot covers the `next` package.
- **Reliability:**
  - One API replica, restart policy on failure.
  - Background jobs resume after redeploys (v1 leases).
  - The web service can restart independently. Browsers reconnect to SignalR (the v1 reconnect behaviour).
- **Observability:**
  - Structured JSON logs from the API (unchanged) and one JSON line per request from the web server (method, redacted
    path, status, duration). Both are visible in Railway logs.
- **Testing:**
  - The full v1+v2 E2E suites run in CI against the Next.js dev loop (or `next start`).
  - On Kubernetes, all three browsers run as a manual check (FR-104). The agent gate is GitHub Actions CI, not local
    runs (CLAUDE.md "Commit gate").
  - The FR-111 smoke test runs on each Railway deploy.
  - No test calls Railway, GitHub, Vercel or LLM APIs, except the deploy workflow itself.

## 9. Environments

| Environment | Where | Web URL | Notes |
| --- | --- | --- | --- |
| Dev loop | your machine | http://localhost:3000 | API 5080, compose DB 5432, Seed on |
| Local Kubernetes | Docker Desktop (`docker-desktop` context) | http://localhost:8080 | `deploy-local.sh` (`--e2e` for Fake mode). Seed on |
| Railway `preview` | Railway | generated `*.up.railway.app` | Production config, own DB and volume. Auto-deploys from green `main` |
| Railway `production` | Railway | generated `*.up.railway.app` | Manual promote. Backups on |

**Railway `api` variables** (full list in `deploy/railway/variables.md`):
- **Literals:**
  - `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_HTTP_PORTS=8080`
  - `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `ForwardedHeaders__ForwardLimit=2`
  - `DataProtection__KeysPath=/data/keys`, `DataProtection__ApplicationName=aide`
  - `Auth__Cookie__SecurePolicy=Always`
  - `Seed__Enabled=false`, `Telemetry__ConsoleExporter=false`
- **References:** `ConnectionStrings__Aide` (FR-107), `App__PublicBaseUrl=https://${{web.RAILWAY_PUBLIC_DOMAIN}}`.
- **Secrets:** `Approvals__SigningKey` (64 random characters). Optional: `Llm__*`, `GitHub__*`, `Smtp__*`.

**Railway `web` variables:** `BACKEND_URL`, `PORT=8080`, `NODE_ENV=production`, `TRUST_EDGE_FORWARDED=true` (keep the edge's X-Forwarded-Proto/Host; elsewhere the web server overwrites them) *(build decision 2026-10-23)*.

**External services:** unchanged (LLM providers, GitHub, Vercel and SMTP, all optional). On Railway, Ollama needs an
explicit `Llm__Providers__Ollama__BaseUrl`.

## 10. Decisions and assumptions

v1 D1–D21 and v2 D-V2-1 to D-V2-15 stand.

| # | Decision / assumption | Source | Status |
| --- | --- | --- | --- |
| D-V3-1 | Frontend moves to Next.js App Router. It is the UI for the separate ASP.NET API, which stays the only backend (no Next.js route handlers or server actions for business logic) | Ben, compose-stack 2026-10-23 | agreed |
| D-V3-2 | Pages are ported as client components with TanStack Query. No server-side data fetching in v3. Why: no-feature version, lowest risk, tests carry over | Ben, spec interview 2026-10-23 | agreed |
| D-V3-3 | Same-origin model: the Next.js server proxies `/api` (including WebSockets). No CORS, and cookie auth is unchanged | Ben, compose-stack 2026-10-23 | agreed |
| D-V3-4 | Every v2 URL is kept identical. No route tidy-ups | Ben, spec interview 2026-10-23 | agreed |
| D-V3-5 | Next.js replaces nginx as the web container in both Kubernetes and Railway, so there is one web image. Old: nginx serving the Vite build | build | agreed |
| D-V3-6 | Railway with IaC (`.railway/railway.ts`), not the deprecated `railway.json` | deploy-railway pack | agreed |
| D-V3-7 | Deploys go through GitHub Actions + Railway CLI: `preview` after green CI on `main`, `production` by manual dispatch with a GitHub environment reviewer | Ben, spec interview 2026-10-23 | agreed |
| D-V3-8 | `preview` is production-like (real providers, Seed off, its own DB). Smoke-tested read-only, plus a sign-in round trip with a pre-made smoke account | Ben, spec interview 2026-10-23 | agreed |
| D-V3-9 | `ConnectionStrings__Aide` is built from Railway Postgres reference variables. The API does not parse `DATABASE_URL`, so the migration bundle and the app share one variable | build | assumed |
| D-V3-10 | The API keeps running as non-root on Railway through an entrypoint that fixes volume ownership and drops privileges, not `RAILWAY_RUN_UID=0` | build | assumed |
| D-V3-11 | Local Docker Desktop Kubernetes stays the full local deployment. CLAUDE.md rule 7 still applies to `deploy/scripts/*`. Railway deploys come only from the deploy workflow, or from agents within STACK.md autonomy | Ben, compose-stack 2026-10-23 | agreed |
| D-V3-12 | The `aide` CLI (FR-52) is unaffected. On Railway it targets the web URL (`/api` is proxied) | build | assumed |
| D-V3-13 | Railway runs Postgres 18 (`postgres-ssl:18`). Local Kubernetes, compose, CI and Testcontainers stay on 17. Why: Railway provisions databases from its template default (18) and applies a pinned image only afterwards, and Postgres 17 can't open an 18 data directory. EF Core 10 / Npgsql 10 support 18, and the migrations are plain SQL | Ben, 2026-10-24 | agreed |

## 11. Open questions

| # | Question | Who can answer | Blocks |
| --- | --- | --- | --- |
| Q1 | On a public deploy, should the first-registered-user-is-Admin rule (FR-3) be guarded, for example by `Auth:BootstrapAdminEmail` or by disabling registration after the first user? Proposed for v3: no code change. Operators register first (FR-112). Revisit if the URL is shared widely | Ben | nothing (docs only in v3) |
| Q2 | Railway plan, region and volume size for production (cost) | Ben | FR-107 values only |
| Q3 | A custom domain for production? (Would change `App__PublicBaseUrl` and GitHub App URLs) | Ben | nothing in v3 |
| Q4 | Build-time checks against current vendor docs (not questions for Ben): Railway IaC field names (root dir, Dockerfile path, pre-deploy, volumes, watch paths); Railway private networking IPv4/IPv6 and Kestrel binding; whether Next.js `proxy.ts`/rewrites proxy WebSocket upgrades in standalone mode (else the FR-98 custom server); `X-Forwarded-For` behaviour of Railway's edge | build agents | FR-98, FR-105, FR-107 |

## 12. Milestones

**M0 Baseline:**
- The repo, CI, deploy scripts and the v2 app already exist. **Do not re-scaffold.**
- Branch `build/aide-v3` from `main` and push it. The gate runs on GitHub Actions, not locally (CLAUDE.md
  "Commit gate").
- Confirm CI is green on the baseline commit. Record the run URL and the baseline (including the v2 Vite bundle size for
  §8, taken from CI's frontend build output) in a new root `PROGRESS.md`.

1. **M17 Next.js foundation:**
   - Next.js app shell in `frontend/`: layout, providers, theme first paint, fonts, global CSS.
   - The `/api` proxy, including WebSockets, with runtime `BACKEND_URL`.
   - CSP nonce headers, log redaction, `/healthz`, the standalone Dockerfile and the dev loop on 3000.
   - Vitest config without the Vite app config.
   - FR-94, FR-98–FR-103.
2. **M18 Page port:**
   - Every route group ported as client components, with navigation helpers, search params, auth redirects and
     not-found/error/loading.
   - The route parity test, all unit tests ported, and the v1+v2 E2E suites green on CI (dev loop).
   - FR-95–FR-97. Depends on M17.
3. **M19 Local Kubernetes:**
   - Next.js `web` workload, nginx removed, smoke headers and both overlays, verified on CI.
   - The Kubernetes deploy and three-browser E2E are manual checks for Ben.
   - The CI image and E2E jobs updated, and the route-tree check removed.
   - FR-104. Depends on M18.
4. **M20 Backend hosting:**
   - Configurable forwarded headers and the API entrypoint with volume ownership, with integration tests.
   - FR-105, FR-106. Independent; can run alongside M17–M19.
5. **M21 Railway:**
   - `.railway/railway.ts`, the environments, the pre-deploy migration, `smoke-remote.sh` and `deploy.yml`.
   - The first deploy to `preview` (agent autonomy `preview`). Production only when Ben asks.
   - FR-107–FR-111. Depends on M19 and M20.
6. **M22 Integrate & harden:**
   - Operations docs (FR-112), the README and CLAUDE.md (Stack, Layout, Commands, Versions), and STACK.md sync.
   - Perf and bundle measurements, the axe pass and the Dependabot `next` group.
   - The spec-verifier over **v1, v2 and v3 requirements**.

## Revision notes

- 2026-10-23: v3 spec created. The v2 spec and build log moved to `docs/v2/`. The stack was recorded in `STACK.md`
  (compose-stack). Decisions D-V3-1 to D-V3-4, D-V3-7, D-V3-8 and D-V3-11 were confirmed with Ben in the compose-stack
  and spec interviews.
