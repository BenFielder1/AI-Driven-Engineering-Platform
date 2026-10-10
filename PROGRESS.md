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
| FR-1 | Register, sign in/out, cookie session | 3 | both | 2 | done | auth endpoints + /login,/register; FR1_* tests; e2e/auth.spec |
| FR-2 | GitHub sign-in and linking | 3 | both | 2 | done | GitHub OAuth (fake handler tests), hidden when unconfigured |
| FR-3 | First user Admin; admins change roles | 3 | both | 2, 13 | done | first-user admin + PATCH /api/admin/users/{id}/role; /admin/users page with confirmation and last-admin 409; FR3_* tests |
| FR-4 | Personal API tokens (hash stored) | 3 | both | 2 | done | /api/me/tokens + bearer scheme + Account page; e2e |
| FR-5 | Project wizard, defaults, PROJECT_CREATED | 3 | both | 3 | done | POST /api/projects + wizard; slug/defaults/PROJECT_CREATED; FR5_* tests |
| FR-6 | Projects list: paging, search, filters, sort, membership | 3 | both | 3 | done | GET /api/projects paging/search/filters/sort; list page; FR6_* tests |
| FR-7 | Edit / delete project (owner) | 3 | both | 3 | done | PATCH/DELETE project (owner); danger zone; FR7_* tests |
| FR-8 | Members and roles, last-owner rule | 3 | both | 3 | done | members endpoints, LAST_OWNER row lock; members tab; FR8_* tests |
| FR-9 | Persisted encrypted Git/LLM/notification settings | 3 | both | 3 | done | git/llm/notification settings, Data Protection-encrypted secrets (incl. Slack/Teams URLs); FR9_* tests |
| FR-10 | Git connection test | 3 | both | 3 | done | POST …/settings/git/test (GitHub fake handler + Local); ok on k8s; FR10_* tests |
| FR-11 | Phase changes only via state machine + transition + event | 3, 4.1 | backend | 1, 4 | done | LifecycleService/Writer: transition + PHASE_CHANGED + follow-up job in one SaveChanges, xmin → CONFLICT; FR11_* tests |
| FR-12 | Advance request; gate per approval mode | 3, 4.2 | both | 1, 4 | done | POST …/phase/advance (state or pending gate, GATE_PENDING); reason kept through gate; lifecycle card; FR12_* tests |
| FR-13 | Approve/reject gates; rejection re-runs producer | 3, 4.2 | both | 4 | done | gates approve/reject (owner), GATE_NOT_PENDING; rejection enqueues producer job (handlers arrive phase 6–7); pending-gates panel; FR13_* tests |
| FR-14 | Escalation gate decisions | 3, 4.5 | both | 4, 8, 10 | done | continue/rollback/accept; step_blocked (phase 8); review reasons low_score/severe_drift/max_cycles/no_improvement/no_optimization_plan, continue = extra cycle, accept = pre_release gate; FR14_* tests |
| FR-15 | Gate reminders 24h, overdue 72h | 3 | both | 12 | done | NotificationSweeper gate timers: one reminder at 24h, GATE_OVERDUE at 72h, never decides; starvation-safe batch; overdue badge; FR15_* tests |
| FR-16 | Versioned artifacts with hash, parent, status | 3, 4.3 | backend | 1, 5 | done | artifact endpoints (latest/history/versions), version+parent+hash+status; FR16_* tests |
| FR-17 | Approve/reject drafts; locking | 3, 4.3 | both | 5 | done | approve/reject (owner), no-op on locked, producer re-run on agent drafts; detail page actions; FR17_* tests |
| FR-18 | Human-edited drafts | 3 | both | 5 | done | POST …/artifacts/{type} with baseVersion (stale → CONFLICT); editor; FR18_* tests |
| FR-19 | Invalidation cascade | 3, 4.4 | backend | 1, 5 | done | cascade + phase fallback + stale gate cancel in one tx; spec v4 example; FR19_* tests |
| FR-20 | Integrity re-check, corrupted, alert, regenerate | 3 | both | 5 | done | every read re-hashes; corrupted + INTEGRITY_ALERT; re-run for current agent version; FR20_* tests |
| FR-21 | Artifact history and diff | 3 | both | 5 | done | Myers diff (collapsed context, 100k-line cap), side-by-side view; FR21_* tests |
| FR-22 | LLM provider abstraction (Anthropic/OpenAI/Ollama/Fake) | 3 | backend | 6 | done | ILlmProvider + typed HttpClients, resolver (project → config), Fake responder seam; FR22_* tests (fake handlers only) |
| FR-23 | Agent run records | 3 | both | 6, 13 | done | AgentRunner run rows (tokens, cost, duration, inputs, prompt version); GET …/runs[/:id]; runs page + drawer; GET /api/admin/runs + /admin/runs; FR23_*/FR51_* tests |
| FR-24 | Versioned prompt templates, MISSING_INPUT | 3 | backend | 6 | done | embedded Prompts/*.md with front-matter version, delimited data tags, MISSING_INPUT before provider call; FR24_* tests |
| FR-25 | Provider retries, dead letter | 3, 4.9 | backend | 6 | done | transient classification, 4 job attempts, run failed + dead letter + RUN_FAILED (incl. reaper path); FR25_* tests (owner notification phase 12) |
| FR-26 | Token budget | 3 | both | 6 | done | TokenBudget rule, BUDGET_EXCEEDED event, Owner raises via PATCH project; budget meter; FR26_* tests (execution start check phase 8) |
| FR-27 | Idea refinement rounds with questions | 3 | both | 7 | done | refinement endpoints, rounds + answers, each round a new draft, REFINEMENT_IN_PROGRESS, cancelled rounds; refinement page; FR27_* tests; verified on k8s |
| FR-28 | Spec generation, 11 sections, retry | 3 | backend | 7 | done | 11-section check, one corrective re-run listing missing sections, MISSING_SECTIONS → no draft; FR28_* tests |
| FR-29 | Structured plan generation, INVALID_PLAN | 3 | backend | 7 | done | aide-plan block + rendered Markdown (round-trip checked), INVALID_PLAN reasons, agent plans reject protected/unsafe paths; plan preview; FR29_* tests |
| FR-30 | Plan approval creates execution steps | 3 | backend | 7 | done | approval creates one pending step per plan step (current max files/step re-checked); GET …/execution/steps with protected flag; FR30_* tests |
| FR-31 | Step runner ordering and inputs | 3 | backend | 8 | done | ExecutionJobHandler: one attempt per job, StepRules next step, inputs incl. declared file content from the working copy; Execution page; FR31_* tests; 4 steps ran on k8s |
| FR-32 | Execution Guardian | 3, 4.6 | backend | 1, 8 | done | all six checks + UNSAFE_PATH (Windows aliases) + `.aide/` never writable; violations stored and shown; FR32_*/S4_6_* tests |
| FR-33 | Step retries, blocked, escalation | 3 | backend | 8 | done | violations fed back, 4th rejection → blocked + step_blocked escalation (continue/rollback; accept invalid); interrupted jobs → rejected; FR33_* tests |
| FR-34 | Commit per step on project branch | 3 | backend | 8, 9 | done | commit per step with subject/body + AIDE-Step-Id trailer, sha/branch/manifest recorded; GitHub push after each step (failure → GIT_PUSH_FAILED, retried); FR34_* tests; verified on k8s (Local) |
| FR-35 | GitHub and Local Git providers | 3 | backend | 8, 9 | done | Local repo on the data volume; GitHub clone/fetch over HTTPS with the token only in per-command env headers, host from config; FR35_* tests (local bare repo + fake API) |
| FR-36 | PR on completion, advance to review | 3 | both | 9 | done | pull_request job: find-or-create PR (description template), Local branch comparison, PULL_REQUEST_OPENED, advance to review with phase re-check; RetryPullRequest; PR section + overview summary; FR36_* tests; verified on k8s (Local) |
| FR-37 | Pause / resume execution | 3 | both | 8 | done | start/pause/resume endpoints (202 ExecutionStateDto), running step finishes, no new step; executionStatus on /state; page controls; FR37_* tests |
| FR-38 | Review cycle: checks + review agent | 3, 4.7 | backend | 1, 10 | done | review job: deterministic checks + code_review agent, §4.7 scoring, report with evidence + optimisation draft; StartReview; FR38_*/S4_7_* tests; verified on k8s |
| FR-39 | Release decision | 3, 4.5 | backend | 1, 10 | done | §4.5 table incl. worked examples; pre_release gate / optimisation loop / escalation; FR39_* tests |
| FR-40 | Optimisation loop | 3 | backend | 10 | done | approved optimisation plan re-enters execution on the same branch, PR reused, next cycle automatic; FR40_* tests; 2-cycle loop on k8s |
| FR-41 | Review page | 3 | frontend | 10 | done | score vs threshold, categories + evidence, accessible SVG trend + table, drift, violations, decision copy; FR41_* tests |
| FR-42 | Release: merge, tag, idempotent | 3 | both | 11 | done | pre_release approval → release job: Local --no-ff merge / GitHub PR merge pinned to head, annotated tag v<N>-aide (suffix on reused versions), released via state machine; release panel; FR42_* tests; verified on k8s |
| FR-43 | Merge conflict blocks release; retry | 3 | both | 11 | done | MERGE_CONFLICT with files, stays releasing, RELEASE_FAILED (notification phase 12); POST /release retry (NOT_RELEASING, RELEASE_IN_PROGRESS); FR43_* tests |
| FR-44 | Rollback | 3 | both | 1, 8, 10, 11 | done | POST /rollback (owner) via RollbackWriter in one transaction with job-row locks; record lists invalidated/reset/requeued/cancelled ids; versions picker + confirmation dialog; FR44_* tests; verified on k8s |
| FR-45 | Rollback rate limit and blocking | 3 | backend | 11 | done | 1/min per project → 429 + Retry-After; blocked by in-progress step/agent run/running or partially applied release with reason details; FR45_* tests; 429 verified on k8s |
| FR-46 | Live updates (SignalR) | 3 | both | 5 | done | /api/hubs/projects, post-commit publisher, origin check; useProjectLive invalidation; FR46_* tests (browser E2E in phase 14) |
| FR-47 | Notifications: in-app + channels | 3 | both | 12 | done | 10 event types → in-app centre (bell, unread count, read/read-all, per-user muted types) + email/Slack/Teams/HMAC-signed webhook via outbox interceptor + notification job; SSRF-safe connector; FR47_* tests |
| FR-48 | Signed single-use approval links | 3 | both | 12 | done | HMAC tokens (hash stored), 72h expiry, single use, Owner + browser session, /approvals/$token confirmation page; tokens redacted from logs; FR48_* tests |
| FR-49 | Per-channel delivery retry | 3 | backend | 12 | done | one delivery row per channel, 5 attempts (1m/5m/30m/2h), sent never re-sent, 60s send cap < 2 min claim lease; FR49_* tests |
| FR-50 | Activity feed | 3 | both | 4 | done | GET …/activity (type filter, keyset `before`, 50/page); activity feed with load more, humanised copy; FR50_* tests |
| FR-51 | Admin pages, dead-letter retry | 3 | both | 13 | done | GET /api/admin/events·runs·dead-letters (filters, paging), POST …/dead-letters/{jobId}/retry (FOR UPDATE, retried + new job, DEAD_LETTER_RETRIED); /admin pages; FR51_* tests |
| FR-52 | `aide` CLI | 3 | backend | 13 | done | Aide.Cli (System.CommandLine, PackAsTool `aide`): full FR-52 command set, --json, exit codes 0/1/2/3/4; Aide.Cli.Tests FR52_* (fake handler) |
| NFR-PERF | p95 < 300 ms reads; live events < 2 s | 8 | both | 14 | todo | — |
| NFR-REL | Job lease expiry re-queue (5 min) | 8, 4.9 | backend | 1, 4 | done | JobWorker: SKIP LOCKED claim, continuous slots, lease renewal, reaper, 2/8/32 s backoff → dead letter; NFR_REL_* tests |
| NFR-SEC | Cookies, antiforgery, rate limits, Data Protection, no secret logging | 8 | backend | 2–3 | todo | — |
| NFR-OBS | JSON logs, OTel console, ready check incl. migrations | 8 | backend | 0, 2 | partial | health live/ready + JSON logs + OTel (phase 0); project/run/job ids in logs later |
| NFR-A11Y | WCAG 2.2 AA core flows, both themes | 8 | frontend | 2+ | todo | — |
| NFR-TEST | Domain unit tests, endpoint integration tests, Fake+Local E2E | 8 | both | all | todo | — |
| DEP-1 | Docker Desktop k8s deploy at :8080, ns `aide`, db/api/web/db-migrate, aide-data PVC, WS proxy | 9 | deploy | 0, 14 | partial | skeleton deployed, smoke ✓ (phase 0) |
| DEP-2 | Dev seed: admin + demo project (Fake + Local) | 9 | backend | 3 | done | DevSeeder (admin@aide.local + Fake/Local demo, marker event); on in dev + k8s overlay; DEP2_* tests; verified on k8s |

