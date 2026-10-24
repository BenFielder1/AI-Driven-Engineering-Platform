# AIDE on Railway (FR-107 to FR-112)

One Railway project, `aide`, with two environments: `preview` and `production`. Each environment has:

- **`web`:** the Next.js server from `frontend/Dockerfile`, with a public `*.up.railway.app` domain and healthcheck
  `/healthz`
- **`api`:** the ASP.NET API from `backend/Dockerfile`, private only, with a 500 MB volume at `/data` (5 GB after a plan upgrade), migrations as its
  pre-deploy command, and healthcheck `/api/health/ready`
- **`postgres`:** Railway Postgres 17

The browser only talks to `web`, which proxies `/api` and the SignalR WebSockets to
`http://api.railway.internal:8080`.

| What | Where |
| --- | --- |
| Infrastructure as Code | [`.railway/railway.ts`](../../.railway/railway.ts) (`railway config plan` / `apply`) |
| Every variable | [`variables.md`](variables.md) |
| Smoke test | [`deploy/scripts/smoke-remote.sh`](../scripts/smoke-remote.sh) `<web-url>` |
| Deploys | `.github/workflows/deploy.yml`: `preview` after green CI on `main`, `production` by manual dispatch with approval (FR-110) |

Agents follow STACK.md autonomy (`preview`). They can run `railway config plan` and deploy to `preview`. Production
deploys and `config apply` against production happen only on Ben's explicit request. Nobody runs
`--confirm-destructive` without reviewing the exact destroys.

## Prerequisites

1. **A Railway account on the Hobby plan or higher.** The Free plan caps volumes at 0.5 GB and keeps old images for
   rollback for only 24 h.
