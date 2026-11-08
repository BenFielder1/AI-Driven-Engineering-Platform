# Tevrin

Tevrin (formerly AIDE, the AI-Driven Engineering Framework) coordinates specialised AI agents through a governed software lifecycle — Refinement → Specification → Planning → Execution → Review → Release — turning an idea into released software with versioned artifacts, approval gates, rollback and human oversight. It is for engineers and project owners who want AI-driven delivery with deterministic state and an audit trail.

**`SPEC.md` is the source of truth.** Read the relevant section before building a feature. If code and spec disagree, flag it rather than silently picking one. Requirement IDs are referenced in commits and PRs.

## Versions

| Version | Spec | Build log | Status |
| --- | --- | --- | --- |
| 0.1.0 | [`docs/0.1.0/SPEC.md`](docs/0.1.0/SPEC.md) | [`docs/0.1.0/PROGRESS.md`](docs/0.1.0/PROGRESS.md) | Built and merged (PR #1). Frozen; never edit except to fix links |
| 0.2.0 | [`docs/0.2.0/SPEC.md`](docs/0.2.0/SPEC.md) | [`docs/0.2.0/PROGRESS.md`](docs/0.2.0/PROGRESS.md) | Built and merged (PR #13). Frozen; never edit except to fix links |
| 0.3.0 | [`docs/0.3.0/SPEC.md`](docs/0.3.0/SPEC.md) | [`docs/0.3.0/PROGRESS.md`](docs/0.3.0/PROGRESS.md) | Built and merged (PR #16, #20, #21, #22, #23). Frozen; never edit except to fix links |
| 0.4.0 | [`docs/0.4.0/SPEC.md`](docs/0.4.0/SPEC.md) | [`docs/0.4.0/PROGRESS.md`](docs/0.4.0/PROGRESS.md) | Built and merged (PR #27). Frozen; never edit except to fix links |
| 0.5.0 | [`docs/0.5.0/SPEC.md`](docs/0.5.0/SPEC.md) | [`docs/0.5.0/PROGRESS.md`](docs/0.5.0/PROGRESS.md) | Built and merged (PR #28). Frozen; never edit except to fix links |
| 0.6.0 | [`SPEC.md`](SPEC.md) | [`PROGRESS.md`](PROGRESS.md) | Current: rename to Tevrin, brand and design system, Home dashboard, marketing site, docs site on GitHub Pages |

- **Version numbers are semver `x.y.z`** (SPEC FR-229, D-V6-24, D-V6-26). The versions first specified as v1–v6 are 0.1.0–0.6.0. A feature version raises the minor number (next: 0.7.0), a fix-only release the patch number. Use semver everywhere: folders (`docs/<x.y.z>/`), history pages (`docs/history/<x.y.z>.md`), prose, comments, test names and file names. Existing requirement and decision IDs keep their form (`D-V2-11`, `D-V6-n`); from 0.7.0 new decisions are `D-<x.y.z>-n` (e.g. `D-0.7.0-1`). The release version lives in `<Version>` in `backend/Directory.Build.props`, `version` in `frontend/package.json` (+ lock) and the newest entry of `frontend/src/features/marketing/content/changelog.ts`; `scripts/ci/check-version.sh` fails CI if they differ. Bump all three when a new version starts.
- The root `SPEC.md` and `PROGRESS.md` always describe the version being built. When a version is done, move both into `docs/<x.y.z>/` and add a row here. Then release it (see Releases).
- **0.6.0 is an increment on 0.5.0 (and 0.1.0–0.4.0):** earlier requirements stay in force unless `SPEC.md` amends them. Requirement IDs continue across versions (0.2.0 starts at FR-53, 0.3.0 at FR-94, 0.4.0 at FR-113, 0.5.0 at FR-163, 0.6.0 at FR-196), and the spec-verifier checks the requirements of 0.1.0–0.6.0.
- **Scaffolding:** 0.2.0, 0.4.0, 0.5.0 and 0.6.0 M0 were baseline checks, and 0.3.0 migrated the frontend in place. Never re-scaffold the repo.
- Version assets (e.g. the logo in `docs/0.2.0/brand/`) live under `docs/<x.y.z>/`.

## Stack

Packs and conventions are recorded in [`STACK.md`](STACK.md). The current stack:

- **Frontend** (`frontend/`): TypeScript (strict), Next.js 16 App Router, React 19, TanStack Query, Tailwind 4, React Hook Form + Zod, Vitest + Testing Library + MSW, Playwright, ESLint (typescript-eslint strict type-checked, plus `@next/eslint-plugin-next`).
  - Pages are client components (SPEC D-V3-2), and routing goes through `src/lib/navigation.ts`.
  - The web server is `frontend/server.ts`, a custom Node server run with Node 24 type stripping. It serves Next, proxies `/api` (including the SignalR WebSockets) to `BACKEND_URL` read at runtime, sets the CSP nonce and security headers, and writes redacted JSON request logs.
- **Backend** (`backend/`): C# / ASP.NET Core (.NET 10), minimal APIs, EF Core 10 (`dotnet-ef`, local tool manifest `backend/dotnet-tools.json`) + PostgreSQL 18 via Npgsql (SPEC D-V5-1) (snake_case naming, history table `__ef_migrations_history`), xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`), Testcontainers.PostgreSql; JSON console logs, OpenTelemetry (console exporter when `Telemetry:ConsoleExporter=true`, on in Development)
- **Background work**: hosted services in `Aide.Api` reading a PostgreSQL-backed job/outbox table — no Redis, no message broker (SPEC.md §10 D1)
- **Contract**: `backend/openapi/api.json` (generated, committed) → `frontend/src/api/generated/` (generated with `openapi-typescript` + `openapi-fetch`)
- **Deploy**: Docker images. Local: Kustomize manifests (`deploy/k8s`) on the Docker Desktop Kubernetes cluster (context `docker-desktop`). Remote: Railway `preview` (after green CI on `main`) and `production` (when a GitHub release `x.y.z` is published, or by dispatch), deployed by `.github/workflows/deploy.yml`, defined as code in `.railway/railway.ts`
- **VCS/CI**: Git, GitHub, GitHub Actions. `.github/workflows/docs.yml` builds the docs site (Jekyll, just-the-docs) from `docs/` and publishes it to GitHub Pages at <https://docs.tevrin.dev/> (custom domain, empty `baseurl`)

Router, auth and background-work choices come from SPEC.md; if SPEC.md changes them, update this file.

## Layout

```
backend/
  Aide.slnx
  src/Aide.Api/             endpoints, DI, auth, ProblemDetails, OpenAPI, hosted background workers
  src/Aide.Domain/          pure business rules: lifecycle state machine, phase map, gate rules, versioning (no EF, no ASP.NET)
  src/Aide.Infrastructure/  Persistence/AideDbContext + entity configs, Migrations/, LLM providers, Git providers, notification channels
                            Agents/Prompts/*.md — versioned prompt templates (SPEC FR-24)
  src/Aide.Cli/             `tevrin` .NET global tool, package Tevrin.Cli (SPEC FR-52, D-V6-2)
  src/Aide.Cli.Alias/       deprecated `aide` command (package Aide.Cli), same sources as Aide.Cli
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
scripts/ci/check-version.sh    CI + release deploy: the three version sources agree (and equal the release tag)
docker-compose.yml          dev-loop database only
.github/workflows/ci.yml · deploy.yml   CI gate · Railway deploy (preview after CI on main, production on a published release or dispatch)
.github/workflows/docs.yml         docs site build + link check, deployed to GitHub Pages (FR-226)
SPEC.md · PROGRESS.md · STACK.md · CLAUDE.md   current version (0.6.0)
docs/                       Jekyll + just-the-docs site (index.md, _config.yml, Gemfile) and the version archive:
  index.md                  site home
  getting-started/ guides/ operations/ reference/ history/   site sections (front matter: title, nav_order, parent)
  brand.md                  logo files, colours, usage rules
  0.1.0 … 0.5.0/            frozen SPEC.md + PROGRESS.md + research (never edit except links; no front matter)
  0.6.0/                    brand assets (and the 0.6.0 SPEC/PROGRESS once done)
```

## Commands

```bash
# Dev loop (one-off setup: cp .env.example .env, pick a password, then
#   cd backend && dotnet tool restore && dotnet user-secrets set ConnectionStrings:Aide "Host=localhost;Port=5432;Database=aide;Username=aide;Password=<from .env>" --project src/Aide.Api)
docker compose up -d db                                   # dev database (PostgreSQL 18, values from .env)
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

Agents follow STACK.md autonomy `preview`: they may deploy `preview`, and touch production only when Ben asks explicitly in the session. Publishing a release deploys production, so it follows the same rule (see Releases).

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

## Releases

A release is a semver tag `x.y.z` (no `v` prefix) on a `main` merge commit plus a GitHub release from it (SPEC FR-230,
FR-231, D-V6-25). **Publishing the release deploys `production`** (`deploy.yml`, after the `production` environment's
reviewer approves). Ben tags and releases, or an agent does, **only after Ben confirms the version and commit in the
session.** Tag pushes and releases are outside the build-branch push permission in the Commit gate.

0. **Once:** the `production` GitHub environment is set up as in `docs/operations/releases.md` (required reviewer,
   secrets, deployment branches and tags `main` + `*.*.*`). If it isn't, stop and tell Ben: don't publish.
1. **Ready:** the version's PR is merged into `main`, the root SPEC/PROGRESS are in `docs/<x.y.z>/` with a row above, and
   the changelog entry has its release date (`date`, not `null`).
2. **Confirm with Ben:** the version `x.y.z` and the commit: the newest `main` commit with a green CI run
   (`gh run list --workflow ci.yml --branch main --status success --limit 1 --json headSha`). CI skips docs-only
   commits, so that may not be `origin/main`'s head.
3. **Check:** `git checkout --detach <sha> && bash scripts/ci/check-version.sh x.y.z` passes (it also requires the
   changelog date).
4. **Tag:** `git tag -a x.y.z <sha> -m "Tevrin x.y.z"` then `git push origin x.y.z`. Never move, delete or re-push a
   tag a release uses; ship a fix as the next patch version.
5. **Release:** `gh release create x.y.z --verify-tag --latest --title "Tevrin x.y.z" --notes-file <notes>`, the notes
   built from `docs/history/<x.y.z>.md` and the changelog entry, plus `--generate-notes` for the PR list if wanted.
6. **Watch the deploy:** `gh run list --workflow deploy.yml --event release --limit 1`, then `gh run watch <id>
   --exit-status`. It waits for Ben's approval on the `production` environment. Report the result and the smoke test.
   If it fails, follow the job summary's rollback steps; don't re-tag.

Pre-releases, and releases that aren't the repo's latest (e.g. backfilled 0.1.0–0.5.0, created with `--latest=false`),
never deploy. Promoting a pre-release to a full release doesn't deploy either (GitHub sends `released`, not
`published`): publish a new patch version instead.

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

Use the `orchestrate-stack` skill for multi-phase work (0.3.0 onwards). It loads the packs named in [`STACK.md`](STACK.md). 0.1.0 and 0.2.0 were built with `build-from-spec`. Either way, the commit gate above replaces the skill's local gate and local-run steps.

## Definition of done

- The step reviewer has no open Critical or Major findings, and GitHub Actions CI is green on the pushed head commit (see
  Commit gate). New behaviour has tests named after the requirement IDs.
- Contract and client are regenerated (if the API changed).
- Migration added and reviewed (if the schema changed).
- Docker Desktop redeploy and smoke test: not run by agents on this machine. They're listed under "Manual checks" in
  PROGRESS.md for Ben.
- SPEC.md is updated if behaviour differs from it.
