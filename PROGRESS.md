# Build progress

Spec: `SPEC.md` · Branch: `build/aide-v1` · Started: 2026-10-07

## Environment
- .NET SDK: 10.0.401 · Node: v24.21.0 (npm 12.2.0) · Docker: 29.8.2 (Docker Desktop) · gh 2.102.0
- Kubernetes: `docker-desktop` context missing at start (2026-10-07); enabled by Ben during phase 0 → present + reachable (node Ready, v1.36.1; kubectl 1.36.1, kustomize 5.8.1)
- Optional tools: kubeconform, oasdiff, actionlint — not installed
- Local app URL: http://localhost:8080 · Dev loop: API :5080, Vite :5173, DB :5432

## Requirements
Spec IDs are used directly (FR-n). NFR rows use the spec section.

| ID | Summary | Spec § | Area | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-1 | Register, sign in/out, cookie session | 3 | both | 2 | todo | — |
| FR-2 | GitHub sign-in and linking | 3 | both | 2 | todo | — |
| FR-3 | First user Admin; admins change roles | 3 | both | 2 | todo | — |
| FR-4 | Personal API tokens (hash stored) | 3 | both | 2 | todo | — |
| FR-5 | Project wizard, defaults, PROJECT_CREATED | 3 | both | 3 | todo | — |
| FR-6 | Projects list: paging, search, filters, sort, membership | 3 | both | 3 | todo | — |
| FR-7 | Edit / delete project (owner) | 3 | both | 3 | todo | — |
| FR-8 | Members and roles, last-owner rule | 3 | both | 3 | todo | — |
| FR-9 | Persisted encrypted Git/LLM/notification settings | 3 | both | 3 | todo | — |
| FR-10 | Git connection test | 3 | both | 3 | todo | — |
| FR-11 | Phase changes only via state machine + transition + event | 3, 4.1 | backend | 1, 4 | todo | — |
| FR-12 | Advance request; gate per approval mode | 3, 4.2 | both | 1, 4 | todo | — |
| FR-13 | Approve/reject gates; rejection re-runs producer | 3, 4.2 | both | 4 | todo | — |
| FR-14 | Escalation gate decisions | 3, 4.5 | both | 4, 10 | todo | — |
| FR-15 | Gate reminders 24h, overdue 72h | 3 | both | 12 | todo | — |
| FR-16 | Versioned artifacts with hash, parent, status | 3, 4.3 | backend | 1, 5 | todo | — |
| FR-17 | Approve/reject drafts; locking | 3, 4.3 | both | 5 | todo | — |
| FR-18 | Human-edited drafts | 3 | both | 5 | todo | — |
| FR-19 | Invalidation cascade | 3, 4.4 | backend | 1, 5 | todo | — |
| FR-20 | Integrity re-check, corrupted, alert, regenerate | 3 | both | 5 | todo | — |
| FR-21 | Artifact history and diff | 3 | both | 5 | todo | — |
| FR-22 | LLM provider abstraction (Anthropic/OpenAI/Ollama/Fake) | 3 | backend | 6 | todo | — |
| FR-23 | Agent run records | 3 | both | 6 | todo | — |
| FR-24 | Versioned prompt templates, MISSING_INPUT | 3 | backend | 6 | todo | — |
| FR-25 | Provider retries, dead letter | 3, 4.9 | backend | 6 | todo | — |
| FR-26 | Token budget | 3 | both | 6 | todo | — |
| FR-27 | Idea refinement rounds with questions | 3 | both | 7 | todo | — |
| FR-28 | Spec generation, 11 sections, retry | 3 | backend | 7 | todo | — |
| FR-29 | Structured plan generation, INVALID_PLAN | 3 | backend | 7 | todo | — |
| FR-30 | Plan approval creates execution steps | 3 | backend | 7 | todo | — |
| FR-31 | Step runner ordering and inputs | 3 | backend | 8 | todo | — |
| FR-32 | Execution Guardian | 3, 4.6 | backend | 1, 8 | todo | — |
| FR-33 | Step retries, blocked, escalation | 3 | backend | 8 | todo | — |
| FR-34 | Commit per step on project branch | 3 | backend | 9 | todo | — |
| FR-35 | GitHub and Local Git providers | 3 | backend | 9 | todo | — |
| FR-36 | PR on completion, advance to review | 3 | backend | 9 | todo | — |
| FR-37 | Pause / resume execution | 3 | both | 8 | todo | — |
| FR-38 | Review cycle: checks + review agent | 3, 4.7 | backend | 1, 10 | todo | — |
| FR-39 | Release decision | 3, 4.5 | backend | 1, 10 | todo | — |
| FR-40 | Optimisation loop | 3 | backend | 10 | todo | — |
| FR-41 | Review page | 3 | frontend | 10 | todo | — |
| FR-42 | Release: merge, tag, idempotent | 3 | both | 11 | todo | — |
| FR-43 | Merge conflict blocks release; retry | 3 | both | 11 | todo | — |
| FR-44 | Rollback | 3, 4.8 | both | 1, 11 | todo | — |
| FR-45 | Rollback rate limit and blocking | 3 | backend | 11 | todo | — |
| FR-46 | Live updates (SignalR) | 3 | both | 5 | todo | — |
| FR-47 | Notifications: in-app + channels | 3 | both | 12 | todo | — |
| FR-48 | Signed single-use approval links | 3 | both | 12 | todo | — |
| FR-49 | Per-channel delivery retry | 3 | backend | 12 | todo | — |
| FR-50 | Activity feed | 3 | both | 4 | todo | — |
| FR-51 | Admin pages, dead-letter retry | 3 | both | 13 | todo | — |
| FR-52 | `aide` CLI | 3 | backend | 13 | todo | — |
| NFR-PERF | p95 < 300 ms reads; live events < 2 s | 8 | both | 14 | todo | — |
| NFR-REL | Job lease expiry re-queue (5 min) | 8, 4.9 | backend | 1, 4 | todo | — |
| NFR-SEC | Cookies, antiforgery, rate limits, Data Protection, no secret logging | 8 | backend | 2–3 | todo | — |
| NFR-OBS | JSON logs, OTel console, ready check incl. migrations | 8 | backend | 0, 2 | partial | health live/ready + JSON logs + OTel (phase 0); project/run/job ids in logs later |
| NFR-A11Y | WCAG 2.2 AA core flows, both themes | 8 | frontend | 2+ | todo | — |
| NFR-TEST | Domain unit tests, endpoint integration tests, Fake+Local E2E | 8 | both | all | todo | — |
| DEP-1 | Docker Desktop k8s deploy at :8080, ns `aide`, db/api/web/db-migrate, aide-data PVC, WS proxy | 9 | deploy | 0, 14 | partial | skeleton deployed, smoke ✓ (phase 0) |
| DEP-2 | Dev seed: admin + demo project (Fake + Local) | 9 | backend | 3 | todo | — |