2. **Railway CLI 5.49.1 or newer.** IaC needs at least 5.42.1, and 5.49.1 fixes pre-deploy handling. Install it with
   `npm i -g @railway/cli` (or see Railway's install docs), then check it with `railway --version`.
3. **`railway login`** opens a browser. Use `railway login --browserless` on a headless machine.
4. **Node 24 and the IaC package:** run `npm ci --prefix .railway`. The CLI evaluates `.railway/railway.ts` with Node
   and the `railway` package pinned in `.railway/package.json`. To type-check it, run
   `npm --prefix .railway run typecheck`.
5. **One project token per environment** (dashboard: Project → Settings → Tokens → choose the environment). Store each
   one as the `RAILWAY_TOKEN` secret of the matching GitHub environment (`preview`, `production`). A project token
   can deploy and plan only in its own environment. Creating environments needs your login (or an account token),
   not a project token.

## First-time setup

Run these from the repo root. In each environment, finish all the steps before its first deploy.

### 1. Project and environments (FR-108)

```bash
railway init --name aide            # creates the project (or create it in the dashboard), links this folder
railway environment new preview     # empty environment; don't --duplicate production (that would copy its variables)
railway environment link production # Railway creates `production` with every new project
```

Each environment has its own Postgres, `/data` volume, `Approvals__SigningKey` and Data Protection key ring, because
every resource and variable is per environment. Both run the Production configuration (`ASPNETCORE_ENVIRONMENT=Production`,
`Seed__Enabled=false`, no Fake providers). Don't enable PR environments: they would fork `preview` with its secrets.

### 2. Apply the IaC to each environment

```bash
npm ci --prefix .railway
railway environment link preview
railway config plan                 # expect: create postgres, volume api-data, services api and web
railway config apply                # review the plan it shows, then confirm
railway environment link production
railway config plan && railway config apply
```

Run these from `.railway/`: `railway link` is stored per directory, and that's where the IaC lives.

**Windows (npm-installed CLI).** `railway/iac` 3.16 checks the CLI version by running `process.env._` (or plain `railway`)
with `execFileSync`. On Windows that only finds the npm shims (`railway.cmd`/`.ps1`), which it can't run, so the plan
fails with "requires Railway CLI 5.42.1 or newer" even on 5.71. In PowerShell, point `_` at the real binary first:

```powershell
$env:_ = "$env:APPDATA\npm\node_modules\@railway\cli\bin\railway.exe"
railway config plan
```

Git Bash can't do this: bash sets `_` to each command's own path. Use PowerShell. Linux and macOS (including CI) are unaffected.

**What the first apply on preview taught us (2026-10-24).** The early applies failed with
`Apply finished with status failed; failed changes: change ×N`. The human output lists every change as failed, because one
rejected change fails the whole batch. The cause was the `api-data` volume: 5 GB exceeds the current plan's 500 MB maximum.
Only `railway config apply --json --yes` shows it, under `applyResult.diagnostics` ("Max size of 500 MB on current plan").
To diagnose a failed apply, run that command and read the diagnostics. `railway.ts` now holds settings that keep the
plan drift-free after apply:

- **Volume:** `sizeMB: 500` and `region: 'iad'`. Railway assigns a region, and leaving it unset plans a destructive change.
- **Services:** `source: { type: 'github', rootDirectory }` with no repo. That is how Railway records a repo-less service
  with a root directory. Nothing is connected to GitHub.
- **Build:** `dockerfilePath: 'Dockerfile'` next to `builder: 'DOCKERFILE'`. Without it Railway kept RAILPACK.
- **Restart policy:** omitted. Railway's default (ON_FAILURE, 10 retries) is stored as null, so declaring it drifts forever.

Plan and apply use the linked environment. In CI they use the environment of `RAILWAY_TOKEN`. `railway.ts` doesn't
branch on the environment, so both environments get the same configuration. After the first apply, `railway config plan`
should report no changes (FR-107). CI checks that with `railway config plan --detailed-exit-code` before every deploy,
where exit 2 means drift and stops the deploy.

**Operator-set variables and `variables.ignore`.** IaC owns every variable on a service, so a variable that exists in
Railway but not in `railway.ts` is planned for **deletion**, which would also make CI's drift check fail. To keep one
file for both environments, `railway.ts` lists `Approvals__SigningKey` and the **full optional set** from
[variables.md](variables.md) (`Llm__*`, `GitHub__App__*`, `GitHub__OAuth__*`, `Smtp__*`, 19 names) in
`operatorSetApiVariables`, which is the project-level `variables.ignore`. IaC never touches an ignored name, whether it
is set or not, so setting any of them in either environment needs no code change. An ignored name may be set or unset,
which is why it is used instead of `preserve()`. `variables.ignore` is a CLI 5.71 feature that the `railway` 3.16 package doesn't type yet, hence a cast in `railway.ts`.

While a name is unset, `railway config plan` prints one warning per name:
`variables.ignore pattern "<name>" matches no variable`. That is expected and harmless. A variable set in Railway that is
neither declared in `railway.ts` nor in the ignore list is still planned for deletion.

**First-plan checklist** (each environment, before the first apply):

- [ ] The plan creates `postgres`, volume `api-data`, and services `api` and `web`, and destroys nothing.
- [ ] The plan shows no add, change or error for the ignored `Approvals__SigningKey`, `Llm__*`, `GitHub__*` and `Smtp__*`
      names. Only the "matches no variable" warnings for names you haven't set are expected.
- [ ] `api` shows the pre-deploy command, `preDeployTimeoutSeconds: 600`, healthcheck `/api/health/ready`, and no
      domain or TCP proxy.
- [ ] After the apply and the step 3 secrets, `railway config plan --detailed-exit-code` exits 0.
- [ ] On the first deploy run, the "api has no public domain or TCP proxy" step passes. With none, the CLI may print
      `No domains found for service …` / `No TCP proxies found for service …` instead of JSON, which the step treats as
      empty. Any other non-JSON output or error fails the step: check the CLI output format if that happens.

If the apply still fails on these names, `railway.ts` can instead use per-environment lists:
`export default defineRailway((ctx) => …)`, spreading only the names that environment actually uses. Adding a secret to
one environment is then: set the value in Railway, add its name to that environment's list, and plan (no destroys, no
changes).

### 3. One-time dashboard or CLI steps (not expressible in IaC)

Do these per environment, with that environment linked:

| Step | How |
| --- | --- |
| **Generate the web domain** | `railway domain --service web --port 8080` (or `web` → Settings → Networking → Generate Domain, port 8080). Note the URL. Generated domains are not part of IaC |
| **No api domain** | Check `api` → Settings → Networking shows no public domain and no TCP proxy. Never generate one. Every deploy fails if one exists (FR-111) |
| **Signing key** | `openssl rand -hex 32 \| railway variable set Approvals__SigningKey --stdin --service api --environment <env> --skip-deploys` (64 characters, read from stdin so it never appears in argv or shell history). Use a new value for each environment. Don't echo it anywhere else |
| **Optional secrets** | LLM, GitHub App/OAuth and SMTP values: see [variables.md](variables.md). Set them the same way (`--stdin` for secrets). Their names are already in `operatorSetApiVariables` (`variables.ignore`) in `railway.ts`. A name not in that list must be added there first, or the next plan deletes it |
| **Postgres backups** | `postgres` → Backups → schedule **Daily + Weekly** in `production`. Optional in `preview` |
| **Volume backups** | `api` → Backups (the `api-data` volume) → **Daily + Weekly** in `production`. See [Backups](#backups) |
| **Volume size** | 500 MB, the current plan's maximum. SPEC Q2 wants 5 GB: upgrade the plan, raise `sizeMB` in `railway.ts` and apply. Shrinking is destructive |
| **Source** | Don't connect the GitHub repo to `web` or `api`. Deploys come from GitHub Actions with `railway up` (D-V3-7). `railway.ts` declares `source` with a root directory and no repo, so nothing is connected. The watch paths in `railway.ts` (`/frontend/**`, `/backend/**`) only matter if a repo is connected later |

`App__PublicBaseUrl` is the reference `https://${{web.RAILWAY_PUBLIC_DOMAIN}}`, so it picks up the generated domain with
no extra step. The API validates it at start-up, so generate the domain before the first deploy. If you add a custom
domain later (SPEC Q3), put it in `domains` for `web` in `railway.ts`, and set `App__PublicBaseUrl` to it.

### 4. First deploy

Normally the deploy workflow does this. By hand (preview only, unless Ben asks for production):

```bash
railway up --service api --environment preview --ci   # from the repo root: Railway builds from the service root dir
railway deployment list --service api --environment preview   # repeat until the new deployment is SUCCESS
railway up --service web --environment preview --ci
railway deployment list --service web --environment preview   # until SUCCESS
deploy/scripts/smoke-remote.sh https://<web-domain>
```

`railway up --ci` streams the build log and exits once the image is built. It doesn't wait for the pre-deploy
migration or the healthcheck, so wait for `SUCCESS` (or `FAILED`/`CRASHED`) before the next step. Deploy `api` first. Its pre-deploy command runs the migration bundle
(`/bin/sh -c 'exec /app/efbundle --connection "$ConnectionStrings__Aide"'`). The shell is needed because Railway runs
Dockerfile commands in exec form, which doesn't expand variables.

Check that the build log shows the `backend` (or `frontend`) Dockerfile. If Railway can't find a Dockerfile, the upload
didn't honour the root directory. In that case, deploy with `railway up backend --path-as-root --service api` (and
`frontend` for `web`) and remove `rootDirectory` from `railway.ts`.

### 5. First Admin: straight after the first deploy (FR-3)

The **first account registered becomes Admin**, and the URL is public. Seeding is off on Railway, so there is no
`admin@aide.local`. Immediately after an environment's first successful deploy, before the URL is shared or the smoke
account is created:

1. Open `https://<web-domain>/register` and create your admin account.
2. Check Admin is reachable in the menu.

If someone else got there first, they are Admin and can't be demoted by a non-admin. Treat the environment as
compromised: before anything else is stored there, decide with the project owner whether to reset its database (this
deletes its data), then register again. To keep a later mistake recoverable, give a second trusted person Admin
(Admin → Users → role) once they have registered.

### 6. The smoke account

The smoke test signs in with a **normal (non-admin) user** and never registers one. After the first Admin exists, per
environment:

1. Register `smoke+preview@<your-domain>` (and `smoke+production@…`) with a long random password, at `/register`.
2. Store them as the GitHub environment secrets `SMOKE_EMAIL` and `SMOKE_PASSWORD` (environments `preview` and
   `production`).
3. Store the web URL as the GitHub environment variable `RAILWAY_WEB_URL`.

The smoke test runs `GET /api/projects` and opens the live-updates WebSocket as this user. It changes nothing.

### 7. GitHub App and OAuth against the Railway URL

URLs are per environment, so register a separate GitHub App (or OAuth App) for `preview` and for `production`.

- **AIDE GitHub App (recommended):** sign in as Admin, then go to Admin → Integrations → GitHub → Register. The manifest
  uses `App__PublicBaseUrl`, so its callback and setup URLs point at this environment's domain. The App's keys are
  stored encrypted with this environment's Data Protection keys.