## Phases
- [x] 0 Walking skeleton (base: 581a8d6) — IDs: DEP-1 (skeleton), NFR-OBS (health)
  - [x] repo-scaffolder (backend gate 5/5, frontend gate 3/3, dev loop ready=200)
  - [x] github-ci-engineer ‖ local-k8s-engineer (skeleton deployed + smoke ✓)
  - [x] gate · review (3 Major, 4 Minor — all fixed) · commit
- [x] 1 Domain core + base schema (base: 45fcdae) — IDs: §4.1–§4.9 rules (FR-11, FR-12, FR-16, FR-19, FR-32, FR-38, FR-39, FR-44 rule parts, NFR-REL)
  - [x] aspnet-engineer (pure Aide.Domain rules + worked-example unit tests — 713 unit tests)
  - [x] ef-data-engineer (Identity + full §5 schema, migration CoreSchema)
  - [x] gate · review (1 Major, 6 Minor — all fixed) · redeploy · commit
- [x] 2 M1 Auth + app shell (base: 55bf373) — IDs: FR-1–FR-4, NFR-SEC (cookies, antiforgery, login rate limit), NFR-OBS
  - [x] aspnet-engineer (auth, ProblemDetails, first contract — 884 tests)
  - [x] vite-react-engineer (shell, theme, nav, login/register, account tokens, generated client)
  - [x] gate · review (1 Major, 4 Minor — all fixed) · redeploy · E2E on k8s 4/4 · commit
