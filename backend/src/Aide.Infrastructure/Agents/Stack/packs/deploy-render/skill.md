<!-- Adapted from stack-packs deploy-render@1.0.0 skills/deploy-render/SKILL.md, snapshot 2026-10-25 -->

# Render

AIDE creates the Render services and Postgres through the API (not from a Blueprint): production services auto-deploy from `main`, `-preview` services have auto-deploy off, and AIDE sets root dir, runtime, build/start commands, Dockerfile path, pre-deploy command, health check path, static routes and env vars from the composition. You write the files those settings point at so the build succeeds.

## Supports
- Backends: `be-express`, `be-nestjs` (`runtime: node`), `be-fastapi` (`runtime: python`), `be-aspnet`, `be-spring-boot` (`runtime: docker`).
- `fe-nextjs` as a Node web service (`npm run build`, `npm start`).
- Static SPAs as static sites (proxy routes set by AIDE, or cross-origin).
- Render Postgres. No SQL Server (use an External connection string).
- Git hosts: GitHub, gitlab.com, Bitbucket.

## Prerequisites and auth
None for you. The Render API key, workspace and Git connection are AIDE connections. Never commit keys or service ids.

## Config as code (`render.yaml` Blueprint at repo root)
- **Docker services:** `{be}/Dockerfile` per the backend pack's Containerise recipe: multi-stage, non-root `USER`, listens on `0.0.0.0:$PORT` (Render injects `PORT`, default 10000; `EXPOSE` it or read the env), and a `.dockerignore`. The image must contain the data pack's one-off migrate command (e.g. an EF bundle, Flyway, Alembic, Prisma CLI), because AIDE runs it as the `preDeployCommand`.
- **Native runtimes:** the build and start commands come from the backend/frontend pack (`npm ci && npm run build` / `npm start`, `uv sync` / `uvicorn … --host 0.0.0.0 --port $PORT`); the migrate command must run from the same build output.
- **Health:** the backend's ready path (`/api/health/ready`; Spring `/api/health/readiness`) must exist and check the database.
- **Static sites:** AIDE sets the SPA fallback (`/*` → `/index.html`) and, for the proxy model, `/api/*` → the backend URL per service. Don't hard-code backend hosts in the frontend.
- **Free instances** have no pre-deploy: the container's start command runs the migrate command first (single instance only), then starts the app. Write the entrypoint so it exits non-zero when migration fails.
- **`render.yaml` is optional documentation** for users who later manage the project themselves. AIDE never applies it. If you write it, it must mirror what AIDE creates (services, `rootDir`, `runtime`, `dockerfilePath`, `preDeployCommand`, `healthCheckPath`, `fromDatabase` for `DATABASE_URL`, `sync: false` for secrets, `previews.generation: off`), with a header comment saying so, and never literal secret values.

## Environment variables
- AIDE syncs the inventory to each service (production and `-preview`). List every variable in `.env.example` (placeholders only).
- `DATABASE_URL` is `postgres://…` (the internal connection string). `be-aspnet` and `be-spring-boot` convert it at startup (see their packs); Prisma, Drizzle and SQLAlchemy read it directly (SQLAlchemy normalises the scheme).
- Don't rely on Render-only variables except `PORT` and `RENDER_EXTERNAL_URL`.

## Database
Render Postgres, created by AIDE in the backend's region (version 17), plus `<name>-preview` for the preview service. Migrations run in the backend's pre-deploy (paid) or at container start (free), with the data pack's one-off migrate command. Migrations must be backward compatible (expand/contract), because the old version keeps serving until the new one is healthy. Free Postgres expires after 30 days.

## Deploy commands
None in files. AIDE deploys `-preview` services pinned to the PR head commit, adopts the production auto-deploy of the merge commit, and rolls back by deploy id. Don't add CI jobs or scripts that call the Render CLI or deploy hooks.

## Previews and promotion
- One persistent preview: `<name>-preview` services (and database) tracking the version branch, deployed explicitly. Render PR previews stay off.
- Promotion is merging to `main`.

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| Deploy fails health check | Health path, binding to `0.0.0.0:$PORT`, slow start-up |
| Pre-deploy migration fails | Migrate command present in the image, `DATABASE_URL` format for the backend, SSL required |
| SPA deep-link 404 | Rewrite to `/index.html` missing on the static site |
| Free instance slow | Free services sleep after 15 minutes idle |

## Checklist
- [ ] Each Docker service has a non-root Dockerfile and `.dockerignore`, listens on `$PORT`, and contains the migrate command.
- [ ] The ready health path exists; the backend handles the `DATABASE_URL` format.
- [ ] Free instances migrate at container start and fail fast on error.
- [ ] Any `render.yaml` is marked documentation, mirrors AIDE's resources, and has no literal secrets.