- **OAuth App (v1 sign-in, only while no GitHub App exists):**
  - Homepage URL: `https://<web-domain>`
  - Callback URL: `https://<web-domain>/api/auth/github/callback`
  - Set `GitHub__OAuth__ClientId` and the `GitHub__OAuth__ClientSecret` secret in Railway for that environment (both
    are in `operatorSetApiVariables` in `railway.ts`).
- **A custom domain later:** re-register the App, or edit its URLs, after changing `App__PublicBaseUrl`.

## Deploys and the smoke test

`.github/workflows/deploy.yml` (FR-110) runs these steps for each environment, using that environment's project token:

1. `railway config plan --detailed-exit-code`: any drift stops the deploy (CI never applies).
2. `railway up --service api --environment <env> --ci`, then poll `railway deployment list --service api --json` for
   the deployment that upload created until it is `SUCCESS` (pre-deploy migration and `/api/health/ready` passed).
   `FAILED`, `CRASHED`, `REMOVED` or `SKIPPED`, or 15 minutes without success, fails the run and names a failed
   migration as the likely cause.
3. The same for `web` (healthcheck `/healthz`, 10 minutes), only after `api` is live.
4. `railway domain list` and `railway tcp-proxy list` for `api` (read-only): any domain or TCP proxy fails the run
   (FR-111, the API must not be public).
5. `deploy/scripts/smoke-remote.sh "$RAILWAY_WEB_URL"`

`railway up --ci` itself only streams the build log and exits once the image is built, so step 2's wait is what
catches a failed migration or healthcheck. When a deployment fails, the previous deployment of that service keeps
serving (FR-109). After a timeout, the deployment may still go live later: check Railway before re-running.

A `workflow_run` for a commit older than the newest main commit with green CI is skipped with a notice, so CI runs that
finish out of order never roll `preview` back.

Smoke test by hand:

```bash
deploy/scripts/smoke-remote.sh https://<web-domain>                                   # read-only checks
SMOKE_EMAIL=… SMOKE_PASSWORD=… deploy/scripts/smoke-remote.sh https://<web-domain>    # + sign-in round trip
API_PUBLIC_URL=https://<suspected-api-url> deploy/scripts/smoke-remote.sh https://<web-domain>  # assert not served
```

It retries for up to 3 minutes (`SMOKE_TIMEOUT`), prints PASS, FAIL or SKIP per check, and exits 1 naming the failed
checks. A wrong smoke password fails at once (no retries, so no lockout).

## Backups

Two things hold state, and you need **both** to recover:

- **Postgres:** all application data. Turn on the Daily and Weekly schedules (`postgres` → Backups). Daily backups are
  kept for 6 days and weekly ones for 27. For an off-platform copy, use
  `pg_dump "$DATABASE_PUBLIC_URL"` through a temporary TCP proxy. Remove the proxy afterwards.
- **The `api-data` volume (`/data`):**
  - `/data/keys` is the **Data Protection key ring**. It encrypts the secrets stored in the database: the registered
    GitHub App's client secret and private key, users' LLM keys, and Vercel and GitHub tokens. **If you lose
    `/data/keys`, those values can't be decrypted**, even from a perfect database backup. Users must re-enter their
    keys, and the GitHub App must be re-registered.
  - `/data/workspaces` holds Git working copies, which can be cloned again.

  Turn on the Daily and Weekly schedules on `api`'s volume (`api` → Backups).

Restore Postgres and `/data` from the **same point in time**. Restoring a volume backup in Railway mounts a new volume
and keeps the old one unmounted; click **Deploy** to finish. Never delete the old volume until the app is verified.

## Rollback

Migrations are **forward-only**. The pre-deploy bundle applies new migrations and never reverts them.

1. **Bad release, same schema** (the usual case): in `web` or `api` → Deployments, open a previous successful deployment →
   **Rollback**. Railway reuses the old image and its variables, with no rebuild. Old images are kept for 72 h on Hobby
   and 120 h on Pro. After that, **Redeploy** rebuilds from that deployment's source. Roll back `web` and `api`
   together if both changed.
2. **The bad release included a migration:** an older `api` usually runs fine on a newer schema when the migration was
   additive (rule: API changes are additive). Its pre-deploy step finds nothing to apply, because EF bundles never
   downgrade. Roll back `api` only if the old code tolerates the new schema. Otherwise ship a fix forward.
3. **The schema or data is broken:** restore the Postgres backup (and `/data` from the same time), then roll back to a
   deployment that matches that schema.
