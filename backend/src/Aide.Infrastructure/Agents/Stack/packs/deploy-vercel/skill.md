<!-- Adapted from stack-packs deploy-vercel@1.0.0 skills/deploy-vercel/SKILL.md, snapshot 2026-10-25 -->

# Vercel

AIDE creates or links the Vercel project through the API (one project per deployable folder, Root Directory = that folder), connects the Git repo, syncs env vars and triggers deploys. You only write the repo files Vercel reads during its build.

## Supports
- Next.js (native, all features), in full-stack or ui mode.
- Static SPAs (`fe-react-vite`, `fe-vue`, `fe-angular`, `fe-vanilla`). **Cross-origin only:** `vercel.json` rewrites have fixed destinations, so they can't point preview and production at different backends. Don't add an `/api` rewrite to a backend URL.
- `be-express`, `be-nestjs` (Node) and `be-fastapi` (Python) as Vercel Functions. Not `be-aspnet` or `be-spring-boot`.
- **Functions are serverless:** no in-memory sessions, no background workers or long-running jobs, and databases need pooled connections.

## Prerequisites and auth
None for you. The user's Vercel token and the Vercel Git integration (GitHub, GitLab or Bitbucket) are AIDE connections. Never write `.vercel/`, tokens, org or project ids into the repo; make sure `.vercel` is in `.gitignore`.

## Config as code
Write `vercel.json` at the root of **every** deployable folder on Vercel (`{fe}/vercel.json`, `{be}/vercel.json`, or `./vercel.json` when the app is at the root). It is required even if nothing else is configured, because it carries branch control:
```json
{
  "$schema": "https://openapi.vercel.sh/vercel.json",
  "git": { "deploymentEnabled": { "aide/**": false } }
}
```
- **Branch control:** `git.deploymentEnabled` must contain `"aide/**": false`, and no other key that matches `aide/**` may be `true`. AIDE deploys its version branches explicitly. A missing rule is a major review finding.
- **SPA fallback** (static SPAs without a framework preset that handles it): `"rewrites": [{ "source": "/(.*)", "destination": "/index.html" }]`. Framework presets detect Vite, Angular and Next.js; set `outputDirectory` only when it differs (`dist`, `dist/<project>/browser`).
- **Migrations on serverless** (host-mode database with an ORM data pack): the production build runs the data pack's one-off migrate command only when `AIDE_RUN_MIGRATIONS=true`:
  `"buildCommand": "if [ \"$AIDE_RUN_MIGRATIONS\" = \"true\" ]; then <migrate command>; fi && npm run build"`.
  Settings in `vercel.json` override the project settings AIDE sets, so keep the build command consistent with the frontend/backend pack's build.
- **FastAPI:** Vercel finds `app` in an entrypoint such as `app/main.py`, or `[tool.vercel] entrypoint = "pkg.module:app"` in `pyproject.toml`. Dependencies come from `pyproject.toml`/`requirements.txt`.
- **Express/NestJS:** export the app from an entrypoint Vercel supports (per the backend pack's Deploy notes); `functions` (`maxDuration`, memory) only when needed.
- `headers` for security headers and immutable caching of hashed assets. `crons` only if the spec needs scheduled work, and only for idempotent endpoints.

## Environment variables
- AIDE syncs the environment inventory to the Production and Preview environments. List every variable the app needs in `.env.example` (placeholders only).
- `NEXT_PUBLIC_*` / `VITE_*` are inlined at build time; a change needs a rebuild. Server-only values never use a public prefix.
- Cross-origin SPAs read `API_BASE_URL` with the frontend's public prefix; the backend allows the Vercel production URL and the preview URL pattern in `CORS_ALLOWED_ORIGINS`.

## Database
Vercel hosts no database here. Use Supabase (host mode) or an external Postgres. The app uses the **pooled** `DATABASE_URL` (Supabase transaction pooler `:6543`, prepared statements off); migrations use `DIRECT_URL` (session pooler `:5432`) and run only in the gated build command above. Never run migrations in request handlers or at function cold start.

## Deploy commands
None in files. AIDE deploys previews with an explicit Git-source deployment of the version branch, adopts the production deploy of the merge commit, and rolls back by promotion. Don't add CI jobs or scripts that call the Vercel CLI.

## Previews and promotion
- One persistent preview: AIDE deploys the version branch's PR head. Automatic branch deploys of `aide/**` are off (branch control above); the default branch still deploys production automatically.
- Promotion is merging to the default branch. Preview builds skip migrations unless a preview database exists (`AIDE_RUN_MIGRATIONS` is then `true` in Preview too).

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| `aide/**` branches deploy automatically | `git.deploymentEnabled` missing, misspelt, or a broader `true` rule matches |
| 404 on refresh in a SPA | Missing SPA rewrite or wrong output directory |
| Function times out | `maxDuration`, DB without a pooler, cold start |
| `too many connections` | Use the pooled URL and a pool size of 1–2 per function |
| Env var undefined | Wrong environment scope, or a build-time variable that needs a rebuild |

## Checklist
- [ ] Every Vercel-deployed folder has `vercel.json` with `git.deploymentEnabled {"aide/**": false}`.
- [ ] Framework preset/output dir correct; SPA fallback where needed; no `/api` rewrite to a fixed backend.
- [ ] Migrations only in the build gated by `AIDE_RUN_MIGRATIONS`, using `DIRECT_URL`; the app uses the pooled URL.
- [ ] `.env.example` lists every variable; no secrets or `.vercel/` in the repo; no CLI deploy scripts.
