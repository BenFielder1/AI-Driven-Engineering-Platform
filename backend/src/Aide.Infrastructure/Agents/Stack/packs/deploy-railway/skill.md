<!-- Adapted from stack-packs deploy-railway@1.0.0 skills/deploy-railway/SKILL.md, snapshot 2026-10-25 -->

# Railway

AIDE creates the Railway project, services, Postgres, domains and the `aide-preview` environment through the API (not with IaC or the CLI). Services build from GitHub on `main`, PR environments are off, and AIDE sets root directory, Dockerfile path or build/start commands, pre-deploy command, healthcheck path and watch patterns from the composition. You write the files those settings point at so the build succeeds.

## Supports
- Every backend pack (Railpack or the pack's Dockerfile; prefer the Dockerfile for .NET and Java).
- `fe-nextjs` (Node service).
- Static SPAs in the frontend pack's nginx container (or Railpack static).
- Railway Postgres. No SQL Server (use an External connection string).
- Git host: **GitHub only**.

## Prerequisites and auth
None for you. The Railway token and the Railway GitHub app are AIDE connections. Never commit tokens or project/service ids.

## Config as code
- **Services:** one per deployable folder (`{be}`, `{fe}`), each with its Dockerfile (backend/frontend pack Containerise recipe: multi-stage, non-root `USER`, `.dockerignore`), listening on `$PORT` (Railway injects it) on all interfaces, including IPv6 (`::`) for private networking.
- **Health:** the backend's ready path (`/api/health/ready`; Spring `/api/health/readiness`).
- **Migrations:** the image must contain the data pack's one-off migrate command, which AIDE sets as the pre-deploy command.
- **Same-origin SPA** (frontend and backend both on Railway): the SPA's `{fe}/nginx.conf` proxies `/api/` to the backend over the private network. Copy it to `/etc/nginx/templates/default.conf.template` in the Dockerfile so the image's envsubst fills `${API_ORIGIN}` (e.g. `http://api.railway.internal:8080`) and `${PORT}` at start; use `resolver [fd12::10] valid=10s;` with a variable in `proxy_pass` so the private name re-resolves. Keep the SPA fallback and cache headers from the frontend pack.
- **`.railway/railway.ts` is optional documentation** for users who later manage the project themselves. AIDE never applies it. If you write it, mirror what AIDE creates (services, root dirs, Dockerfile paths, pre-deploy, healthchecks, `DATABASE_URL` from the Postgres service), add a header comment saying so, and never literal secrets. Don't write legacy `railway.json`/`railway.toml` (deprecated).

## Environment variables
- AIDE syncs the inventory per environment (production, `aide-preview`). List every variable in `.env.example` (placeholders only).
- `DATABASE_URL` is the reference `${{Postgres.DATABASE_URL}}` (`postgresql://…`, private network). `be-aspnet` and `be-spring-boot` convert it at startup (see their packs).
- Private service-to-service traffic uses `<service>.railway.internal`. Don't rely on other Railway variables except `PORT` and `RAILWAY_PUBLIC_DOMAIN`.

## Database
Railway Postgres with a volume, one per environment (`aide-preview` gets its own copy). Migrations run in each environment's pre-deploy with the data pack's one-off migrate command, and must be backward compatible (expand/contract).

## Deploy commands
None in files. AIDE deploys the PR head commit to `aide-preview`, deploys or adopts the merge commit in production, and rolls back by deployment. No `railway up`, `railway config apply` or CLI steps in CI or scripts.

## Previews and promotion
- One persistent preview: the `aide-preview` environment, copied from production, deployed explicitly per commit. PR environments stay off.
- Promotion is merging to `main`.

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| Build picks the wrong builder | Root directory or Dockerfile path; add a Dockerfile to force it |
| App unreachable | Listen on `$PORT`, healthcheck path, generated domain |
| SPA `/api` 502 | `API_ORIGIN`, backend listening on `::`, nginx `resolver` for `railway.internal` |
| DB connection fails | Variable reference, `DATABASE_URL` conversion (.NET/Java), private vs public URL |

## Checklist
- [ ] Each service has a non-root Dockerfile and `.dockerignore`, listens on `$PORT` (IPv4 and IPv6), and has a ready health path.
- [ ] The migrate command is in the image for the pre-deploy.
- [ ] Same-origin SPA: nginx proxies `/api/` to `API_ORIGIN` over the private network.
- [ ] Any `.railway/railway.ts` is marked documentation; no legacy config, secrets or CLI deploy steps.