## Phases
- [x] 0 Walking skeleton (base: 581a8d6) — IDs: DEP-1 (skeleton), NFR-OBS (health)
  - [x] repo-scaffolder (backend gate 5/5, frontend gate 3/3, dev loop ready=200)
  - [x] github-ci-engineer ‖ local-k8s-engineer (skeleton deployed + smoke ✓)
  - [x] gate · review (3 Major, 4 Minor — all fixed) · commit
- [ ] 1 Domain core + base schema — IDs: §4.1–§4.9 rules (FR-11, FR-12, FR-16, FR-19, FR-32, FR-38, FR-39, FR-44 rule parts, NFR-REL)
  - [ ] ef-data-engineer (Identity + core schema, first migration) ‖ aspnet-engineer (pure Aide.Domain rules + worked-example unit tests)
  - [ ] gate · review · commit
- [ ] 2 M1 Auth + app shell — IDs: FR-1–FR-4, NFR-SEC (cookies, antiforgery, login rate limit), NFR-OBS
  - [ ] aspnet-engineer (auth, ProblemDetails, first contract)
  - [ ] vite-react-engineer (shell, theme, nav, login/register, account tokens, generated client)
  - [ ] gate · review · redeploy · commit
- [ ] 3 M1 Projects — IDs: FR-5–FR-10, DEP-2
- [ ] 4 M2 Lifecycle, gates, jobs, events — IDs: FR-11–FR-14, FR-50, NFR-REL (job worker/outbox)
- [ ] 5 M2 Artifacts + live updates — IDs: FR-16–FR-21, FR-46
- [ ] 6 M3 Agent infrastructure — IDs: FR-22–FR-26
- [ ] 7 M3 Refinement, spec, plan — IDs: FR-27–FR-30
- [ ] 8 M4 Execution runner — IDs: FR-31–FR-33, FR-37
- [ ] 9 M4 Git providers, commits, PR — IDs: FR-34–FR-36
- [ ] 10 M5 Review — IDs: FR-38–FR-41, FR-14 (escalation continue)
- [ ] 11 M6 Release + rollback — IDs: FR-42–FR-45
- [ ] 12 M7 Notifications — IDs: FR-47–FR-49, FR-15
- [ ] 13 M8 Admin + CLI — IDs: FR-51, FR-52
- [ ] 14 Integrate & deploy — full-lifecycle E2E (Fake + Local) → redeploy → E2E on k8s, NFR-PERF
- [ ] Final — spec-verifier → fixes → github-ci-engineer → readme-writer

