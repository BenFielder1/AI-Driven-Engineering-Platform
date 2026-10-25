<!-- Adapted from stack-packs deploy-cloudflare@1.0.0 skills/deploy-cloudflare/SKILL.md, snapshot 2026-10-25 -->

# Cloudflare (Workers static assets / Pages)

AIDE connects the repo to Workers Builds through the API and creates two triggers: production (the default branch, `npx wrangler deploy`) and preview (excluding `aide/*`, `npx wrangler versions upload`). It syncs build variables and runtime secrets and starts SHA-pinned builds. You only write the repo files Wrangler reads during the build.

## Supports
- Static SPAs (`fe-react-vite`, `fe-vue`, `fe-angular`, `fe-vanilla`) on **Workers with static assets**.
- `fe-nextjs` through Cloudflare's Next.js adapter, with limits: some Node APIs and middleware aren't supported. Follow the adapter's current setup.
- No backends: host the API on Render or Railway and use the `proxy` (default) or `cross-origin` origin model.
- Git hosts: GitHub and gitlab.com only.
- **Pages** is adopt-only: keep an existing Pages project as it is; new projects use Workers.

## Prerequisites and auth
None for you. The Cloudflare user token and the Cloudflare Git app are AIDE connections. Never commit tokens or account ids; add `.wrangler/` and `.dev.vars` to `.gitignore`.

## Config as code (`frontend/wrangler.jsonc`)
Write `{fe}/wrangler.jsonc` (add `wrangler` as a frontend dev dependency only through the frontend-engineer):
```jsonc
{
  "$schema": "./node_modules/wrangler/config-schema.json",
  "name": "<app>-web",
  "compatibility_date": "<today, yyyy-mm-dd>",
  "assets": {
    "directory": "./dist",                 // Angular: ./dist/<project>/browser
    "not_found_handling": "single-page-application"
  }
}
```
- **Proxy origin model:** add `"main": "./worker/index.ts"` and set `"assets": { …, "binding": "ASSETS", "run_worker_first": ["/api/*"] }`. Write `{fe}/worker/index.ts`, a minimal Worker that forwards `/api/*` requests (method, headers, body, query) to `env.API_ORIGIN` + the same path, and serves everything else with `env.ASSETS.fetch(request)`. Type `Env` with `ASSETS: Fetcher` and `API_ORIGIN: string`. No hard-coded backend URL.
- **Branch control** lives on the preview trigger (`aide/*` excluded), which AIDE configures; nothing in `wrangler.jsonc` deploys branches.
- **Next.js:** follow the adapter's setup (`nodejs_compat` in `compatibility_flags`, its config files and build script). The proxy is Next's `rewrites()` (frontend pack), not a Worker.
- **Migrations** (Next.js full-stack with an ORM data pack): the build runs the migrate command only when `AIDE_RUN_MIGRATIONS=true` (a build variable); put the gate in the build command, never in Worker code.
- Don't add `routes`, custom domains, `env.*` blocks for other accounts, D1, KV or Hyperdrive bindings unless the spec asks.

## Environment variables
- **Runtime:** `API_ORIGIN` and other non-secret runtime values are synced by AIDE (don't hard-code them in `vars`; at most a placeholder documented in `.env.example`). Runtime secrets are shared by preview and production.
- **Build-time public values** (`VITE_*`, `NEXT_PUBLIC_*`) are build variables per trigger; changing them needs a rebuild.
- Local `wrangler dev` reads `.dev.vars` (git-ignored); list its keys in `.env.example`.

## Database
None hosted by this pack. Use Supabase (BaaS from the frontend, or host mode for Next.js full-stack) or an external Postgres, through its pooled URL. D1 and Hyperdrive aren't covered.

## Deploy commands
None in files. The triggers run `wrangler deploy` (production) and `wrangler versions upload` (preview); AIDE starts builds and rolls back by version. Don't add CI jobs or scripts that call Wrangler to deploy.

## Previews and promotion
- One persistent preview: AIDE starts the preview trigger with the PR head's commit hash and reads the version's preview URL. Automatic builds of `aide/*` are excluded.
- Promotion is merging to the default branch; the production trigger deploys it.

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| Deep links 404 | `not_found_handling: "single-page-application"`, or the wrong `assets.directory` |
| `/api` serves `index.html` | `run_worker_first` must include `/api/*`, with `main` set and the Worker forwarding |
| `/api` 5xx | `API_ORIGIN` unset for that environment, or the backend rejects the forwarded `Host` |
| Next.js runtime errors | `nodejs_compat`, compatibility date, adapter version, unsupported middleware |
| Build times out | Workers Builds has a 20-minute limit |

## Checklist
- [ ] `wrangler.jsonc` has the right name, asset dir, SPA handling and a real compatibility date.
- [ ] Proxy model: `main`, `binding: "ASSETS"`, `run_worker_first: ["/api/*"]` and a Worker forwarding to `env.API_ORIGIN`.
- [ ] No backend runs on Cloudflare; migrations only in the gated build.
- [ ] No tokens, account ids or hard-coded backend URLs in the repo; no CLI deploy scripts.
