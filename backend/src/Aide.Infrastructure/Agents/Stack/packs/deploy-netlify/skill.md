<!-- Adapted from stack-packs deploy-netlify@1.0.0 skills/deploy-netlify/SKILL.md, snapshot 2026-10-25 -->

# Netlify

AIDE creates or links the Netlify site through the API (repo, base directory, production branch, `allowed_branches`, Deploy Previews off), syncs env vars per context and triggers branch builds. You only write the repo files Netlify reads during its build.

## Supports
- Static SPAs (`fe-react-vite`, `fe-vue`, `fe-angular`, `fe-vanilla`).
- `fe-nextjs` through Netlify's Next.js adapter (applied automatically), full-stack or ui mode.
- No backends: host the API on Render or Railway and use the `proxy` (default) or `cross-origin` origin model.
- With GitLab or Bitbucket, Netlify is linked by a read-only deploy key and a push webhook that AIDE adds; nothing changes in the repo.

## Prerequisites and auth
None for you. The Netlify token and the Git link are AIDE connections. Never commit tokens, site ids or `.netlify/`; make sure `.netlify` is in `.gitignore`.

## Config as code (`netlify.toml` at the site's base directory)
Write `netlify.toml` in the frontend folder (`{fe}/netlify.toml`; root when the app is at `.`):
```toml
[build]
  command = '''npm run build && if [ -n "$API_ORIGIN" ]; then printf '/api/*  %s/api/:splat  200!\n' "$API_ORIGIN" >> dist/_redirects; fi'''
  publish = "dist"            # Angular: "dist/<project>/browser"
  ignore = 'case "$BRANCH" in aide/*) exit 0 ;; *) exit 1 ;; esac'

# SPA fallback (static SPAs only, last)
[[redirects]]
  from = "/*"
  to = "/index.html"
  status = 200
```
- **Branch control:** `[build] ignore` must exit 0 (skip) for `aide/*` branches and 1 otherwise. AIDE triggers the version branch's builds itself. A missing rule is a major review finding.
- **Proxy origin model:** `_redirects` is generated at build time from `API_ORIGIN` into the publish directory, so preview and production point at different backends. `_redirects` rules are applied before `netlify.toml` redirects, so `/api/*` wins over the SPA fallback. The `200!` forces the proxy even if a file matches. Don't hard-code a backend host in `netlify.toml` or a committed `_redirects`. The command appends (`>>`), so a committed `{fe}/public/_redirects` is kept, but it must not contain a catch-all rule (it would shadow the appended `/api/*` line); keep the SPA fallback in `netlify.toml`.
- **Next.js:** no SPA rule and no generated `_redirects`; the adapter routes, and the proxy is Next's `rewrites()` (frontend pack). Pin Node with `.nvmrc` or `[build.environment] NODE_VERSION`.
- **Migrations** (Next.js full-stack with an ORM data pack): prefix the build with `if [ "$AIDE_RUN_MIGRATIONS" = "true" ]; then <migrate command>; fi && `.
- `[[headers]]` for security headers and immutable caching of hashed assets.
- `netlify.toml` overrides the site settings AIDE sets, so keep `command` and `publish` consistent with the frontend pack.
- **Cookies through the proxy** stay first-party; the backend's cookie domain and `SameSite` must suit the Netlify domain.

## Environment variables
- AIDE syncs the inventory to contexts `production`, `deploy-preview` and `branch-deploy` (API branch builds run as branch deploys). List every variable in `.env.example` (placeholders only).
- `API_ORIGIN` (proxy model) is read by the build command. Build-time public values (`VITE_*`, `NEXT_PUBLIC_*`) need a rebuild to change.

## Database
Netlify hosts no database here. Next.js full-stack uses Supabase (host mode) or an external Postgres: the pooled `DATABASE_URL` for functions, `DIRECT_URL` for migrations, which run only in the gated build. Never migrate in request handlers.

## Deploy commands
None in files. AIDE starts branch builds of the version branch, adopts the production deploy of the merge commit, and restores previous deploys for rollback. Don't add CI jobs or scripts that call the Netlify CLI.

## Previews and promotion
- One persistent preview: the version branch built as a branch deploy, with `API_ORIGIN` = the backend's preview URL. Deploy Previews per PR are off.
- Promotion is merging to the production branch, which Netlify builds automatically.

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| `aide/*` pushes build on their own | `[build] ignore` missing or exits non-zero for `aide/*` |
| 404 on refresh | SPA fallback missing, or not last |
| `/api` 404 or returns `index.html` | `API_ORIGIN` unset in that context, `_redirects` not written to the publish dir, or missing `!` |
| Next.js feature broken | Adapter and Next.js compatibility in Netlify's docs |
| Env missing | Wrong context (`branch-deploy` vs `deploy-preview`), or a build-time value that needs a rebuild |

## Checklist
- [ ] `netlify.toml` has the right command and publish dir, and an `ignore` that skips `aide/*`.
- [ ] Proxy model: `_redirects` generated from `API_ORIGIN` at build; no hard-coded backend host; SPA fallback last.
- [ ] Migrations only in the build gated by `AIDE_RUN_MIGRATIONS`.
- [ ] `.env.example` lists every variable; no tokens or `.netlify/` in the repo; no CLI deploy scripts.