## Decisions
- 2026-10-07 — §11 Q1: GitLab provider is out of v1; the Git provider abstraction stays so it can be added later — Ben's answer — SPEC updated: yes (§11)
- 2026-10-07 — §11 Q2: GitHub Copilot LLM adapter dropped; providers are Anthropic, OpenAI, Ollama and Fake — Ben's answer — SPEC updated: yes (§11)
- 2026-10-07 — §11 Q3: no token streaming in v1; runs show `running` until complete — Ben's answer — SPEC updated: yes (§11)
- 2026-10-07 — §11 Q4: default models per agent are configuration (appsettings `Llm:Defaults`), not code. Anthropic for spec, plan, code generation and review; a cheaper model for refinement. Model IDs confirmed from provider docs when the provider config is built (phase 6) — Ben's answer — SPEC updated: yes (§11)
- 2026-10-07 — §4 domain rules are built as pure code in `Aide.Domain` (phase 1) before any endpoint uses them; milestone M2's "§4.1–4.4 tested" and later §4.5–4.8 rules are therefore front-loaded — Ben's instruction — SPEC updated: no
- 2026-10-07 — xUnit v3 on Microsoft.Testing.Platform (opted in via global.json); Microsoft.NET.Test.Sdk/coverlet dropped — xunit.v3 4.x requires MTP on the .NET 10 SDK — SPEC updated: no
- 2026-10-07 — ESLint replaces create-vite's default oxlint; `overrides` lets openapi-typescript 7.13 (peer TS ^5) run with TS 6 — CLAUDE.md stack — SPEC updated: no
- 2026-10-07 — Dev connection string lives in user-secrets (password chosen in git-ignored `.env`), not appsettings.Development.json — no secrets committed — SPEC updated: no
- 2026-10-07 — `/api/health/ready` is a minimal-API endpoint over HealthCheckService (tag `ready`: DB reachable + no pending migrations) so it appears in the OpenAPI contract — SPEC updated: no

- 2026-10-07 — Deploy applies in two passes (infra + db-migrate Job, then api/web) so a failed migration never replaces the running API — review finding — SPEC updated: no
- 2026-10-07 — PostgreSQL pod runs non-root (uid 999), caps dropped, read-only root FS; api/web non-root read-only — hardening — SPEC updated: no
- 2026-10-07 — `deploy/scripts/validate-manifests.sh` renders the overlay from a temp copy with secrets.env.example, so the gate works on a fresh clone without touching the real secrets.env — SPEC updated: no
- 2026-10-07 — Secret env names (to confirm when the app reads them): Llm__Anthropic__ApiKey, Llm__OpenAI__ApiKey, GitHub__OAuth__ClientId/ClientSecret, Approvals__SigningKey, Smtp__*, DataProtection__ApplicationName/KeysPath (/data/keys) — SPEC updated: no

## Known issues
- kubeconform, oasdiff, actionlint not installed locally — schema validation skipped locally (CI runs kubeconform; actionlint was run via Docker) — install optionally — Ben
- CI not yet run on GitHub (nothing pushed): Testcontainers on ubuntu-latest, SDK 10.0.401 install, Docker cache unverified — verify on first push — orchestrator
- Migration Job logs a harmless "Cannot load library libgssapi_krb5.so.2" (Npgsql Kerberos probe) — ignore — —