- [x] 3 M1 Projects (base: 6d96fc9) — IDs: FR-5–FR-10, DEP-2
  - [x] aspnet-engineer (projects, members, settings, git test, LLM catalog, dev seed) ‖ vite-react-engineer (list, wizard, overview, settings tabs) — resumed from an uncommitted tree; 5 failing integration tests fixed
  - [x] gate · review (0 Critical, 5 Major, 6 Minor — all Major fixed; Minors fixed except SSRF/model IDs → Known issues) · redeploy (smoke ✓, seed ✓, Local git test ✓) · E2E on k8s 4/4 · commit
- [x] 4 M2 Lifecycle, gates, jobs, events (base: a50f6c1) — IDs: FR-11–FR-14, FR-50, NFR-REL (job worker/outbox)
  - [x] aspnet-engineer (advance, gates, escalation decisions, activity, job worker/outbox) ‖ vite-react-engineer (lifecycle card, gates panel/dialogs, gate history, activity feed) — resumed from an uncommitted tree; 2 lint errors fixed
  - [x] gate · review (0 Critical, 1 Major, 3 Minor — Major + 2 Minor fixed; gate list paging → Known issues) · redeploy (smoke ✓) · commit
- [x] 5 M2 Artifacts + live updates (base: 6841646) — IDs: FR-16–FR-21, FR-46
  - [x] aspnet-engineer (contract stubs) → aspnet-engineer (artifacts, cascade, integrity, diff, SignalR hub + publisher) ‖ vite-react-engineer (artifacts pages, diff view, editor, useProjectLive)
  - [x] gate · review (0 Critical, 1 Major, 6 Minor + flaky tests — all fixed) · redeploy (smoke ✓, CSP ✓, hub foreign origin 403 ✓) · E2E on k8s 4/4 · commit
- [x] 6 M3 Agent infrastructure (base: 64010e6) — IDs: FR-22–FR-26
  - [x] aspnet-engineer (providers, prompts, runner, runs endpoints; resumed once after a rate-limit stop) → vite-react-engineer (runs page + drawer, budget card)
  - [x] gate · review (0 Critical, 1 Major, 7 Minor — Major + 5 Minor fixed; budget soft cap and OpenAI pricing logged) · redeploy (smoke ✓) · E2E on k8s 4/4 · commit
- [x] 7 M3 Refinement, spec, plan (base: e850d35 → e6f6531) — IDs: FR-27–FR-30
  - [x] aspnet-engineer (refinement, spec/plan agents, plan validation, steps on approval) ‖ vite-react-engineer (refinement page, plan steps) — resumed from an uncommitted tree that didn't build (unfinished AgentRunner change, broken MSW mock types); fixed forward
  - [x] gate · review (0 Critical, 3 Major, 5 Minor — all Major + 4 Minor fixed; error-table Minor logged as a Decision) · redeploy (smoke ✓, refinement round → draft on k8s ✓) · commit
- [x] 8 M4 Execution runner (base: e6f6531) — IDs: FR-31–FR-33, FR-37, FR-34/FR-35 (Local part)
  - [x] aspnet-engineer (contract stubs) → aspnet-engineer (runner, Guardian wiring, retries/escalation, pause/resume, Local working copy + commits) ‖ vite-react-engineer (Execution page) → vite-react-engineer (wired to final contract)
  - [x] gate · review (0 Critical, 5 Major, 5 Minor — all fixed; backend fixer resumed once after a usage-limit stop) · redeploy (smoke ✓, full Fake+Local run to 4 completed steps with commits on k8s ✓) · commit
- [x] 9 M4 Git providers, commits, PR (base: 32dbd64) — IDs: FR-34–FR-36
  - [x] aspnet-engineer (GitHub clone/push, PR job, Local comparison, advance to review) → vite-react-engineer (PR section, overview summary, activity copy)
  - [x] gate · review (0 Critical, 2 Major, 4 Minor + enum casing — all fixed) · redeploy (smoke ✓, Fake+Local project reached review with a 4-commit comparison on k8s ✓) · commit
- [x] 10 M5 Review (base: bd10238) — IDs: FR-38–FR-41, FR-14
  - [x] aspnet-engineer (contract stubs) → ef-data-engineer (migration ReviewReportDetails) ‖ vite-react-engineer (Review page) → aspnet-engineer (review job, decisions, loop; resumed once after a usage-limit stop)
  - [x] gate · review (1 Critical, 3 Major, 5 Minor — all fixed) · redeploy (migration applied, smoke ✓, 2-cycle review loop → pre_release gate on k8s ✓) · commit
- [x] 11 M6 Release + rollback (base: bf8229d) — IDs: FR-42–FR-45
  - [x] aspnet-engineer (contract stubs) → ef-data-engineer (migration ReleaseAndRollbackDetails) ‖ vite-react-engineer (release panel, rollback dialog/history) → aspnet-engineer (release job, rollback endpoint)
  - [x] gate · review (0 Critical, 1 Major, 4 Minor + tag-reuse gap — all fixed; migration rebuilt as 20261009195805) · redeploy (migration applied, smoke ✓, release → v7-aide tag + merge, rollback + 429 on k8s ✓) · commit
- [x] 12 M7 Notifications (base: 01ebe33 → 10b774a) — IDs: FR-47–FR-49, FR-15
  - [x] aspnet-engineer (domain rules, outbox, dispatcher/sweeper, channels, approval links) → ef-data-engineer (migration NotificationDetails) ‖ vite-react-engineer (bell, notifications page, preferences, approval page) — resumed from an uncommitted tree; gate green except 2 flaky frontend tests
  - [x] gate · review (0 Critical, 2 Major, 6 Minor — all fixed; frontend flake root-caused: lazy route chunks under Vitest) · redeploy (migration applied, smoke ✓, notifications + approval-link 404 + token redaction checked on k8s) · commit
- [x] 13 M8 Admin + CLI (base: 32634f0 → f2b3172) — IDs: FR-51, FR-52, FR-3 (admin UI), FR-23 (admin runs)
  - [x] aspnet-engineer (admin API, contract) → vite-react-engineer (admin pages) ‖ aspnet-engineer (aide CLI)
  - [x] gate · review (0 Critical, 0 Major, 7 Minor — all fixed; migration AdminListIndexes) · redeploy (migration applied, smoke ✓, non-admin 403, CLI login/projects/init/status/logs exit 0 and 404 → 1 on k8s) · commit
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

