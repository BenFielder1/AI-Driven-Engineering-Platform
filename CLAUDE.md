# AIDE Platform

AIDE (AI-Driven Engineering Framework) coordinates specialised AI agents through a governed software lifecycle — Refinement → Specification → Planning → Execution → Review → Release — turning an idea into released software with versioned artifacts, approval gates, rollback and human oversight. It is for engineers and project owners who want AI-driven delivery with deterministic state and an audit trail.

**`SPEC.md` is the source of truth.** Read the relevant section before building a feature. If code and spec disagree, flag it rather than silently picking one. Requirement IDs are referenced in commits and PRs.

## Versions

| Version | Spec | Build log | Status |
| --- | --- | --- | --- |
| v1 | [`docs/v1/SPEC.md`](docs/v1/SPEC.md) | [`docs/v1/PROGRESS.md`](docs/v1/PROGRESS.md) | Built and merged (PR #1). Frozen; never edit except to fix links |
| v2 | [`docs/v2/SPEC.md`](docs/v2/SPEC.md) | [`docs/v2/PROGRESS.md`](docs/v2/PROGRESS.md) | Built and merged (PR #13). Frozen; never edit except to fix links |
| v3 | [`SPEC.md`](SPEC.md) | `PROGRESS.md` (root; created by orchestrate-stack) | Current: Next.js frontend migration + Railway deploy, no new features. Stack in [`STACK.md`](STACK.md) |

- The root `SPEC.md` and `PROGRESS.md` always describe the version being built. When a version is done, move both into `docs/v<N>/` and add a row here.
- **v3 is an increment on v2 (and v1):** earlier requirements stay in force unless `SPEC.md` amends them. Requirement IDs continue across versions (v2 starts at FR-53, v3 at FR-94), and the spec-verifier checks the requirements of v1, v2 and v3.
- **Scaffolding:** v2 M0 was a baseline check, and v3 migrated the frontend in place. Never re-scaffold the repo.
- Version assets (e.g. the logo in `docs/v2/brand/`) live under `docs/v<N>/`.

## Stack

Packs and conventions are recorded in [`STACK.md`](STACK.md). The current stack:

- **Frontend** (`frontend/`): TypeScript (strict), Next.js 16 App Router, React 19, TanStack Query, Tailwind 4, React Hook Form + Zod, Vitest + Testing Library + MSW, Playwright, ESLint (typescript-eslint strict type-checked, plus `@next/eslint-plugin-next`).
  - Pages are client components (SPEC D-V3-2), and routing goes through `src/lib/navigation.ts`.
  - The web server is `frontend/server.ts`, a custom Node server run with Node 24 type stripping. It serves Next, proxies `/api` (including the SignalR WebSockets) to `BACKEND_URL` read at runtime, sets the CSP nonce and security headers, and writes redacted JSON request logs.
- **Backend** (`backend/`): C# / ASP.NET Core (.NET 10), minimal APIs, EF Core 10 (`dotnet-ef`, local tool manifest `backend/dotnet-tools.json`) + PostgreSQL 17 via Npgsql (snake_case naming, history table `__ef_migrations_history`), xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`), Testcontainers.PostgreSql; JSON console logs, OpenTelemetry (console exporter when `Telemetry:ConsoleExporter=true`, on in Development)
- **Background work**: hosted services in `Aide.Api` reading a PostgreSQL-backed job/outbox table — no Redis, no message broker (SPEC.md §10 D1)
- **Contract**: `backend/openapi/api.json` (generated, committed) → `frontend/src/api/generated/` (generated with `openapi-typescript` + `openapi-fetch`)
- **Deploy**: Docker images. Local: Kustomize manifests (`deploy/k8s`) on the Docker Desktop Kubernetes cluster (context `docker-desktop`). Remote: Railway `preview` and `production`, deployed by `.github/workflows/deploy.yml`, defined as code in `.railway/railway.ts`
- **VCS/CI**: Git, GitHub, GitHub Actions

Router, auth and background-work choices come from SPEC.md; if SPEC.md changes them, update this file.

## Layout

```
backend/
  Aide.slnx
  src/Aide.Api/             endpoints, DI, auth, ProblemDetails, OpenAPI, hosted background workers
  src/Aide.Domain/          pure business rules: lifecycle state machine, phase map, gate rules, versioning (no EF, no ASP.NET)
  src/Aide.Infrastructure/  Persistence/AideDbContext + entity configs, Migrations/, LLM providers, Git providers, notification channels
                            Agents/Prompts/*.md — versioned prompt templates (SPEC FR-24)
  src/Aide.Cli/             `aide` .NET global tool (SPEC FR-52)
  tests/Aide.UnitTests/     domain + application tests
  tests/Aide.IntegrationTests/ WebApplicationFactory + Testcontainers
  openapi/api.json          GENERATED contract — commit, never hand-edit
  Directory.Build.props · Directory.Packages.props · global.json · dotnet-tools.json
frontend/
  src/api/generated/        GENERATED client — never hand-edit
  src/features/<feature>/   pages, components, hooks, tests (projects, artifacts, gates, execution, review, admin)
  src/app/                  Next.js App Router: (public) and (app) route groups, thin 'use client' pages that render feature pages
  src/server/               web-server helpers for server.ts (proxy, headers, CSP nonce, log redaction)
  server.ts                 custom Node server (dev and production)
  src/lib/                  errors, permissions, config, query client
  src/mocks/                MSW handlers (typed with generated schemas), used by Vitest
  e2e/                      Playwright
deploy/
  k8s/base/                 namespace, db, api, web, migration job
  k8s/overlays/local/       docker-desktop overlay, secrets.env (git-ignored), secrets.env.example
  k8s/.generated/           GIT-IGNORED overlay written by deploy-local.sh (pins image tags)
  scripts/                  deploy-local.sh, teardown-local.sh, smoke.sh, validate-manifests.sh (offline render, no real secrets needed),
                            smoke-remote.sh (+ smoke-ws.mjs) for a deployed Railway URL
  railway/                  Railway ops docs (README.md) and variable inventory (variables.md)
.railway/                   Railway IaC (railway.ts), applied with `railway config plan|apply`
scripts/ci/check-generated.sh  CI: fails if generated files differ from what is committed
docker-compose.yml          dev-loop database only
.github/workflows/ci.yml · deploy.yml   CI gate · Railway deploy (preview after CI on main, production on dispatch)
SPEC.md · PROGRESS.md · STACK.md · CLAUDE.md   current version (v3)
docs/v1/                    v1 SPEC.md + PROGRESS.md (frozen)
docs/v2/                    v2 SPEC.md + PROGRESS.md (frozen) + AIDE brand assets
```

## Commands

```bash
# Dev loop (one-off setup: cp .env.example .env, pick a password, then
#   cd backend && dotnet tool restore && dotnet user-secrets set ConnectionStrings:Aide "Host=localhost;Port=5432;Database=aide;Username=aide;Password=<from .env>" --project src/Aide.Api)
docker compose up -d db                                   # dev database (PostgreSQL 17, values from .env)
dotnet run --project backend/src/Aide.Api                 # http://localhost:5080
npm --prefix frontend run dev                             # http://localhost:3000 (proxies /api + WebSockets → BACKEND_URL, default 5080)

# Backend
cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
dotnet ef migrations add <Name> --project src/Aide.Infrastructure --startup-project src/Aide.Api
dotnet ef database update --project src/Aide.Infrastructure --startup-project src/Aide.Api   # apply to the dev DB
dotnet build src/Aide.Api                                 # regenerates openapi/api.json

# Frontend
cd frontend && npm run typecheck && npm run lint && npm test && npm run build
npm run gen:api                                           # regenerate client from ../backend/openapi/api.json
npm run test:e2e                                          # Playwright (BASE_URL=http://localhost:3000 default, or http://localhost:8080) — CI runs it; not locally on this machine

# Local Kubernetes (Docker Desktop)
./deploy/scripts/deploy-local.sh                          # build images, apply, migrate, roll out, smoke test → http://localhost:8080
./deploy/scripts/teardown-local.sh                        # remove app (keeps DB data unless --wipe-data)

# Railway (deploys go through .github/workflows/deploy.yml, not from this machine)
railway config plan                                       # read-only diff of Railway vs .railway/railway.ts
bash deploy/scripts/smoke-remote.sh <web-url>             # smoke test a deployed environment
```

Agents follow STACK.md autonomy `preview`: they may deploy `preview`, and touch production only when Ben asks explicitly in the session.

**Gate** (what GitHub Actions CI runs, in `.github/workflows/ci.yml`. **Don't run it locally:** see Commit gate below):
```bash
(cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes) && (cd frontend && npm run typecheck && npm run lint && npm test && npm run build) && ./deploy/scripts/validate-manifests.sh
```

## Commit gate: review locally, gate on CI

This machine doesn't have the memory to run Docker Desktop, Testcontainers integration tests and E2E alongside the
build, so **the gate runs on GitHub Actions, not locally.** This overrides any skill or agent instruction to run the gate,
the tests or a local deploy before committing. That includes `orchestrate-stack` §7 steps 1 and 5, the "Done when: <gate
commands>" lines in its briefs, `stack-reviewer` "runs the gates", and `build-from-spec`.

For every phase or fix:

1. **Review first.** Launch the step reviewer (`stack-reviewer`) on the diff, with the requirement IDs, spec sections,
   base SHA and packs. It reviews statically (reading code, tests, contract and config) and **doesn't run** the gate,
   tests, Docker or deploys. Fix its Critical and Major findings through the owning agent before committing. The point
   of this step is to catch what CI would catch.
2. **Local commands are limited to:**
   - producing files that must be committed: `dotnet build src/Aide.Api` (contract), `npm run gen:api`,
     `dotnet ef migrations add`, and Next.js/route generation if a tool writes committed files
   - optionally, single quick checks on the touched layer, one at a time: `npm run typecheck`, `npm run lint`, a single
     Vitest file, `dotnet build` of one project

   **Never run locally:** the full `dotnet test` (integration tests start PostgreSQL in Docker), Playwright E2E,
   `docker build`, `docker compose`, `deploy-local.sh`, or the whole gate command.
3. **Commit and push** to the current build branch (e.g. `build/aide-v3`). This is standing permission to push **build
   branches only.** Never push to `main`, never force-push, and never amend a pushed commit. Use the commit format
   `phase N: <summary> (<IDs>)` or `fix: <summary> (<IDs>)`.
4. **Wait for CI on that commit.**
   - Find the run: `gh run list --branch <branch> --commit <sha> --json databaseId,status,conclusion,name`.
   - Wait for it in the background: `gh run watch <id> --exit-status`.
   - On failure, read the logs: `gh run view <id> --log-failed`. For job detail use
     `gh run view <id> --json jobs`, and download artifacts (test results) with `gh run download <id>`.
5. **Fix and repeat.** Send each failure to the owning agent. Review the fix diff (step 1), commit a new commit, push,
   and wait for CI again, until the head commit's run is green. A phase is done only when **CI is green on its head
   commit.** CI skips commits that only touch `*.md`, `docs/**` or `.claude/**`, so for those, the latest code commit's
   run counts. Record the run URL in PROGRESS.md.
6. **Local Kubernetes and three-browser E2E** aren't part of the agent gate. CI's image builds, manifest render and
   Chromium E2E stand in for them. List anything that still needs a manual Docker Desktop check under "Manual checks"
   in PROGRESS.md for Ben to run.

If CI can't run (no `gh` auth, Actions outage), stop and tell Ben. Don't fall back to running the gate locally.

## Rules that must not be broken

1. `openapi/api.json` and `frontend/src/api/generated/` are generated. Regenerate them, and never hand-edit them. API changes are additive unless SPEC.md records a breaking-change decision.
2. The frontend calls the backend only through the generated client, at same-origin `/api`. No hard-coded hosts.
3. Business rules live in `Aide.Domain`, pure and unit-tested. Endpoints stay thin.
4. Errors are ProblemDetails with an `errorCode` from the spec's error table. The frontend maps those codes to copy in one place.
5. Every schema change is an EF Core migration. Never edit a committed migration, and never use `EnsureCreated` outside tests.
6. Secrets are never committed: user-secrets for the dev loop, `deploy/k8s/overlays/local/secrets.env` (git-ignored) for Kubernetes. This includes LLM provider keys and GitHub credentials.
7. Deploy scripts target only the `docker-desktop` Kubernetes context.
8. Project lifecycle changes go only through the domain state machine. Every allowed and forbidden phase/status transition has a unit test.
9. Artifacts are immutable once `locked`. New content means a new version with a content hash and parent version; invalidation and rollback never rewrite history.
10. Agent (LLM) calls sit behind one provider abstraction, are never made from endpoints directly, and are faked in tests — no test hits a real provider API.

## Conventions

- C#: nullable on, warnings as errors, async with a `CancellationToken`, `TypedResults`, `ILogger` message templates, `TimeProvider` injected.
- TS: strict, no `any`. Server state lives in TanStack Query, and environment values come from same-origin config, not `NEXT_PUBLIC_*`, unless they're build-time constants. `BACKEND_URL` is server-only.
- Names: C# PascalCase, TS files kebab-case, components PascalCase, DB tables and columns snake_case (`UseSnakeCaseNamingConvention()`, matching the old schema).
- Data: `Guid.CreateVersion7()` keys, UTC `timestamptz` only (from `TimeProvider`), enums stored as strings, `xmin` concurrency token on aggregates edited concurrently (→ 409 `CONFLICT`), `jsonb` only for genuinely schemaless payloads (event payloads, agent outputs).

## Workflow

Use the `orchestrate-stack` skill for multi-phase work (v3 onwards). It loads the packs named in [`STACK.md`](STACK.md). v1 and v2 were built with `build-from-spec`. Either way, the commit gate above replaces the skill's local gate and local-run steps.

## Definition of done

- The step reviewer has no open Critical or Major findings, and GitHub Actions CI is green on the pushed head commit (see
  Commit gate). New behaviour has tests named after the requirement IDs.
- Contract and client are regenerated (if the API changed).
- Migration added and reviewed (if the schema changed).
- Docker Desktop redeploy and smoke test: not run by agents on this machine. They're listed under "Manual checks" in
  PROGRESS.md for Ben.
- SPEC.md is updated if behaviour differs from it.
