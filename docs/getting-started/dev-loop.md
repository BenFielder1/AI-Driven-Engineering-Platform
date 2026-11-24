---
title: Dev loop
parent: Getting started
nav_order: 2
---

# Dev loop

In the dev loop the database runs in Docker, and the backend and frontend run on your machine.

## One-off setup

```bash
cp .env.example .env                 # pick your own POSTGRES_PASSWORD (git-ignored)
docker compose up -d db              # PostgreSQL 18 on 127.0.0.1:5432
cd backend
dotnet tool restore
dotnet user-secrets set "ConnectionStrings:Tevrin" "Host=localhost;Port=5432;Database=aide;Username=aide;Password=<POSTGRES_PASSWORD from .env>" --project src/Tevrin.Api
dotnet ef database update --project src/Tevrin.Infrastructure --startup-project src/Tevrin.Api
cd ../frontend && npm ci
```

## Each session

```bash
docker compose up -d db                                   # dev database (PostgreSQL 18, values from .env)
dotnet run --project backend/src/Tevrin.Api                 # http://localhost:5080
npm --prefix frontend run dev                             # http://localhost:3000 (proxies /api + WebSockets → BACKEND_URL, default 5080)
```

| What | URL |
| --- | --- |
| App (use this one) | http://localhost:3000 |
| API | http://localhost:5080 |
| Database | localhost:5432 |

Open the app at <http://localhost:3000>. It proxies `/api` and the live-update WebSocket to the backend.

## Seed data

The Development environment enables the seed. On first start it creates the Admin `admin@aide.local`, the project "Demo: Todo API" (Fake provider, Local Git, released at `0.1.0`, so you can try "Start new version" at once) and "Demo: Recipe Finder" (a migrated 0.2.0 preset project, in planning), plus "Demo: Vue Notes" (Vue + Supabase BaaS, in specification). A 0.1.0 database keeps its demo project, and an untouched one is upgraded to a released `0.1.0`. The generated password is logged once at Warning in the `dotnet run` console. Look for `Seeded admin admin@aide.local with password ...`. If you missed it, sign in with another account (the first registered user is Admin) or reset the database volume.

## Change the schema

New migration: `dotnet ef migrations add <Name> --project src/Tevrin.Infrastructure --startup-project src/Tevrin.Api` (from `backend/`). Never edit a committed migration.

If you used the dev loop before the move to PostgreSQL 18, `docker compose up` now starts on a new, empty volume. See [Upgrade local data from PostgreSQL 17 to 18](../operations/postgres-18-upgrade.md).

## Next steps

- [Your first project](first-project.md)
- [Testing](../reference/testing.md) for the test commands
- [Configuration](../reference/configuration.md) for settings and secrets