- 2026-10-07 — Missing credentials never block: no GitHub OAuth config → "Continue with GitHub" hidden and /api/auth/github* return 404 (flow tested with a fake OAuth handler); no LLM keys → Anthropic/OpenAI shown "not configured", new projects and dev seed default to Fake; no SMTP → email skipped with a logged warning, other channels still work. README lists every real-use setting — Ben's instruction — SPEC updated: no

- 2026-10-07 — Job retries: 4 attempts total (1 + 3 retries, per FR-25 example), backoff 2s/8s/32s; non-transient failures fail immediately; an expired lease counts as a failed attempt — §4.9/FR-25 wording — SPEC updated: no
- 2026-10-07 — FR-33: attempts 1–3 rejected → retry; the 4th rejection blocks the step and creates an escalation gate (follows the acceptance example) — SPEC wording ambiguous — SPEC updated: no
- 2026-10-07 — `execution → review` increments the version; only `review → execution` (review cycle) keeps it — §4.1 — SPEC updated: no
- 2026-10-07 — Rollback may restore the same phase it is in (e.g. back to an earlier execution version); §4.1 says "earlier phase" — otherwise such rollbacks are impossible — SPEC updated: no
- 2026-10-07 — Invalidation fallback is its own transition kind (earlier phase, status follows phase, version unchanged); reset steps count as produced by execution — §4.4 — SPEC updated: no
- 2026-10-07 — Cascade and rollback invalidate drafts as well as locked versions (§4.3 lists only locked → invalidated) — a stale draft shouldn't stay approvable — SPEC updated: no
- 2026-10-07 — Escalation gates are decided with continue/rollback/accept; "reject" on an escalation gate is INVALID_DECISION; rollback requires targetVersion — FR-14 — SPEC updated: no
- 2026-10-07 — Rollback to v1 with no recorded transition restores refinement; check order INVALID_TARGET_VERSION → ROLLBACK_BLOCKED → RATE_LIMITED (with retry-after) — SPEC updated: no
- 2026-10-07 — Review deterministic penalties: secret = critical, missing spec endpoint = major, dependency not on allowlist = major — §4.7 silent on severity — SPEC updated: no
- 2026-10-07 — Guardian additions: UNSAFE_PATH (absolute, `..`, drive letters) error; protected paths matched case-insensitively; secret values never echoed — security — SPEC updated: no
- 2026-10-07 — Next-step selection retries a `rejected` step before any new pending step; nothing is picked while a step is in progress (§4.9 one execution job per project) — SPEC updated: no
- 2026-10-07 — Full §5 schema is created in phase 1 (one baseline migration), not split per phase — fewer serial migration steps, the model is fully specified — SPEC updated: no

