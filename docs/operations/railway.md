---
title: Railway
parent: Operations
nav_order: 2
---

# Deploy to Railway

Tevrin runs on Railway as one project with two environments, `preview` and `production`. Each has a public `web`, a private `api` with a `/data` volume, and its own Postgres 18. The project is defined as code in [`.railway/railway.ts`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/.railway/railway.ts). The Railway project and service names keep the internal name `aide`.

## How deploys happen

| Environment | Trigger |
| --- | --- |
| `preview` | `.github/workflows/deploy.yml` runs after green CI on `main` |
| `production` | Publishing a GitHub release `x.y.z` ([Releases](releases.md)), or manual dispatch of the same workflow. Both wait for approval |

Each deploy runs the read-only `railway config plan` (drift fails the deploy) and then the smoke test. You don't deploy from your machine. These two commands are the ones to run by hand:

```bash
railway config plan                                       # read-only diff of Railway vs .railway/railway.ts
bash deploy/scripts/smoke-remote.sh <web-url>             # smoke test a deployed environment
```

## One-time human steps

1. Create a Railway account on the Hobby plan or higher, and install the Railway CLI (5.49.1 or newer).
2. Create the project and the `preview` environment, then link the folder.
3. Create one project token per environment. Store each as the `RAILWAY_TOKEN` secret of the matching GitHub environment (`preview`, `production`).
4. Create a workspace token and store it as `RAILWAY_API_TOKEN` in both GitHub environments. It is used only by the drift check.
5. Set the secrets and optional variables per environment in Railway. Literals and references come from the IaC.
6. After an environment's first deploy, register the first user straight away. The first registered user becomes Admin.

The full procedure, with every command, is in the Railway ops guide below.

## Railway reference

The ops docs stay beside the deploy files in the repo.

| Document | Covers |
| --- | --- |
| [`deploy/railway/README.md`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/README.md) | First-time setup, secrets, the first Admin, the smoke account, GitHub App URLs, backups (Postgres and the `/data` volume), rollback and costs |
| [`deploy/railway/variables.md`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/variables.md) | Every Railway variable per service, and whether it is a literal, a reference or a secret |

{: .warning }
The `/data` volume holds the Data Protection keys that encrypt stored credentials. Back it up with Postgres. Losing it makes those secrets unreadable.

For the variables themselves, see [Configuration](../reference/configuration.md).
