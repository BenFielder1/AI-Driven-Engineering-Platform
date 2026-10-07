# AIDE Platform

AIDE (AI-Driven Engineering Framework) coordinates specialised AI agents through a governed software lifecycle — Refinement → Specification → Planning → Execution → Review → Release — turning an idea into released software with versioned artifacts, approval gates, rollback and human oversight. It is for engineers and project owners who want AI-driven delivery with deterministic state and an audit trail.

**`SPEC.md` is the source of truth.** Read the relevant section before building a feature. If code and spec disagree, flag it rather than silently picking one. Requirement IDs are referenced in commits and PRs.

This is a rewrite of the previous implementation in `../AIDE-Platform-old-stack/` (read-only; see `../WORKSPACE.md` for the rewrite rules and a map of where things live in the old code).

## Stack

- **Frontend** (`frontend/`): TypeScript (strict) + React + Vite, TanStack Router, TanStack Query, Tailwind, React Hook Form + Zod, Vitest + Testing Library + MSW, Playwright
- **Backend** (`backend/`): C# / ASP.NET Core (.NET 10), minimal APIs, EF Core (`dotnet-ef`, local tool manifest) + PostgreSQL 17 via Npgsql (snake_case naming), xUnit, Testcontainers.PostgreSql
- **Background work**: hosted services in `Aide.Api` reading a PostgreSQL-backed job/outbox table — no Redis, no message broker (SPEC.md §10 D1)
- **Contract**: `backend/openapi/api.json` (generated, committed) → `frontend/src/api/generated/` (generated with `openapi-typescript` + `openapi-fetch`)
- **Deploy**: Docker images and Kustomize manifests (`deploy/k8s`) on the Docker Desktop Kubernetes cluster (context `docker-desktop`)
- **VCS/CI**: Git, GitHub, GitHub Actions

Router, auth and background-work choices come from SPEC.md; if SPEC.md changes them, update this file.

## Layout

```
backend/
  Aide.slnx
  src/Aide.Api/             endpoints, DI, auth, ProblemDetails, OpenAPI, hosted background workers
  src/Aide.Domain/          pure business rules: lifecycle state machine, phase map, gate rules, versioning (no EF, no ASP.NET)
  src/Aide.Infrastructure/  DbContext, entity configs, Migrations/, LLM providers, Git providers, notification channels
                            Agents/Prompts/*.md — versioned prompt templates (SPEC FR-24)
  src/Aide.Cli/             `aide` .NET global tool (SPEC FR-52)
  tests/Aide.UnitTests/     domain + application tests
  tests/Aide.IntegrationTests/ WebApplicationFactory + Testcontainers
  openapi/api.json          GENERATED contract — commit, never hand-edit
  Directory.Build.props · Directory.Packages.props · global.json
frontend/
  src/api/generated/        GENERATED client — never hand-edit
  src/features/<feature>/   pages, components, hooks, tests (projects, artifacts, gates, execution, review, admin)
  src/lib/                  errors, permissions, config
  e2e/                      Playwright
deploy/
  k8s/base/                 namespace, db, api, web, migration job
  k8s/overlays/local/       docker-desktop overlay, secrets.env (git-ignored), secrets.env.example
  scripts/                  deploy-local.sh, teardown-local.sh, smoke.sh
docker-compose.yml          dev-loop database only
.github/workflows/ci.yml
SPEC.md · PROGRESS.md · CLAUDE.md
```

## Commands

```bash
# Dev loop
docker compose up -d db                                   # dev database (PostgreSQL)
dotnet run --project backend/src/Aide.Api                 # http://localhost:5080
npm --prefix frontend run dev                             # http://localhost:5173 (proxies /api → 5080)

# Backend
cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
dotnet ef migrations add <Name> --project src/Aide.Infrastructure --startup-project src/Aide.Api
dotnet build src/Aide.Api                                 # regenerates openapi/api.json

# Frontend
cd frontend && npm run typecheck && npm run lint && npm test && npm run build
npm run gen:api                                           # regenerate client from ../backend/openapi/api.json
npm run test:e2e                                          # Playwright (BASE_URL=http://localhost:5173 or http://localhost:8080)

# Local Kubernetes (Docker Desktop)
./deploy/scripts/deploy-local.sh                          # build images, apply, migrate, roll out, smoke test → http://localhost:8080
./deploy/scripts/teardown-local.sh                        # remove app (keeps DB data unless --wipe-data)
```

**Gate:**
```bash
(cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes) && (cd frontend && npm run typecheck && npm run lint && npm test && npm run build) && kubectl kustomize deploy/k8s/overlays/local > /dev/null
```

## Rules that must not be broken

1. `openapi/api.json` and `frontend/src/api/generated/` are generated. Regenerate them, and never hand-edit them. API changes are additive unless SPEC.md records a breaking-change decision.
2. The frontend calls the backend only through the generated client, at same-origin `/api`. No hard-coded hosts.
3. Business rules live in `Aide.Domain`, pure and unit-tested. Endpoints stay thin.
4. Errors are ProblemDetails with an `errorCode` from the spec's error table. The frontend maps those codes to copy in one place.
5. Every schema change is an EF Core migration. Never edit a committed migration, and never use `EnsureCreated` outside tests.
6. Secrets are never committed: user-secrets for the dev loop, `deploy/k8s/overlays/local/secrets.env` (git-ignored) for Kubernetes. This includes LLM provider keys and GitHub credentials.
7. Deploy scripts target only the `docker-desktop` Kubernetes context.
8. Never edit, build, run or commit anything in `../AIDE-Platform-old-stack/`. Read it for behaviour only.
9. Project lifecycle changes go only through the domain state machine. Every allowed and forbidden phase/status transition has a unit test.
10. Artifacts are immutable once `locked`. New content means a new version with a content hash and parent version; invalidation and rollback never rewrite history.
11. Agent (LLM) calls sit behind one provider abstraction, are never made from endpoints directly, and are faked in tests — no test hits a real provider API.
12. Behaviour that differs from the old stack is logged under Decisions in PROGRESS.md with the reason. Removing an old-stack feature is an open question, not a decision.

## Conventions

- C#: nullable on, warnings as errors, async with a `CancellationToken`, `TypedResults`, `ILogger` message templates, `TimeProvider` injected.
- TS: strict, no `any`. Server state lives in TanStack Query, and environment values come from same-origin config, not `VITE_*`, unless they're build-time constants.
- Names: C# PascalCase, TS files kebab-case, components PascalCase, DB tables and columns snake_case (`UseSnakeCaseNamingConvention()`, matching the old schema).
- Data: `Guid.CreateVersion7()` keys, UTC `timestamptz` only (from `TimeProvider`), enums stored as strings, `xmin` concurrency token on aggregates edited concurrently (→ 409 `CONFLICT`), `jsonb` only for genuinely schemaless payloads (event payloads, agent outputs).

## Workflow

Use the `build-from-spec` skill for multi-phase work. Stack skills: `aspnet-endpoints`, `efcore-data`, `vite-react-ui`, `openapi-contract`, `docker-desktop-k8s`, `github-actions-ci`.

## Definition of done

- The gate passes, and new behaviour has tests named after the requirement IDs.
- Contract and client are regenerated (if the API changed).
- Migration added and reviewed (if the schema changed).
- Redeployed to Docker Desktop, and the smoke test passes (if Kubernetes is available).
- SPEC.md is updated if behaviour differs from it.