- 2026-10-07 — Any path with a `.git` segment is UNSAFE and rejected by plan validation and the Guardian even when declared (§4.6 says protected paths are allowed "unless declared") — prompt-injected plans could otherwise write git hooks/config and get code execution in the API pod — SPEC updated: no
- 2026-10-07 — Rollback into a `release`-phase version is refused (INVALID_TARGET_VERSION) — it would leave the project in an unrecoverable `release/rolled_back` state — SPEC updated: no
- 2026-10-07 — Review cycle: on `review_required` the review job creates the optimisation_plan draft and the phase stays `review`; approving that plan applies `review → execution` (requires plan locked, ≥1 step, working copy) — reconciles FR-39 ("re-enters execution") with FR-40 ("approved like any artifact") — SPEC updated: no
- 2026-10-07 — Guardian rejects duplicate normalised produced paths — ambiguous content otherwise — SPEC updated: no
- 2026-10-07 — One-click approval links only for spec_approval/plan_approval/pre_release gates; escalation notifications link to the project page (escalation needs continue/rollback/accept + target) — SPEC updated: no
- 2026-10-07 — Review state persisted as `projects.review_cycle` (reset to 0 by rollback) and `projects.extra_review_cycles` (escalation "continue" adds one; effective max = max_review_cycles + extra); `compliance_reports.project_version` so only the current version's decision counts for release entry — §4.8 "review state is cleared" — SPEC updated: no
- 2026-10-07 — Dead-letter retry (FR-51): the dead job becomes `retried` (new JobStatus value) and a new queued job with the same payload is created — §5 job status list lacked `retried` — SPEC updated: no
- 2026-10-07 — DB guard for §4.9 "one execution job per project": partial unique index on jobs(project_id) where type='execution' and status in (queued, running) — SPEC updated: no
- 2026-10-07 — Email uniqueness via citext column + unique index (plus Identity's normalized_email index); all tables plural snake_case; POCO entities in Aide.Domain/Entities, AppUser in Infrastructure — SPEC updated: no
- 2026-10-07 — Extra columns beyond §5: job.locked_at/completed_at, project.last_rollback_at, agent_run.status/completed_at/output, approval_gate.target_version, execution_step.branch_name, compliance_report.escalation_reason, release.conflicting_files, project_git_settings.pull_request_url/number, notification_delivery.project_id/created_at/sent_at, created_at on api_tokens/project_members/approval_links — SPEC updated: no

- 2026-10-07 — Password policy: ≥ 8 chars with a letter and a digit, no symbol requirement; login lockout after 10 failures for 5 min (still INVALID_CREDENTIALS) — SPEC silent — SPEC updated: no
- 2026-10-07 — Auth cookie `aide.auth` SecurePolicy defaults to SameAsRequest (configurable `Auth:Cookie:SecurePolicy`) because the k8s app is served over http://localhost:8080; §8 says Secure — local-only deployment — SPEC updated: no
- 2026-10-07 — Antiforgery: double-submit `XSRF-TOKEN` cookie + `X-XSRF-TOKEN` header for cookie-authenticated mutations; failure → 403 `ANTIFORGERY` (new error code); bearer-token requests exempt — §8 — SPEC updated: no
- 2026-10-07 — GitHub sign-in with an email matching an existing *unlinked* account is not auto-linked (redirect `/login?error=GITHUB_AUTH_FAILED&reason=link_required`); user signs in and links from Account. Old stack auto-linked by email (account-takeover risk). GitHub callback errors redirect with `reason=` rather than returning 400 — SPEC updated: no
- 2026-10-07 — Last Admin can't be demoted (409 CONFLICT); first-user-Admin made race-safe with an advisory lock; revoking a token twice is 204; `GET /api/auth/providers` (new) tells the SPA whether GitHub is configured; `GET /api/admin/users` (new) for the admin page — SPEC updated: no
- 2026-10-07 — Domain enums serialise as snake_case strings on the wire (e.g. `admin`, `spec_approval`) — matches stored values and §5 — SPEC updated: no

- 2026-10-07 — API tokens can't list/create/revoke tokens or change system roles (BrowserSession policy → 403); an admin token can still read admin lists — a leaked token must not be able to mint a replacement — SPEC updated: no
- 2026-10-07 — 405/415 responses carry errorCode VALIDATION; GitHub create racing an existing email → reason=link_required; concurrent link of the same GitHub id → reason=already_linked — SPEC updated: no
- 2026-10-07 — A failed GitHub *link* lands on /account?error=… (the SPA forwards from /login) — SPEC updated: no

- 2026-10-08 — Validation errors from both ASP.NET's built-in validation and domain rules use camelCase wire keys (`name`, `git.repoUrl`, `channels[0].target`) — consistency with the wire format — SPEC updated: no
- 2026-10-08 — Project slugs fold Latin-1/Latin Extended-A with an explicit table (ß→ss, æ→ae, œ→oe…) instead of Unicode normalisation, because the app runs with InvariantGlobalization — SPEC updated: no
- 2026-10-08 — Slack/Teams incoming-webhook URLs are secrets: Data Protection-encrypted, sent in `secret`, returned only as a host-masked `target` + `hasSecret`, kept/replaced by channel id. Old stack stored them as plain targets — NFR-SEC — SPEC updated: no
- 2026-10-08 — Switching a project's Git provider to local drops the saved GitHub token; the Local connection-test failure reason omits the server workspace path (logged only) — security — SPEC updated: no
- 2026-10-08 — Dev seed (DEP-2) records a `DEMO_PROJECT_SEEDED` event; check + create run in one transaction under the advisory lock, so a deleted demo project is never recreated. Seed is gated only by `Seed:Enabled` (true in appsettings.Development.json and the k8s local overlay); the generated admin password is logged once at Warning — SPEC updated: no
- 2026-10-08 — LLM config shape is `Llm:DefaultProvider` + `Llm:Providers:{Anthropic|OpenAI|Ollama|Fake}:{ApiKey,BaseUrl,DefaultModel,Models:{<agent>}}` (supersedes the `Llm:Defaults` wording in §11 Q4 and the earlier secret-name list: env names are `Llm__Providers__Anthropic__ApiKey`, `Llm__Providers__OpenAI__ApiKey`, `Llm__Providers__Ollama__BaseUrl`) — SPEC updated: no
- 2026-10-08 — API image installs `git` (Local Git provider) with HOME=/tmp and GIT_CONFIG_NOSYSTEM=1 for the non-root, read-only-root-FS pod — SPEC updated: no
- 2026-10-08 — OpenAPI descriptions are line-ending-normalised (LF) by a schema/operation transformer so api.json generated on Windows matches CI — SPEC updated: no
- 2026-10-08 — A gated advance keeps the user's reason in the GATE_CREATED payload; the transition recorded on approval reads "<reason> (<type> gate approved[: <comment>])" (previously the bare approval comment) — FR-11 audit — SPEC updated: no
- 2026-10-08 — Job worker fills free slots continuously and renews its lease every LeaseDuration/3; a job whose lease is lost is cancelled and its outcome discarded — review finding (long LLM runs) — SPEC updated: no
- 2026-10-08 — Activity feed text is built client-side from event type + payload with the UI label maps; the server `summary` is the fallback — SPEC updated: no
- 2026-10-08 — FR-20 "integrity admin alert" is an `INTEGRITY_ALERT` event (category `integrity`) — §5 has no alerts table; admin events page lists it — SPEC updated: no
- 2026-10-08 — Approving a new artifact version cancels stale pending gates (GATE_CANCELLED): all of them on phase fallback, those whose artifact was invalidated, those holding an older version of the type; the Owner re-requests advance. Gates are never re-pointed (decisions final, §4.2) — SPEC updated: no
- 2026-10-08 — Drafts allowed from the type's producing phase onwards; optimisation plan only in review; none while releasing. A version without created_by_user_id is agent-authored. A cascade that resets steps also cancels queued execution jobs — SPEC updated: no
- 2026-10-08 — Corrupted artifacts always raise an alert; the producing agent re-runs only for the current version (open draft or highest locked) when agent-authored; marked via conditional update (one alert/job under concurrent reads) — SPEC updated: no
- 2026-10-08 — Diff: Myers, 3 lines of context with `skipped` collapse entries (new DiffLineKind), refuses > 100,000 total lines with VALIDATION — DoS — SPEC updated: no
- 2026-10-08 — Draft create takes optional `baseVersion`; a stale base → 409 CONFLICT (details latestVersion) so concurrent edits don't silently supersede — SPEC updated: no
- 2026-10-08 — Live events published by an EF interceptor after save/commit (never on rollback); in-process groups → single API replica (a backplane would be needed to scale); hub join subscribes then checks membership; removed members are dropped — SPEC updated: no
- 2026-10-08 — /api/hubs requires a matching Origin (scheme+host, X-Forwarded-Host, or `Live:AllowedOrigins`); cookie WS upgrades without Origin are refused (CSWSH under SameSite=Lax on localhost) — NFR-SEC — SPEC updated: no
- 2026-10-08 — Markdown never loads remote images (rendered as a link); nginx sends a strict CSP and proxies with `Host $http_host` — NFR-SEC (prompt-injected content) — SPEC updated: no
- 2026-10-08 — Adding a value to a response enum (EventCategory `integrity`, DiffLineKind `skipped`) is treated as additive: the only client is the generated SPA client, regenerated in the same change — SPEC updated: no
- 2026-10-08 — Default models confirmed from provider docs (platform.claude.com/docs/en/models/overview, developers.openai.com/docs/models): Anthropic `claude-opus-5-5` for spec/plan/code/review, `claude-sonnet-5-5` for refinement; OpenAI `gpt-6-astra`, `gpt-6.1-sol` for refinement; Ollama `qwen2.5-coder`; Fake `fake-1` — §11 Q4 — SPEC updated: no
- 2026-10-08 — Cost estimate from `Llm:Pricing:<model>` (opus 4/20, sonnet 2/10, haiku 0.10/0.50 USD/MTok); unpriced models (OpenAI, Ollama, Fake) report 0 until configured — OpenAI docs gave no prices — SPEC updated: no
- 2026-10-08 — One run row per job: run id = job id, or a v8 GUID from job id + run key; a redelivered succeeded run returns stored output; RUN_STARTED carries `jobId` so job-end (complete, permanent failure, dead letter, lease reaper) fails runs left `running` — FR-25 — SPEC updated: no
- 2026-10-08 — Budget refusal writes BUDGET_EXCEEDED and creates no run; MISSING_INPUT / PROVIDER_NOT_CONFIGURED create a failed run with 0 attempts. The budget is a soft cap checked at run start (concurrent runs of one project can overshoot slightly; the token count itself is atomic) — SPEC updated: no
- 2026-10-08 — Transient = timeouts, connection errors, 408/425/429/5xx; provider error text is redacted (key-like tokens) and capped, 401/403 get a fixed message; project API keys reject whitespace/control chars; a project key is used only when its settings name that provider — NFR-SEC — SPEC updated: no
- 2026-10-08 — Prompt values are wrapped in data tags and closing tags inside values are neutralised (prompt-injection hardening); integer inputs stay inline — SPEC updated: no
- 2026-10-08 — LLM base URLs are system config only (no per-project base URL → no SSRF); Ollama defaults to host.docker.internal:11434 in containers — SPEC updated: no

- 2026-10-09 — New error codes/API for phase 7 beyond §6: REFINEMENT_IN_PROGRESS (409), INVALID_PLAN as 400 with per-step reasons (draft create/approve), MISSING_SECTIONS and INVALID_OUTPUT as run/job failure codes; GET /api/projects/{id}/refinement, POST …/refinement/run, GET …/execution/steps (optional planType) — FR-27–FR-30 needed them — SPEC updated: no
- 2026-10-09 — Plan structure is stored inside the artifact Markdown as a fenced `aide-plan` JSON block plus the rendered Markdown (no separate plan table); render must round-trip and fences inside task/instructions are neutralised — FR-29 "stores the structure and renders it" — SPEC updated: no
- 2026-10-09 — Agent drafts are saved in their own transaction after the run row/tokens commit; idempotent per run id; a save conflict is a transient retry that reuses the succeeded run (no second provider call); the save bumps the project row so a concurrent rollback/fallback forces a re-check — review finding — SPEC updated: no
- 2026-10-09 — FR-28 corrective re-run uses its own run key (`corrective`) and lists the missing sections; a second miss fails the job with MISSING_SECTIONS and creates no draft — SPEC updated: no
- 2026-10-09 — Agent-generated plans may not declare protected paths (§4.6 list: .github/workflows, .aide/, .env*, lockfiles…) — rejected with a reason so the corrective run can fix it; Owner-edited plans may still declare them per §4.6. Prompt-injected specs could otherwise grant the code agent CI-workflow write access — security — SPEC updated: no
- 2026-10-09 — Plan approval re-validates against the project's current max_files_per_step — SPEC updated: no
- 2026-10-09 — Paths with control characters, line breaks or backticks are UNSAFE everywhere (plan validation and Guardian); runs of ≥3 backticks/tildes in plan title/task/instructions are backslash-escaped in the rendered Markdown and every render is checked to round-trip — review finding (fence injection) — SPEC updated: no
- 2026-10-09 — Approving a draft created at a project version above the current one (stale after rollback/fallback) → 409 ARTIFACT_NOT_DRAFT; execution steps expose `protected` per declared file so the UI flags it — SPEC updated: no
- 2026-10-09 — A refinement round whose job was skipped or ended without a draft is reported `cancelled` with a reason (not `succeeded` with no output) — SPEC updated: no

- 2026-10-09 — Phase 8 includes the Local-provider parts of FR-34/FR-35 (prepare working copy + branch, write, commit per step) so execution works end to end with Fake + Local; GitHub clone/push, PR and advance to review are phase 9. GitHub projects can't enter execution until then — sequencing — SPEC updated: no
- 2026-10-09 — Entering execution (advance or plan_approval gate approval) prepares the working copy first, satisfying §4.1's "working copy prepared" precondition — SPEC updated: no
- 2026-10-09 — One execution job = one step attempt; the next job is enqueued in the same transaction as the step outcome unless paused, blocked or done. Redelivery finds the step's commit by an `AIDE-Step-Id` commit trailer (no second commit/attempt) and reuses a succeeded run (no second provider call) — §4.9 idempotency — SPEC updated: no
- 2026-10-09 — Branch short id = first 8 hex chars of the project id (old stack); project rules = project limits + `.aide/rules.md` from the working copy if present; the Local repo is the working copy itself, created with an empty initial commit — SPEC silent — SPEC updated: no
- 2026-10-09 — An unparseable code-generator reply is a rejected attempt (INVALID_OUTPUT violation). A blocked step halts all execution. A provider failure or budget refusal mid-step leaves the step in_progress; the next start continues it — SPEC silent — SPEC updated: no
- 2026-10-09 — Escalation gate for a blocked step: reason `step_blocked`, step id/number carried in the GATE_CREATED payload and exposed on GateDto (no column). "continue" resets blocked steps to pending with attempt 0 and queues a job; "accept" has no effect in execution (UI hides it) — FR-14/FR-33 — SPEC updated: no
- 2026-10-09 — Git is run as argument lists with hooks, fsmonitor, system/global config and credential helpers disabled, inherited GIT_* env stripped and identity per command; writes can't escape the working copy or follow links — NFR-SEC — SPEC updated: no
- 2026-10-09 — New events (category `step`): EXECUTION_STARTED/PAUSED/RESUMED/COMPLETED, STEP_UNBLOCKED; ProjectStateDto.executionStatus, ExecutionStepDto.commitUrl/fileManifest added — SPEC updated: no
- 2026-10-09 — GitHub token travels only as a per-command `http.<base>.extraHeader` via GIT_CONFIG_KEY_n/VALUE_n env (never in .git/config, remote URL, argv, logs, events or errors); the Git host comes only from system config (`GitHub:GitBaseUrl`, `GitHub:ApiBaseUrl`), the project repo URL supplies owner/repo only — NFR-SEC/SSRF — SPEC updated: no
- 2026-10-09 — A failed push after a step commit doesn't fail the step: STEP_COMPLETED.pushed=false + GIT_PUSH_FAILED {reason, transient}; the next step's push or the PR job pushes again (old stack retried the whole step job) — SPEC updated: no
- 2026-10-09 — FR-36 PR is opened by a `pull_request` job queued when the last step completes (or on a manual execution → review advance): push, find-or-create the PR (422 race → re-find), Local records the branch comparison, store number/URL, PULL_REQUEST_OPENED (with planArtifactId), and advance to review via LifecycleService in one transaction. Description is a deterministic template (spec summary, plan, step table, files; mentions/HTML neutralised), not an agent call — as in the old stack. 5xx/429/rate-limited 403 retried; 401/404/422/rejected push permanent → PULL_REQUEST_FAILED — SPEC updated: no
- 2026-10-09 — Changing a project's Git provider or repo URL clears the stored PR and writes a PULL_REQUEST_FORGOTTEN marker event that /state, GET /git/pull-request and the "PR recorded for this plan" check honour — SPEC updated: no
- 2026-10-09 — The PR job re-checks the phase after the slow git/GitHub work (AdvanceAsync takes `expectedFrom`); a job superseded by a rollback/new plan records nothing and ends succeeded (logged skipped); a mid-job Git settings change retries it. PULL_REQUEST_OPENED is written once per plan — review finding — SPEC updated: no
- 2026-10-09 — New `POST /api/projects/{id}/git/pull-request` (RetryPullRequest, writer, 202) re-queues the PR job when no PR is recorded for the current plan (execution with all steps completed, or review); any Git settings change does the same automatically. Prepare failures (no token, bad URL, 401/404, missing default branch) are permanent — SPEC updated: no
- 2026-10-09 — PR description neutralises issue references (`#n`, `owner/repo#n`, `GH-n`) with U+200D so agent text can't link/close issues; the file list fence is longer than any backtick run; a reused open PR gets its title/body PATCHed to the current description (permanent PATCH failure logged and ignored) — SPEC updated: no
- 2026-10-09 — compliance_reports gains `optimization_plan_artifact_id` (FK, set null) and a `threshold` snapshot (migration ReviewReportDetails); category scores store agentScore, penalty and evidence {file, reason}; findings store source (deterministic|agent) and evidence — FR-41 evidence/trend — SPEC updated: no
- 2026-10-09 — Review job: raises review_cycle once per job (a failed review keeps its cycle, REVIEW_FAILED), cancels pending pre_release/escalation gates from the previous decision, reads the committed branch within fixed limits (changed files first), runs deterministic checks + code_review agent (one corrective re-run), saves report + optimisation draft + gate + REVIEW_COMPLETED in one transaction; redelivery-idempotent — SPEC updated: no
- 2026-10-09 — Only deterministic findings carry §4.7 penalties; the agent's own violations are reported without one. The dependency-allowlist check is skipped when the spec has no Dependencies list. Row 4's previous score is the previous cycle's report at or below the current version — §4.7/§4.5 silent — SPEC updated: no
- 2026-10-09 — An optimisation plan is drafted for review_required and escalation_required (if it has steps), never release_eligible; rejecting/corrupting it revises the plan for the same cycle (report kept, re-pointed). Approving it queues execution unless paused; completion reuses the PR (description updated) and returns to review, which starts the next cycle (FR-40) — SPEC updated: no
- 2026-10-09 — release_eligible always creates a pre_release gate (every approval mode). Escalation in review: continue = one extra cycle (re-enter execution via the draft, or queue a new review if no plan); accept = pre_release gate — FR-14/§4.2 — SPEC updated: no
- 2026-10-09 — A review cycle re-enters execution only with the latest current-timeline report's optimisation plan (its draft or a later version) when locked with steps left; escalation continue with an unapproved draft grants the extra cycle and waits for approval; with no plan it queues a new review. An execution job with nothing to run and every step completed runs the completion hook (no project can sit in execution without a job) — review finding (Critical) — SPEC updated: no
- 2026-10-09 — Rollback and cascade fallback out of review clear review_cycle and extra_review_cycles. The "current review timeline" for latest/drift/trend/state/decisions is project_version ≤ current AND created_at > last_rollback_at; after a cascade fallback (no rollback timestamp) earlier reports still show in the trend — §4.8 — SPEC updated: no
- 2026-10-09 — review_required without optimisation steps gets one corrective re-run, then escalates with new reason `no_optimization_plan`; §4.5 row 4's previous score is the latest earlier-cycle report in the timeline (a failed cycle doesn't skip the row); trend points carry their report's threshold — SPEC updated: no
- 2026-10-09 — Lines the secret scan flags are redacted (`[REDACTED:<pattern>]`, line numbers kept) before code is sent to the review agent — NFR-SEC — SPEC updated: no
- 2026-10-09 — Migration ReleaseAndRollbackDetails: releases.error renamed error_message (+ attempt, completed_at, error_code); rollback_events gains restored_phase and requeued/cancelled-gate/cancelled-job id arrays (backfilled from ROLLBACK_COMPLETED payloads) instead of reading them from events — SPEC updated: no
- 2026-10-09 — Release: approving pre_release enters release/releasing and queues the release job in one transaction. Local merges with `--no-ff` in the working copy (conflict → files from diff-filter=U, abort, branch restored); GitHub merges the PR via the API pinned to the head sha (method `GitHub:ReleaseMergeMethod`, default merge) and pushes an annotated tag with the token in env headers. The tag uses the version before completion (approve at v6 → `v6-aide` → released at v7). An existing tag containing the branch is a no-op. Conflicts and permanent failures end the job and are recorded on the release row (MERGE_CONFLICT, TAG_CONFLICT, GIT_ERROR, RELEASE_CANCELLED) — SPEC updated: no
- 2026-10-09 — Released and releasing projects may roll back to an earlier non-release version; the branch, tags and release history are kept. A running release job blocks rollback (ROLLBACK_BLOCKED); a queued one is cancelled (RELEASE_CANCELLED). POST /release is also allowed when no attempt exists at the current version and no job is active (a lost job can't strand the project). Rollback reason is trimmed (blank → VALIDATION); GET /versions lists versions up to the current one with a recorded phase — SPEC updated: no
- 2026-10-09 — Re-release after a rollback reuses a version number (§4.8): a tag of this version that already contains the branch head is reused (alreadyTagged, no-op success); otherwise the first free `v<N>-aide`, `v<N>-aide.2`, `.3`… is created and recorded. TAG_CONFLICT is no longer produced. Release uniqueness is (project_id, project_version, attempt) instead of one succeeded release per version — orchestrator (spec gap; FR-42 idempotence kept) — SPEC updated: no
- 2026-10-09 — Rollback locks the project's queued/running job rows (FOR UPDATE) inside its transaction; a running release job, or a queued retry whose attempt already merged, → ROLLBACK_BLOCKED and nothing is written. The release job re-checks phase/attempt with the project row touched just before merging. The GitHub merge is pinned to the head AIDE fetched and checked; a moved PR head is a transient failure. ROLLBACK_BLOCKED carries `details: ["reason: step_in_progress|agent_run_in_progress|release_running|release_partially_applied"]` — review finding — SPEC updated: no
- 2026-10-09 — rollback_events.restored_phase is nullable in the DB for this release (old pods can write during the deploy window), required in the model; readers fill NULLs like the backfill. A later contract migration must re-run the backfill and set NOT NULL — SPEC updated: no
- 2026-10-09 — GET …/execution/steps without planType/planVersion returns the current plan (newest locked plan of either type) — closes the phase 8 known issue — SPEC updated: no
- 2026-10-09 — Unsafe paths also include Windows aliases: `.git` with trailing dots/spaces, `~<digit>` short names, `:` and `<>"|?*`, device names (CON/NUL/AUX/PRN/COM/LPT), segments ending in dot/space; resolved paths must stay under the root and outside `<root>/.git`; git runs with an allow-listed environment (not the API's) — review finding (RCE via .git/config on Windows) — SPEC updated: no
- 2026-10-09 — Nothing under `.aide/` is ever writable by agents (Guardian error even if declared) and no plan may declare it; humans edit `.aide/rules.md` directly. §4.6 allowed protected paths "if declared" — persistent prompt-injection route — SPEC updated: no
- 2026-10-09 — "accept" on a step_blocked escalation → 400 INVALID_DECISION (replaces "accept has no effect", which deadlocked the project) — review finding — SPEC updated: no
- 2026-10-09 — Rollback re-queues blocked/rejected steps of surviving plans (pending, attempt 0); completed steps stay completed because their commits stay on the branch (§4.8) — otherwise rolling back to the planning version left execution unstartable — SPEC updated: no
- 2026-10-09 — The working copy is prepared before a step attempt starts; an execution job that dies or fails returns its in_progress step to `rejected` (attempt kept, STEP_REJECTED interrupted=true), so a dead 4th attempt allows a 5th before blocking. Uncommitted leftovers are discarded (reset --hard + clean) before every step, so prompts see only committed content. The empty initial commit is made only on a fresh repo — SPEC updated: no

- 2026-10-10 — §2 notification preferences: table `user_notification_preferences` holds muted notification types per user (in-app only; project channels are configured by Owners). Recipients: Owners for actionable types (gates, drafts, blocked steps, failed runs, budget, failed release), all members for review/release completed and rollback — §5 had no preferences table — SPEC updated: no
- 2026-10-10 — Notifications are raised by an EF interceptor that queues a `notification` job in the same transaction as the audit event; a logical notification is keyed `<type>:<sourceId>` (`notifications.notification_key`, unique per user), so redelivery never duplicates. Unread count is polled every 30 s (paused while the tab is hidden, refetched on focus) rather than pushed — the SignalR hub is per project, notifications are per user — SPEC updated: no
- 2026-10-10 — FR-49: one `notification_deliveries` row per channel; 5 attempts with 1m/5m/30m/2h backoff; new `skipped` status (email without SMTP, gate decided before a retry); every send capped at 60 s, below the 2-min claim lease, so an in-flight send is never claimed twice — SPEC updated: no
- 2026-10-10 — Webhook channels require a signing secret (VALIDATION otherwise; a channel without one fails permanently, never sent unsigned) — FR-47/NFR-SEC — SPEC updated: no
- 2026-10-10 — Channel delivery is SSRF-safe: targets resolved and vetted at connect time (private, loopback, link-local, CGNAT, IPv4-mapped/6to4/NAT64 refused), connection made to the vetted IP, no redirects/proxy; `Notifications:AllowPrivateTargets` for local testing only — closes the phase 3 Known issue — SPEC updated: no
- 2026-10-10 — BUDGET_EXCEEDED is still written on every refusal but notifies only on the first refusal since the token budget was last changed — review finding (notification flood) — SPEC updated: no
- 2026-10-10 — Approval tokens never reach logs: ASP.NET's hosting request scope is replaced by an equivalent redacted scope (`Microsoft.AspNetCore.Hosting.Diagnostics=None`), OTel spans use the redacted path, nginx access log redacts URI/Referer and `/api/approvals/` logs errors at `crit` only (nginx error-log format can't be redacted) — NFR-SEC — SPEC updated: no
- 2026-10-10 — Channel messages use absolute links built from `App:PublicBaseUrl` (default http://localhost:8080); without a ≥32-char `Approvals:SigningKey` no one-click links are created (fail closed, warning logged) — SPEC updated: no
- 2026-10-10 — Vitest builds routes without code splitting (`autoCodeSplitting: !process.env.VITEST`); lazy chunk loading inside findBy timeouts caused flaky route tests — NFR-TEST — SPEC updated: no

- 2026-10-10 — FR-51 "status" filter on the admin event log is the event outcome (`ok` | `failure`; failure = RUN_FAILED, BUDGET_EXCEEDED, STEP_REJECTED/BLOCKED, GIT_PUSH_FAILED, PULL_REQUEST_FAILED, REVIEW_FAILED, RELEASE_FAILED, ARTIFACT_CORRUPTED, INTEGRITY_ALERT) — our events are append-only, the old stack's pending/consumed/dead_letter event status doesn't exist; admin lists use page/limit/total like /api/admin/users — SPEC updated: no
- 2026-10-10 — Dead-letter retry: 409 CONFLICT when an execution job is already queued/running for the project (unique index); 404 when the job isn't `dead` (incl. already retried); requires a browser session (an Admin API token can read the admin lists but not retry, like role changes); writes DEAD_LETTER_RETRIED {deadJobId, jobId, jobType} with no category — §6 lists only NOT_FOUND — SPEC updated: no
- 2026-10-10 — CLI: `release <id>` approves the pending pre_release gate, else POST /release (retry); extras beyond FR-52: `logout`, `artifact list|history`, type aliases (spec, plan); `config get` without a key lists all (old `config list`); the token is set only via `aide login` (prompt/stdin), stored per API URL and sent only to that URL; http is refused for non-loopback hosts; config at %APPDATA%\aide\config.json or ~/.config/aide/config.json (0600), precedence flag > env (AIDE_API_URL, AIDE_TOKEN, AIDE_CONFIG) > file > http://localhost:8080 — SPEC updated: no

## Known issues
- kubeconform, oasdiff, actionlint not installed locally — schema validation skipped locally (CI runs kubeconform; actionlint was run via Docker) — install optionally — Ben
- CI not yet run on GitHub (nothing pushed): Testcontainers on ubuntu-latest, SDK 10.0.401 install, Docker cache unverified — verify on first push — orchestrator
- §2 mentions per-user notification preferences but §5 has no table — designed in phase 12 (user_notification_preferences) — —
- Migrations need their own .editorconfig (generated_code) because the API image's build context is backend/ — fixed in phase 1 — —
- E2E on k8s registers users in the persistent k8s DB; the first one became Admin there. Phase 3 dev seed must cope with an existing admin; E2E may need higher auth rate limits as specs grow — orchestrator
- Migration Job logs a harmless "Cannot load library libgssapi_krb5.so.2" (Npgsql Kerberos probe) — ignore — —
- Webhook/Slack/Teams targets accept any https host (incl. private/loopback ranges); blocked at send time since phase 12 (SafeTargetConnector) — —
- appsettings.json LLM model IDs are placeholders until phase 6 confirms them from provider docs and picks one naming style — orchestrator (phase 6)
- The seed admin password is logged only by the pod that creates the account; after a redeploy the log line is gone, so note it on first deploy — documented in deploy-local.sh output — —
- GET /api/projects/:id/gates is unpaged (spec doesn't require paging); add a limit/cursor if gate counts grow — orchestrator
- Activity summaries exist on both server (fallback) and client; keep them in step when adding event types — orchestrator
- Phases 1–4 commits lacked Aide.Domain/Artifacts (+ its unit tests) because `.gitignore` had an unanchored `artifacts/`; fixed in 6841646 — commits before it don't build from a clean checkout — —
- The UI can't tell whether a corrupted version was current when marked (no `regenerationRequested` flag); copy may over-promise regeneration for rejected/invalidated latest versions — orchestrator
- Admin demotion doesn't drop hub subscriptions the user held as admin on non-member projects (until reconnect) — orchestrator
- k8s DB holds a check user (k8s-check-*@example.com) and project "K8s refinement check" from the phase 7 deploy check — delete if unwanted — Ben
- Frontend mock `isProtectedPath()` mirrors only part of PathRules.IsProtected (mocks only); the plan draft preview doesn't flag protected paths (agent plans can't declare them; Owner edits can) — orchestrator
- `scripts/ci/check-generated.sh` needs an argument and fails locally without one (`$1: unbound variable`) — check in Final CI pass — orchestrator
- Frontend Vitest suites timed out at the 5 s default under parallel load — testTimeout raised to 20 s in vite.config.ts (phase 10) — —
- GitHub Enterprise: `GitHub:GitBaseUrl`/`ApiBaseUrl` are configurable but project repo URLs are still validated as `https://github.com/…` only (GHE not in v1 scope) — orchestrator
- rollback_events.restored_phase is nullable in the DB (deploy-window safety); add a hand-written migration that re-runs the backfill and sets NOT NULL in a later release. The `releases.error → error_message` rename isn't rolling-update safe (fine here: Recreate strategy, releases new in phase 11) — orchestrator
- k8s DB also holds deploy-check users (phases 12–13), an unexpiring API token and project "Deploy check" — delete if unwanted — Ben
- Two users writing the very first version of a type concurrently aren't warned (baseVersion null) — accepted
