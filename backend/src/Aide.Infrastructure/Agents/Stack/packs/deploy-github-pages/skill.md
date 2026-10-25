<!-- Adapted from stack-packs deploy-github-pages@1.0.0 skills/deploy-github-pages/SKILL.md, snapshot 2026-10-25 -->

# GitHub Pages

AIDE enables Pages (source: GitHub Actions) through the project's GitHub App or PAT, and reads the deployment after the workflow runs on the merge commit. You write the workflow and the base-path handling.

## Supports
- Static output from `fe-vanilla`, `fe-react-vite`, `fe-vue` and `fe-angular`.
- `fe-nextjs` with `output: 'export'` only (ui mode or Supabase BaaS): no server actions, route handlers, ISR or middleware.
- No backend or database: pair it with an API on Render or Railway (**cross-origin** only) or with Supabase BaaS.
- Requires `vcs-github`. Sites are public, even from private repos on most plans: never ship anything sensitive in the bundle.

## Prerequisites and auth
None for you. Pages enablement and permissions are AIDE's. The workflow uses only the built-in `GITHUB_TOKEN` through its `permissions`.

## Config as code (`.github/workflows/pages.yml`)
```yaml
name: Deploy Pages
on:
  push: { branches: [main] }        # the default branch's real name
  workflow_dispatch:
permissions: { contents: read, pages: write, id-token: write }
concurrency: { group: pages, cancel-in-progress: false }
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with: { node-version-file: frontend/.nvmrc, cache: npm, cache-dependency-path: frontend/package-lock.json }
      - id: pages
        uses: actions/configure-pages@v5
      - run: npm ci && npm run build
        working-directory: frontend
        env:
          BASE_PATH: ${{ steps.pages.outputs.base_path }}
          VITE_API_BASE_URL: ${{ vars.VITE_API_BASE_URL }}   # cross-origin only; the frontend's prefix
      - run: cp frontend/dist/index.html frontend/dist/404.html   # SPA fallback (SPAs only)
      - uses: actions/upload-pages-artifact@v4
        with: { path: frontend/dist }
  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment: { name: github-pages, url: "${{ steps.deployment.outputs.page_url }}" }
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
```
- Replace `frontend` with the frontend path, `dist` with the build output (Angular `dist/<project>/browser`, Next.js export `out`). Fall back to `npm install` when no lockfile exists yet.
- Use current stable action majors you are confident exist.
- This file is the deploy-engineer's; `ci.yml` and every other workflow are the ci-engineer's. Never add deploy steps to `ci.yml`.
- `vanilla` without a build: upload the site folder directly; no Node steps.

## Base path (project sites at `https://<user>.github.io/<repo>/`)
- **Vite (React, Vue, vanilla):** `base: process.env.BASE_PATH ? process.env.BASE_PATH + '/' : '/'`, plus the router basename (`createBrowserRouter(routes, { basename: import.meta.env.BASE_URL })` / `createWebHistory(import.meta.env.BASE_URL)`). These files belong to the frontend-engineer: state the requirement in your step summary.
- **Angular:** `ng build --base-href "$BASE_PATH/"`.
- **Next.js export:** `basePath: process.env.BASE_PATH`, `output: 'export'`, `images: { unoptimized: true }`.
- **Vanilla without a build:** relative URLs throughout.
- User/org sites and custom domains use `/` (`base_path` is empty).

## Environment variables
- Only build-time public values, from `env:` in the workflow or repository variables (`vars.X`). No runtime secrets exist: everything in the bundle is public.
- Cross-origin: the API URL (`VITE_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, or Angular config) comes from a repository variable; the backend's `CORS_ALLOWED_ORIGINS` includes the Pages origin (`https://<user>.github.io`).

## Database
None. Use Supabase BaaS (publishable key + RLS) or a backend hosted elsewhere.

## Deploy commands
None beyond the workflow: a push to the default branch (the release merge) deploys. AIDE waits for that run and reads the Pages deployment for the merge SHA.

## Previews and promotion
No previews: every successful run replaces the live site, so AIDE skips Pages in the preview rollout. Promotion is the release merge.

## Agent autonomy
AIDE's own rules apply; not loaded by any agent.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| Blank page or asset 404s | Base path missing (project site), or the wrong artifact path |
| Deep links 404 | `404.html` copy missing (SPA), or router basename unset |
| Deploy job fails on permissions | `pages: write` and `id-token: write`, or Pages source not GitHub Actions |
| API calls blocked | CORS on the backend for the Pages origin, or an `http://` API (mixed content) |

## Checklist
- [ ] `pages.yml` triggers on the default branch, with the right permissions, environment, concurrency and artifact path.
- [ ] Base path and router basename handled; SPA `404.html` fallback where needed.
- [ ] Only public build-time values; cross-origin API URL from a variable, CORS on the backend.
- [ ] No deploy steps anywhere else.
