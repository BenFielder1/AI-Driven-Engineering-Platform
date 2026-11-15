---
title: Deployments and databases
parent: Guides
nav_order: 8
---

# Deployments and databases

Tevrin can deploy the projects it builds and provision their databases. Platforms: Vercel, Netlify, Cloudflare (Workers Builds, Pages), GitHub Pages, Render and Railway, set per component (frontend, backend). Databases: Supabase, Render Postgres or Railway Postgres, per environment.

## Connect a platform

Each user adds a token per platform under Account, Connections: Vercel, Netlify, Cloudflare, Render, Railway, and Supabase for databases. The connection dialog lists the token type and permissions to create. Tevrin checks the token, stores it encrypted and never shows it again. GitHub Pages uses the GitHub App instead, which needs its Pages permission (update the App in GitHub after upgrading).

## Bind it to a project

A project Owner binds connections in Settings, Deployment: one target per component (frontend, backend) and one database per environment. Tevrin can create the resource or link an existing one. The page shows readiness checks with fix links, the origin model, and the environment inventory. Derived variables (database URLs, Supabase keys, API origin) are written by Tevrin.

## How a deployment runs

- A platform must build from the repo's Git host, so some combinations are refused (for example Railway needs GitHub). The stack step shows the reason.
- Before review, a [build check](#build-check-before-review) waits for the preview rollout and the required CI checks: migrations, then backend, then frontend. Review reuses that result for the same commit. A failed build or migration is a critical finding. After the release merge, a production rollout runs the same steps. Rollback appears per platform where supported.
- When a build, deploy or CI check fails, review reads the redacted log excerpt and writes a specific fix step. Tevrin adds a **Failure evidence** block with the excerpt to that step. If the same failure repeats, review marks it and must try a different fix.
- The preview and production databases must be separate. The Owner can reset the preview database from its card.
- Tevrin keeps an environment inventory and a persistent preview environment, with preview and production rollouts.

## Build check before review

When execution finishes and the branch is pushed, the project stays in Execution with status **Building** while Tevrin checks the build of that commit:

1. Tevrin waits for the preview rollout (up to 30 minutes) and the PR's required CI checks.
2. If all succeed, the project moves to Review. If there is no preview target and no required check, the check is skipped.
3. If one fails, a build-fix agent reads the log excerpts and plans one to five fix steps for that attempt (Tevrin may add more), each with a **Failure evidence** block. The coding agents run them, Tevrin pushes, and the check runs again.
4. When the build is green, or the attempts run out, the project moves to Review with the result.

Set **Build fix attempts** in the new-project wizard or project Settings (0 to 10, default 5). 0 turns the build check off. The count starts again in each review cycle.

Build-fix steps can't hide errors. The Guardian rejects a step that turns off type or lint errors in the build, adds `@ts-ignore` or `@ts-nocheck`, skips hooks, or deletes or skips tests.

The execution page shows each attempt as a pair of cards: the build (each source's live status, its link and the failure excerpt), then the fix (planning, then the fix steps as they run). Earlier attempts collapse to one line. A check that stops says why: the attempts were used up, no valid fix plan could be made, someone chose to continue, a job failed, or the plan changed.

If the Git host won't say which checks are required (GitHub branch rules on a free private repo), CI checks are shown as **Not counted** and the check waits for the preview only. If the host can't be read for 15 minutes, the check goes on without CI.

## Review findings

Each deterministic finding (dependency, endpoint, secret, stack check, Vercel config) has a stable id. The optimisation plan must fix it in a step that names the id, or waive it with a reason when it's a false positive. Tevrin adds a step, marked **Added by Tevrin**, for a finding that has neither. Critical, secret and failure findings can't be waived. A waived finding stops counting against the score while it's unchanged. The report shows each finding as **Step n**, **Waived**, **Waiver refused** or **No step**.

## Branch names and branch control

New project branches are `tevrin/<slug>-<id>`, and new release-version branches are `tevrin/<slug>/v<version>`. Projects that already have an `aide/` branch keep it.

Preview deployments of these branches must not build on every push. Readiness checks that the config blocks every prefix the project uses.

| Platform | How the branches are blocked |
| --- | --- |
| Vercel | `vercel.json` sets `git.deploymentEnabled` to `false` for `"tevrin/**"` and `"aide/**"` |
| Netlify | The `[build] ignore` command names both prefixes |
| Cloudflare | The preview trigger excludes `tevrin/*` and `aide/*`. Tevrin adds `tevrin/*` to older triggers when you link or verify, and keeps your other excludes |

## Deployment files in plans

Each deploy pack declares the config files it needs: `vercel.json`, `netlify.toml`, `wrangler.jsonc`, `render.yaml`, `railway.json` or `.github/workflows/pages.yml`. The plan prompt names them. If a plan leaves one out, Tevrin adds a deploy-engineer step to create it. The plan view marks it **Added by Tevrin** and says why.

## Shared deployment projects

Parts on one platform (frontend, backend, database) form a deployment group. A group has one platform project, one window in the wizard and one card in Settings. You can split the parts into separate projects with "Deploy parts to separate projects". Vercel keeps one project per part.

## Testing without accounts

Everything is tested against stub HTTP and the Fake providers only. Real-platform checks are listed in PROGRESS.md under "Manual checks". Without accounts, use E2E mode (`Deployments__Provider=Fake`, `Databases__Provider=Fake`). See [Local Kubernetes](../operations/local-kubernetes.md).