4. **Failed migration:** the deploy is marked failed and the previous deployment keeps serving with
   `/api/health/ready` at 200. Fix the migration in a new commit. Never edit a committed migration.

Each rollback is a deployment, so run `deploy/scripts/smoke-remote.sh` afterwards.

## Costs and limits to watch

| Item | Note |
| --- | --- |
| Plan | Hobby is $5/month including $5 of usage; Pro is $20/month. Two environments double the usage |
| Usage | Each environment runs 3 always-on containers (web, api, postgres). RAM ($10/GB/month) dominates: .NET + Next.js + Postgres use roughly 0.6–1 GB per environment. Set usage limits and alerts (Workspace → Usage) |
| Volumes | $0.15/GB/month. `api-data` is 500 MB, the current plan's maximum (SPEC Q2 wants 5 GB after an upgrade). Watch the size: Data Protection keys are small, but `/data/workspaces` (Git working copies) can fill it |
| Backups | Billed as volume storage |
| Egress | $0.05/GB. LLM calls and Git clones count. Keep Postgres private (no TCP proxy) |
| Downtime on deploy | A service with a volume can't overlap deployments, so each `api` deploy has a short gap. SignalR clients reconnect. `web` deploys have no gap |
| Replicas | `api` must stay at 1 (volume, and the v1 job leases assume one worker). `web` can scale later |
| Image retention | Rollback without a rebuild only within 72 h (Hobby) or 120 h (Pro) |
| Region | Choose one region for all three services (latency: §8 p95 < 500 ms for the project list) |

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `railway config plan` says "requires Railway CLI 5.42.1 or newer" on Windows | `railway/iac` can't execute the npm shim. Set `$env:_` to `railway.exe` in PowerShell (step 2) |
| `api` deploy fails in pre-deploy | Deploy logs show the bundle error. `ConnectionStrings__Aide` should resolve: postgres running, and `PG*` references spelt with the service name `postgres` |
| `api` healthcheck fails (503) | DB unreachable, or migrations pending. `railway logs --service api` |
| `api` exits at start with an `App:PublicBaseUrl` validation error | `web` has no generated domain yet (step 3), so the reference resolved to `https://` |
| `/api/...` returns 502 `UPSTREAM_ERROR` | `web` can't reach `api.railway.internal:8080`: `api` down, or `BACKEND_URL` wrong. Kestrel listens on IPv4 and IPv6, so legacy IPv6-only private networks work |
| Cookies not `Secure`, or the live WebSocket refused (403) | `TRUST_EDGE_FORWARDED=true` missing on `web`, or `ForwardedHeaders__ForwardLimit` not 2 on `api` |
| Every user shares one login rate-limit bucket | `ForwardedHeaders__ForwardLimit` is not 2 |
| `railway config apply` fails: `Apply finished with status failed; failed changes: change ×N` | One rejected change fails the whole batch. Run `railway config apply --json --yes` and read `applyResult.diagnostics`. Seen: "Max size of 500 MB on current plan" for the `api-data` volume. Set `sizeMB: 500`, or upgrade the plan first |
| Plan warns `variables.ignore pattern "<name>" matches no variable` | Expected and harmless: that operator-set variable isn't set in this environment yet |
| Plan wants to delete a variable (CI: "Railway config drift") | It exists in Railway but not in `railway.ts`. Add its name to `operatorSetApiVariables` (`variables.ignore`) in `railway.ts`, then plan again. Never apply a plan that deletes secrets, and don't pass `--confirm-destructive` |
| Deploy fails "api is publicly reachable" | `api` has a service domain, custom domain or TCP proxy. Remove it in `api` → Settings → Networking |
| Deploy fails waiting for `api` (`FAILED`) | Usually the pre-deploy migration: `railway logs <deployment-id> --service api --environment <env> --deployment` |
| Plan wants to replace postgres | The image differs from `postgres-ssl:17`. Stop and reconcile. Never accept a database destroy |
| Plan wants to change or destroy volume `api-data` | `region` is unset in `railway.ts`. Railway assigns one (`iad`). Declare `region: 'iad'`. Don't accept a destructive volume change |
| Plan wants to change `builder` from RAILPACK to DOCKERFILE after apply | `build.dockerfilePath: 'Dockerfile'` is missing next to `builder: 'DOCKERFILE'` in `railway.ts` |
| Plan shows a restart-policy change every time | The policy is declared in `railway.ts`. Remove it: Railway's default (ON_FAILURE, 10) is stored as null |
