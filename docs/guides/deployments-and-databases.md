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
- Review waits for a preview rollout: migrations, then backend, then frontend. A failed build or migration is a critical finding. After the release merge, a production rollout runs the same steps. Rollback appears per platform where supported.
- The preview and production databases must be separate. The Owner can reset the preview database from its card.
- Tevrin keeps an environment inventory and a persistent preview environment, with preview and production rollouts.

## Shared deployment projects

Parts on one platform (frontend, backend, database) form a deployment group. A group has one platform project, one window in the wizard and one card in Settings. You can split the parts into separate projects with "Deploy parts to separate projects". Vercel keeps one project per part.

## Testing without accounts

Everything is tested against stub HTTP and the Fake providers only. Real-platform checks are listed in PROGRESS.md under "Manual checks". Without accounts, use E2E mode (`Deployments__Provider=Fake`, `Databases__Provider=Fake`). See [Local Kubernetes](../operations/local-kubernetes.md).
