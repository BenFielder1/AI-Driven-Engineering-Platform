---
title: Prerequisites
parent: Getting started
nav_order: 1
---

# Prerequisites

| Need | Version / note |
| --- | --- |
| .NET SDK | 10.0.401 (`backend/global.json`, roll-forward to the latest feature band) |
| Node.js | 24 (`frontend/.nvmrc`). The web server runs `.ts` files with Node type stripping |
| Docker Desktop | Needed for the dev database, integration tests and image builds |
| Kubernetes | Docker Desktop, Settings, Kubernetes, enable. This provides `kubectl` and the `docker-desktop` context |
| Memory | Give Docker Desktop enough for the cluster, a build, and the pods (limits: api 1 Gi, db 1 Gi, web 128 Mi) |
| `git` | Used by the Local Git provider (the API image installs its own) |
| Optional | `kubeconform`, `oasdiff`, `actionlint`; Playwright browsers (`npx playwright install`) |

## Notes on the cluster provisioner

Docker Desktop's default (kubeadm) cluster shares Docker's image store, so built images are visible at once. With the kind provisioner, `deploy-local.sh` detects the containerd runtime and loads the images into the `desktop-control-plane` / `desktop-worker` node containers. Scripts only ever use the `docker-desktop` context.

## Connect a database GUI

Connect a database GUI:

- Dev loop: `localhost:5432`, database, user and password from your `.env`.
- Kubernetes: `kubectl --context docker-desktop -n aide port-forward svc/db 15432:5432`, then `localhost:15432`. The user is `aide`. The password is `POSTGRES_PASSWORD` in `deploy/k8s/overlays/local/secrets.env`.

## For Railway deploys only

The Railway CLI (5.49.1 or newer) is needed only to inspect or operate a Railway deployment. See [Railway](../operations/railway.md).
