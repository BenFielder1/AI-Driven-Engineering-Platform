# Tevrin — Specification (0.8.0)

2026-11-22 · Ben Fielder · **0.8.0**, built on 0.7.0. Earlier versions: [`docs/0.1.0/SPEC.md`](docs/0.1.0/SPEC.md) … [`docs/0.7.0/SPEC.md`](docs/0.7.0/SPEC.md). Stack: [`STACK.md`](STACK.md). Sources:
- Ben's 0.8.0 brief (2026-11-22):
  - the Impulse coaching pack;
  - the engine features for the other candidate packs;
  - renaming the remaining `Aide` names to Tevrin.
- The research notes in [`docs/0.7.0/tevrin-engine-extraction.md`](docs/0.7.0/tevrin-engine-extraction.md): step 6 and "What could be built on the engine afterwards".
- The session discussion of where a second pack's code goes.
- The interview answers in §10.

> **How this spec relates to 0.1.0–0.7.0.** 0.8.0 is an increment on 0.7.0. **Everything in the earlier specs stays in force unless this document amends it.** Amended items are listed in §3.0. New requirements continue from **FR-323**, domain rules from **§4.62**, decisions use **`D-0.8.0-n`**, and milestones continue from **M65**. Earlier tests stay green unless an amendment changes what they assert; then update the test and name the amendment in it.

## 1. Overview

0.7.0 turned Tevrin into a pipeline engine with one pack, the SDLC. 0.8.0 does three things:

1. **The Aide name is retired.** Every remaining `Aide` project, namespace, type, config key, image and infrastructure name becomes Tevrin. Old names keep working through 0.8.x where users or data depend on them.
2. **The engine runs pipelines itself, and the first new pack proves it.**
   - Engine-controlled runs: start, drive, cancel, submit inputs, plus schedule and webhook triggers.
   - **Impulse coaching** (`Tevrin.Pack.Coach`): session log → performance analyst → programme planner checked by the deterministic Velocity load rules (retry with feedback, capped, then escalate) → plan approval gate → coach-voice message.
3. **The engine gains the features the other candidate packs need,** each proven on a fixture pipeline:
   - parallel stages, veto guards, scheduled follow-up stages and a feedback store per agent (market research desk);
   - fan-out with isolated context, aggregation stages and external outcome scoring (forecasting desk);
   - agent-to-agent conversation stages, map-reduce and scheduled aggregation (QA lab);
   - validator agents with structured findings, source-of-truth artifacts pinned across runs, and a human gate before external actions (job applications);
   - webhook triggers, tool permission tiers and mid-stage approval of destructive tool calls (on-call triage);
   - routing back to any earlier stage, a revision cap across the run, and multiple output variants (content studio).

Success means:
- **The SDLC behaves as in 0.7.0.** The characterisation snapshots change only once, in the reviewed rename re-baseline (FR-328), and every earlier test stays green, renamed where the code was renamed.
- **No `Aide`/`aide` identifier is left** outside an explicit compatibility allowlist (FR-329), and existing users, tokens, sessions and data keep working (§4.77).
- **The coach pack contains only YAML, schemas, prompts and its Velocity guards** (research step 6: "any time you have to change engine code, you've found a gap"). Its worked example (§4.63) runs end to end on the Fake provider and renders in the engine UI with no coach-specific frontend code.
- **Every new engine construct has a fixture pipeline** in `Tevrin.Engine.Tests` that runs end to end on the Fake provider, and the engine UI renders it with no frontend code specific to that fixture.
- **No engine assembly references a pack or a host assembly, and no pack references another pack or a host assembly** (architecture tests).

**In scope (0.8.0):**
- the rename (§3.1);
- engine run control and triggers (§3.2);
- the coach pack (§3.3);
- flow constructs (§3.4);
- quality and learning (§3.5);
- scale and conversation (§3.6);
- tools and safety (§3.7);
- engine UI (§3.8);
- release housekeeping (§3.9).

**Out of scope (0.8.0):**
- **Product packs other than the coach.** The market research desk, forecasting desk, QA lab, job applications, on-call triage, content studio and agent economy packs are future versions. Their engine features ship here and are proven on fixtures only (D-0.8.0-4).
- **Packaging (research step 7):** NuGet/npm packages and a pack template.
- **A CLI** (retired in 0.7.0).
- **Changing the SDLC's runs:** they stay host-controlled through the project endpoints (D-0.8.0-6).
- **Uploading or editing definitions at runtime:** they still ship inside packs.
- **The agent economy's unordered conversations and shared environment:** only two-or-more-participant turn-based conversations are in scope.
- **Running real destructive tools:** fixture tools only. No pack ships a destructive tool in 0.8.0.
- **A separate Impulse app:** Impulse can call the API and webhook, but none is built here.

## 2. Users and permissions

Roles are unchanged: project **Viewer**, **Contributor**, **Owner**, plus instance admins (0.1.0–0.6.0). Engine-controlled runs use the owning project's roles (D-0.8.0-2).

| Action | Who | Unauthorised |
| --- | --- | --- |
| Start an engine-controlled run, submit an input artifact, retry a failed stage | Contributor and up | 403 `FORBIDDEN`; 404 `NOT_FOUND` for a project they can't see |
| Cancel or roll back an engine-controlled run | Owner | 403 `FORBIDDEN` |
| Create, change or delete a pipeline schedule; create, rotate or delete a pipeline webhook | Owner | 403 `FORBIDDEN` |
| Call a pipeline webhook | Anyone holding the webhook URL **and** a valid signature (no user auth) | 401 `WEBHOOK_SIGNATURE_INVALID` |
| Add agent feedback | Contributor and up | 403 `FORBIDDEN` |
| Delete agent feedback | Owner, or the author | 403 `FORBIDDEN` |
| Post an outcome score | Contributor and up (session or API token) | 403 `FORBIDDEN` |
| Edit a source-of-truth artifact | Contributor and up; locking it as before (Owner in strict mode, as today's artifact rules) | 403 `FORBIDDEN` |
| Decide a `tool_approval` or `external_action` gate | Owner | 403 `FORBIDDEN` |
| Decide every other gate | As in 0.7.0 | as today |
| Read runs, timelines, scores, feedback and schedules (not webhook secrets) | Viewer and up | 404 `NOT_FOUND` |

Auth is unchanged, apart from the renamed cookie and token prefix (FR-326).

## 3. Functional requirements

### 3.0 Amended earlier requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| 0.6.0 §4.29 (internal `aide` identifiers stay) | Retired: code, config, image, infrastructure and wire names become Tevrin, with old names read through 0.8.x | FR-323–FR-329, §4.77 |
| CLAUDE.md rule 5 (never edit a committed migration) | One exception: the namespace and class-name rename in committed migration files, mechanically checked (FR-324) | FR-324, D-0.8.0-9 |
| 0.7.0 FR-261 snapshot freeze | One reviewed re-baseline for the rename; the freeze base moves | FR-328 |
| 0.7.0 D-0.7.0-3 (only manual triggers run) | `schedule` and `webhook` triggers run | FR-336, FR-337 |
| 0.7.0 D-0.7.0-4 (generic API is read plus gate decisions) | Engine-controlled pipelines also start, cancel, roll back, retry and take inputs through the generic API. Host-controlled pipelines (SDLC) are unchanged | FR-330–FR-335 |
| 0.7.0 D-0.7.0-19 (fan-out, parallel stages, tool tiers, conversation, scoring and feedback store wait for packs) | Built now, proven on fixtures | §3.4–§3.7 |
| 0.1.0 FR-65 merge strategy value `aide_merges` | Stored and returned as `tevrin_merges`; `aide_merges` is still accepted as input | FR-326, D-0.8.0-10 |
| 0.1.0 FR-4 API tokens (`aide_pat_…`) and the `aide.auth` cookie | New tokens are `tevrin_pat_…`, and the cookie is `tevrin.auth`. Old ones are accepted through 0.8.x | FR-326 |

### 3.1 Rename Aide to Tevrin (M65)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-323 | **Projects and code.**<br>Projects: `Aide.Api` → `Tevrin.Api`, `Aide.Domain` → `Tevrin.Domain`, `Aide.Infrastructure` → `Tevrin.Infrastructure`, `Aide.UnitTests` → `Tevrin.UnitTests`, `Aide.IntegrationTests` → `Tevrin.IntegrationTests`, and `backend/Aide.slnx` → `backend/Tevrin.slnx`, with folders and file names to match.<br>Every `Aide.*` namespace becomes the matching `Tevrin.*` namespace. Types named `Aide…` are renamed (`AideDbContext` → `TevrinDbContext`, `AideJson` → `TevrinJson`, `AideTelemetry` → `TevrinTelemetry`, `AideAuthOptions` → `TevrinAuthOptions`, `AideApiFactory` → `TevrinApiFactory`, `AideJobPayloadSerializer` → `TevrinJobPayloadSerializer`, `AideJobServiceCollectionExtensions` → `TevrinJobServiceCollectionExtensions`), as are test names and comments.<br>The architecture tests name the host assemblies explicitly (`Tevrin.Api`, `Tevrin.Domain`, `Tevrin.Infrastructure`), because a `Tevrin.*` prefix now covers the engine too | backend |
| FR-324 | **Migrations.** Committed migration files and the model snapshot change only their namespace, `TevrinDbContext` references and CLR type-name strings. Migration ids, `Up`/`Down` bodies and the history table are unchanged. A CI script fails if any committed migration differs from its pre-rename version in anything other than those substitutions. `dotnet ef migrations add` after the rename produces an empty migration (CI's EF drift check) | backend + data + CI |
| FR-325 | **Config and telemetry.**<br>Config and env names become Tevrin: `ConnectionStrings:Tevrin`, `TEVRIN_IMPORT_ROOT`, `TEVRIN_DATA_DIR`, and every other `AIDE_*` that Tevrin itself reads. `AIDE_RUN_MIGRATIONS`, which Tevrin writes into generated projects, keeps its name (D-0.8.0-16). The old names are read as a fallback through 0.8.x, with one startup warning per old name used.<br>The auth scheme is renamed `Tevrin`. The activity source and meter are `Tevrin`, with tags `tevrin.*` | backend + deploy |
| FR-326 | **Wire and stored identifiers, read both** (§4.77):<br>- **Tokens:** new API tokens are `tevrin_pat_…`, and `aide_pat_…` tokens still authenticate.<br>- **Cookie:** the session cookie is `tevrin.auth`. A request with only `aide.auth` is accepted and the cookie is reissued as `tevrin.auth`, so nobody is signed out.<br>- **Plans:** new plans write a `tevrin-plan` block, and the plan parser reads `tevrin-plan` and `aide-plan`.<br>- **Merge strategy:** a migration rewrites `aide_merges` to `tevrin_merges`. The API returns `tevrin_merges` and accepts both (breaking-change decision D-0.8.0-10; contract and client regenerated).<br>- **Other names:** every other persisted or externally visible `aide…` name found by FR-329's scan (Git remote or branch names, snapshot folders, Kubernetes names, `X-Aide-*` headers if any) is renamed the same way, and old values are still read | backend + frontend + data |
| FR-327 | **Images and infrastructure.**<br>- Images are `tevrin-api` and `tevrin-web`.<br>- The local Kubernetes namespace, data PVC and labels become `tevrin*`; the generic workload, service and job names (`api`, `web`, `db`, `db-migrate`) stay (D-0.8.0-17).<br>- `.railway/railway.ts`, `deploy.yml` and the smoke scripts use the new project and variable names, and the default database name is `tevrin`.<br>- A runbook, `docs/operations/rename-to-tevrin.md`, covers renaming the Railway project and the Postgres database in place (`ALTER DATABASE … RENAME`, with a maintenance window and rollback steps), and the variable changes. Ben runs it; agents never do.<br>- `deploy-local.sh` redeploys fresh under the new names and documents removing the old `aide` namespace | deploy + docs |
| FR-328 | **One snapshot re-baseline.** A single commit re-baselines the characterisation snapshots for the rename. A CI check replaces `tevrin` with `aide` in the new snapshots, drops content hashes, and requires the result to equal the old snapshots. `check-snapshots-frozen.sh` then moves its base to that commit. No other snapshot or golden-file change is allowed in 0.8.0 | backend + CI |
| FR-329 | **No leftover names.** A test scans tracked files for `aide` and `Aide` (case-insensitive word or identifier match), skipping the frozen `docs/0.*` folders and `docs/history`. It fails on any hit not in `scripts/ci/aide-allowlist.txt`, which lists each kept compatibility name with its reason and removal version. CLAUDE.md, STACK.md, README and the docs site are updated (layout, commands, config) | backend + docs + CI |

**FR-326 acceptance**
- Given the renamed solution, then `backend/Tevrin.slnx` builds with `-warnaserror`, no project or namespace starts with `Aide`, and `EngineDependencyTests` still fails if an engine assembly references `Tevrin.Api`, `Tevrin.Domain`, `Tevrin.Infrastructure` or a pack (FR-323).
- Given each committed migration, when the CI script reverses the rename substitutions, then the file equals its pre-rename version, and `dotnet ef migrations add Check` creates an empty migration (FR-324).
- Given only `ConnectionStrings__Aide` set, when the API starts, then the database connects and one warning is logged per old name (FR-325). `AIDE_RUN_MIGRATIONS` isn't read by the API: it is the variable Tevrin writes into generated projects, so it keeps its name (D-0.8.0-16).
- Given `deploy-local.sh`, then the cluster has namespace `tevrin` running the `tevrin-api` and `tevrin-web` images and its database, and the smoke test passes (FR-327 manual check).
- Given a new `aide` identifier added outside the allowlist, then the FR-329 test fails and names the file and line.
- Given an `aide_pat_…` token created on 0.7.0, when it calls `/api/me`, then 200. Given a token created on 0.8.0, then its value starts `tevrin_pat_`.
- Given a browser holding only `aide.auth`, when it loads `/api/me`, then 200, and the response sets `tevrin.auth` and expires `aide.auth`.
- Given a stored 0.7.0 plan with an `aide-plan` block, when execution reads it, then its steps parse as before.
- Given `PUT` project settings with `mergeStrategy: "aide_merges"`, then 200, and reading the settings returns `tevrin_merges`.

**FR-328 acceptance**
- Given the re-baseline commit, when the CI check runs, then mapping `tevrin` to `aide` and dropping hashes makes every snapshot equal its M54 version. A snapshot with any other change fails it.

### 3.2 Engine run control and triggers (M66)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-330 | **Run control mode.** `IPipelinePack` declares, per pipeline, `RunControl.Engine` (the default) or `RunControl.Host`. The SDLC declares `Host`, so its definition and hash are unchanged. The run-control endpoints (FR-331, FR-333, FR-335) refuse a host-controlled pipeline with 409 `RUN_CONTROLLED_BY_HOST` | backend |
| FR-331 | **Start a run.** `POST /api/projects/{id}/pipeline-runs` with `{ pipelineId, approvalMode?, inputs?: { <artifactKey>: <json> } }`:<br>- creates a run pinned to the pipeline's current definition hash, at its start stage, checkpoint 1;<br>- stores each given input as version 1 of that artifact, checked against its schema;<br>- queues the run driver.<br>Approval mode defaults to the project's. At most one non-final run per project and pipeline, unless the definition says `concurrency: { runs: many }` (409 `RUN_ALREADY_ACTIVE`). An unknown pipeline gives 404 `NOT_FOUND`, an input that fails its schema 400 `INPUT_INVALID` with the paths, and an undeclared input key 400 `VALIDATION` | backend + data |
| FR-332 | **Run driver.** The engine job `stage.run` (`EngineJobTypes`) moves an engine-controlled run one stage at a time (§4.62):<br>- runs the current stage by kind: agent, handler, manual, or one of the §3.4–§3.7 constructs;<br>- checks `validate:`, then applies the transition (`next`, `on_success`, a loop or a route), or stops at a gate, a manual stage or a missing input;<br>- queues itself for the next stage, and marks the run `completed` after the last stage.<br>It is idempotent on redelivery: a stage execution already finished isn't rerun. Gate decisions on engine-controlled runs re-queue the driver | backend |
| FR-333 | **Cancel, roll back, retry.**<br>- `POST /api/pipeline-runs/{runId}/cancel` (Owner) ends the run `cancelled` and cancels its queued jobs.<br>- `/rollback` with `{ targetCheckpoint, reason }` (Owner) applies the engine rollback rules (checkpoint, invalidation cascade).<br>- `/retry` (Contributor) re-queues a failed stage.<br>A final run gives 409 `RUN_NOT_ACTIVE`, and an invalid target 400 `INVALID_TARGET_VERSION` | backend |
| FR-334 | **Runs without a subject.** `pipeline_runs.subject_id` is optional. Artifacts, gates, agent runs and stage executions of a run with no subject belong to the run alone; the SDLC keeps its release-version subject. The unique (`pipeline_id`, `subject_id`) index applies only when there is a subject | backend + data |
| FR-335 | **Submit an input.** `POST /api/pipeline-runs/{runId}/inputs/{artifactKey}` with a JSON body adds a new version of an input artifact (artifacts the definition marks `input: true`), checked against its schema (400 `INPUT_INVALID`). A stage may require an input with `requires: [{ input: <key> }]`. The driver waits there (run status `waiting_on_input`, a new generic status) and continues when the input arrives | backend + data |
| FR-336 | **Schedule trigger.**<br>- **Declaring it:** a definition with `trigger: { kind: schedule }` may be scheduled per project. `POST /api/projects/{id}/pipeline-schedules` takes `{ pipelineId, cron, timezone, inputs? }`, where `cron` has 5 fields.<br>- **Firing:** the engine job `trigger.fire` starts a run at each due time (§4.71). If a run is still active, the fire is skipped and recorded.<br>- **Validation:** an invalid cron or timezone gives 400 `SCHEDULE_INVALID`, and a pipeline that doesn't declare the trigger 409 `TRIGGER_NOT_SUPPORTED`.<br>- **Managing:** schedules can be listed, paused and deleted.<br>This lifts D-0.7.0-3 for schedules | backend + data |
| FR-337 | **Webhook trigger.**<br>- **Endpoint:** for a definition with `trigger: { kind: webhook, input: <artifactKey> }`, an Owner creates a project webhook with `POST /api/projects/{id}/pipeline-webhooks` `{ pipelineId }`. It returns `{ id, url, secret }`, and the secret is shown once. The secret can be rotated (the old one stays valid for 24 hours) and the webhook deleted.<br>- **Calls:** `POST /api/hooks/pipelines/{webhookId}` takes a signed JSON body (§4.72). It starts a run with the body as the declared input, or submits it to the active run when one exists and the definition says `on_active: submit`.<br>- **Responses:** 202 with `{ runId }`; 401 `WEBHOOK_SIGNATURE_INVALID`; 409 `WEBHOOK_REPLAY` for a delivery id seen in the last 24 hours; 400 `INPUT_INVALID`. Deliveries are logged (id, status, run) for 30 days | backend + data |

**FR-331/FR-332 acceptance**
- Given the fixture `fixture-linear` (agent → agent → gate → handler, engine-controlled), when a run starts on the Fake provider, then the driver runs both agents, stops at the gate (`waiting_on_gate`), continues on approval, runs the handler and ends `completed`. The stage list and timeline show every step.
- Given the SDLC pipeline, when `POST /api/projects/{id}/pipeline-runs` names it, then 409 `RUN_CONTROLLED_BY_HOST`.
- Given a redelivered `stage.run` job for a finished stage execution, then no agent runs again.

**FR-337 acceptance**
- Given a running engine-controlled run, when an Owner cancels it, then it ends `cancelled`, its queued jobs are cancelled, and a second cancel gives 409 `RUN_NOT_ACTIVE`. Given a Contributor, then 403 (FR-333).
- Given a run without a subject, then its artifacts and gates list under the run, and a second subject-less run of the same pipeline in the same project is refused only by the one-run rule, not by the subject index (FR-334).
- Given a stage with `requires: [{ input: session_log }]` and no such input, then the run is `waiting_on_input`. When the input is submitted, the driver continues. A body that fails the schema gives 400 `INPUT_INVALID` with the failing paths (FR-335).
- Given schedule `0 6 * * 1` in Europe/London and a fake clock at Monday 05:59 local, when the clock reaches 06:00, then exactly one run starts. If a run is still active, the fire is recorded as skipped (FR-336).
- Given §4.72's worked example, then the signature verifies and a run starts. Given the same delivery id again, then 409 `WEBHOOK_REPLAY`. Given a timestamp 301 s old, then 401.

### 3.3 Impulse coaching pack (M67)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-338 | **The pack project.** A new project, `backend/src/Tevrin.Pack.Coach`, references only `Tevrin.Engine.Domain`. It contains `Definitions/impulse-coach.pipeline.yaml`, JSON schemas, prompts (embedded and registered by the pack) and `CoachPack : IPipelinePack`. It also registers its key constants and its guards, which are pure code. `Tevrin.Api` registers the pack; no other host project changes for it.<br>New architecture tests check two things: a pack references only `Tevrin.Engine.Domain` (never another pack or a host assembly), and no engine assembly references `Tevrin.Pack.Coach`. Tests live in `backend/tests/Tevrin.Pack.Coach.Tests` | backend |
| FR-339 | **The pipeline** `impulse-coach` (engine-controlled; triggers: manual, webhook with input `session_log`, schedule):<br>- **artifacts:**<br>  - `athlete_profile`: source of truth, project scope, pinned at run start (FR-352);<br>  - `session_log`: input, JSON;<br>  - `performance`, `plan_draft`, `coach_message`: JSON with schemas.<br>- **stages:**<br>  1. `analyse`: agent `performance-analyst`, inputs `session_log` and `athlete_profile`, output `performance`;<br>  2. `plan`: agent `programme-planner`, inputs `performance` and `athlete_profile`, output `plan_draft`, with `validate: [{ guard: coach.velocity-load-rules }]` and `on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }`;<br>  3. `review`: gate `plan_approval` in modes strict and advisory, approving `plan_draft`;<br>  4. `write`: agent `coach-voice`, input `plan_draft`, output `coach_message`.<br>The definition passes the §4.49 validator | backend |
| FR-340 | **`coach.velocity-load-rules` guard.** It checks `plan_draft` against `athlete_profile` and the recent `session_log` history using the rules in §4.63. It returns one finding per broken rule, written as `rule: message` (e.g. `R1: week 2 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)`). The rule thresholds come from `athlete_profile.velocity` when present, else the §4.63 defaults | backend |
| FR-341 | **Agent policy.** The coach uses the engine's default agent policy (validate, retry with feedback, cap, escalate), with no pack extension. Host hooks that apply to every pipeline (the project token budget FR-26, audit events and redaction) apply to coach runs too (D-0.8.0-7) | backend |
| FR-342 | **Pack docs.** A docs-site guide **Impulse coaching pack** covers:<br>- the pipeline;<br>- the artifacts and their schemas;<br>- the Velocity rules, with the defaults and how to override them in `athlete_profile.velocity`;<br>- how to start a run from the UI, the API and a webhook (with a signing example), and how to schedule a weekly plan | docs |

**FR-339/FR-340 acceptance**
- Given `Tevrin.Pack.Coach`, then it references only `Tevrin.Engine.Domain`, and the architecture tests fail if it references `Tevrin.Pack.Sdlc` or a host assembly, or if an engine assembly references it (FR-338).
- Given a coach run on a project over its token budget, then the agent stage fails with `BUDGET_EXCEEDED`, as an SDLC agent would (FR-341).
- Given the docs site, then the Impulse coaching pack guide exists, its webhook signing example matches §4.72, and the internal link check passes (FR-342).
- Given §4.63's worked example on the Fake provider, then:
  - `analyse` runs once;
  - `plan` runs 3 times, and attempts 2 and 3 carry the guard's findings as feedback;
  - after attempt 3 still fails, one `escalation` gate is pending, and `write` hasn't run.
- Given the same run where attempt 2 passes, then `plan_approval` is pending. On approval, `write` stores `coach_message` and the run ends `completed`.
- Given the coach run, when the Pipeline tab and replay render it (Vitest with MSW, real definition JSON), then all four stages, the artifacts and the gate show with no coach-specific code in `src/engine/`.

### 3.4 Flow constructs (M68)

Each construct is new definition syntax (§4.49 amended). The validator checks each one, and its fields are left out of the canonical JSON when unused, so existing hashes don't change. Each gets a fixture pipeline in `Tevrin.Engine.Tests/Fixtures`.

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-343 | **Parallel stages.** `parallel: { id, branches: [[<stage>…], [<stage>…]], join: all }` groups branches of ordinary stages:<br>- the driver starts every branch at once, as separate `stage.run` jobs;<br>- the group completes when every branch completes (§4.64);<br>- a failed branch fails the group unless `on_branch_failure: continue`;<br>- branch stages may not read each other's outputs (validator: `DEF_BRANCH_INPUT`).<br>The engine concurrency rules still apply per job type. Fixture: `fixture-parallel` (bull and bear analysts) | backend |
| FR-344 | **Veto.** `veto: [{ guard: <name> } \| { agent: <key> }]` on a stage, then `on_veto: { to: <earlier stage> } \| { end: vetoed }`. A veto is stronger than `validate:`: there's no retry, the path stops, and the run either routes back or ends `completed` with outcome `vetoed`. The veto's findings are stored on the stage execution. Fixture: `fixture-veto` (risk manager vetoes position size) | backend |
| FR-345 | **Fan-out with isolated context.** An agent stage with `fan_out: { agents: [<key>…] }` (each key bound by `agents:` entries, with their own prompts and models) or `fan_out: { count: n }` (same agent, n samples) runs one branch per entry, in parallel. Each branch's output is stored as the stage's artifact, with variant label `<agent key>` or `#i`.<br>**Isolation** (§4.65): a branch's prompt holds only the stage's declared inputs and its own context. It never contains a sibling's output, and the runner asserts this. Fixture: `fixture-forecast` (3 forecasters) | backend |
| FR-346 | **Aggregation stage.** `aggregate: { from: <fan-out or parallel id or map stage> }` on an agent or handler stage gives it every branch output as a list input (`{{<KEY>_ALL}}` for agents, a typed list for handlers), each labelled with its branch. An optional `disagreement:` guard can raise a finding (e.g. spread > 0.25) that flags the aggregate without failing it | backend |
| FR-347 | **Route back to any earlier stage.** An agent stage may declare `routes: [<earlier stage id>…]`. Its output schema then includes a `route` field (`continue` or one of the ids), and the driver applies a `route` transition to the chosen stage. The cascade invalidates downstream artifacts, as `on_reject` does. The validator rejects routes to later or unknown stages (`DEF_ROUTE_TARGET`). Fixture: `fixture-editorial` (the editor sends work back to research or writing) | backend |
| FR-348 | **Revision cap across the run.** `limits: { revisions: n, then: escalate \| fail }` at pipeline level counts every backward transition in a run (loops, `on_reject`, routes, veto `to:`), §4.66. The transition that would exceed `n` is refused, and `then` applies. This sits on top of per-loop `max_iterations` | backend |
| FR-349 | **Multiple output variants.** `variants: { count: n, select: gate \| { agent: <key> } }` on an agent stage produces n variants of the output, stored as versions with variant labels `v1…vn`. `select: gate` raises a `variant_select` gate where the decider picks one. An agent selector returns `{ choice, reason }`. The chosen variant becomes the stage's output, and the rest are kept, read-only (§4.67) | backend |

### 3.5 Quality and learning (M69)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-350 | **Validator agent.** `validate: [{ agent: <key> }]`, alongside guards, runs a validator agent on the stage output.<br>- **Output:** the agent returns `{ verdict: pass \| reject, findings: [{ path?, claim?, reason, severity: error \| warning }] }`, checked against an engine schema.<br>- **Reject:** findings with severity `error` reject the output, and `on_invalid` (retry with feedback, cap, `then`) treats them like guard findings (§4.55). `warning` findings are kept and don't reject.<br>- **Recording:** findings are stored on the attempt's agent run, and the validator's own run is recorded. A validator that fails to return valid JSON fails the attempt with `VALIDATOR_INVALID_OUTPUT`.<br>Fixture: `fixture-factcheck` | backend |
| FR-351 | **Agent feedback store.** Entries hold `{ pipelineId, agentKey, projectId, source: human \| stage, text, runId?, agentRunId?, createdBy?, createdAt }`.<br>- **Human entries:** added and listed through the API and UI.<br>- **Stage entries:** a stage with `writes_feedback: { agent: <key>, from: <output field> }` writes one entry per item in that field of its output.<br>- **Use:** an agent stage with `feedback: { inject: true, limit: 10 }` gets the entries for its agent key in `{{AGENT_FEEDBACK}}`, newest first (§4.75). The rendered prompt records which entries it used.<br>Fixture: `fixture-postmortem` | backend + data |
| FR-352 | **Source-of-truth artifact.** An artifact declared `scope: project` has one version history per project and artifact key, shared by every run in the project of the pipelines that declare it (D-0.8.0-19). It is created and edited through the artifact endpoints, and locked as usual.<br>A run pins the newest **locked** version when it starts (`pinned_artifacts` on the run), and every stage of that run reads the pinned version. A newer version doesn't change a running run (§4.76). A run that needs a project-scope artifact with no locked version can't start: 409 `SOURCE_ARTIFACT_MISSING`. Fixture: `fixture-cv` (master CV) | backend + data |
| FR-353 | **External outcome scoring.** `POST /api/pipeline-runs/{runId}/outcomes` with `{ target: { agentRunId } \| { artifactKey, version, variant? }, metric, kind: number \| probability_brier, value, prediction?, observedAt, source }` stores an outcome score. A Brier score is computed by the engine (§4.74). Scores are listed per run and target. The prompt comparison (0.7.0 FR-298) adds a mean score per metric and count for each agent key and prompt version | backend + data |
| FR-354 | **Follow-up stages (scheduled delayed jobs).** `followups: [{ stage: <id>, after: [<duration>…] }]` at pipeline level (durations such as `1d`, `5d`, `20d`). When a run completes, the engine queues one delayed `stage.run` job per duration for that stage (§4.70). A follow-up stage is declared like any stage but sits outside the main flow. It reads the run's artifacts and outcome scores and stores its outputs with label `followup:<duration>`.<br>The run stays `completed`, and follow-up executions appear in its timeline. Cancelling a run before it completes schedules none; cancelling a completed run cancels pending follow-ups. Fixture: `fixture-postmortem` (grading calls after 1, 5 and 20 days) | backend + data |

### 3.6 Scale and conversation (M70)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-355 | **Map-reduce.** `map: { over: <artifact key>.<array path>, agent \| handler, max_parallel: 8, max_items: 1000, on_item_failure: skip \| fail, max_failed: 5% }` on a stage runs one isolated item execution per element:<br>- each item execution is its own `stage.run` job and agent run, given only that element and the stage's inputs;<br>- an optional `reduce: { agent \| handler }` stage gets every item output (FR-346 shape).<br>More than `max_items` elements gives `MAP_TOO_MANY_ITEMS` and the stage fails. Failures beyond `max_failed` fail the stage (§4.68). Item outputs are stored as one artifact collection, each with an item index. Fixture: `fixture-qa` (scoring 200 transcripts) | backend + data |
| FR-356 | **Conversation stage.** `conversation: { participants: [{ agent: <key>, role: <name> }…], opener: <role>, max_turns: 20, stop: { guard: <name> } \| { phrase: "<text>" } }` runs a turn-based conversation:<br>- each turn is one agent run for the next participant in order, given its own persona prompt and the transcript so far;<br>- it stops at `max_turns`, when the stop guard passes, or when a reply contains the stop phrase (§4.69);<br>- the output artifact is the transcript `{ turns: [{ role, agentKey, text, agentRunId }] }`.<br>Two to six participants. Fixture: `fixture-callsim` (customer and agent simulators) | backend |
| FR-357 | **Scheduled aggregation over runs.** A stage input `from_runs: { pipeline: <id>, artifact: <key>, window: 7d, status: completed }` collects that artifact's final versions from the project's runs completed in the window, as a list (FR-346 shape). It is capped at 1,000 runs, newest first. Combined with a schedule trigger, this gives weekly trend reports. Fixture: `fixture-trends` | backend |

### 3.7 Tools and safety (M71)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-358 | **Tool permission tiers.** A pack registers each tool with a tier: `read`, `write` or `destructive`. An agent stage or binding lists `tools:` and may set `tool_policy: { max_tier }`, defaulting to `write`.<br>- **Validator:** a listed tool above `max_tier` gives `DEF_TOOL_TIER`.<br>- **Call time:** the runner refuses a call to a tool not offered to this stage as an error result, and logs it.<br>- **Recording:** tool calls record their tier. The SDLC repo tools are `read`, so the SDLC is unchanged | backend |
| FR-359 | **Approval in the middle of a stage for destructive tools.** A stage needs `tool_policy: { max_tier: destructive }` to offer destructive tools. When the model calls one, the runner:<br>1. saves the tool loop's state (messages so far and the pending call) on the agent run;<br>2. raises a `tool_approval` gate with the tool, its redacted arguments and the agent's stated reason;<br>3. defers the job.<br>Then (§4.73):<br>- **Approve:** the tool runs and the loop resumes from the saved state, with no repeated LLM call for earlier turns.<br>- **Reject:** the tool result is `denied by <user>: <comment>`, and the loop resumes.<br>- **No decision within the gate's timer** (default 24 hours) counts as reject.<br>A destructive tool is never run without an approved gate, in every approval mode, autonomous included. Fixture: `fixture-oncall` (`restart-pod` is destructive, `read-logs` is read) | backend + data |
| FR-360 | **Human gate before an external action.** A handler or tool registered with `external: true` (it sends, publishes, submits or pays outside Tevrin) runs only after an approved `external_action` gate raised immediately before it. The gate shows the exact payload to be sent. This applies in every approval mode, autonomous included. On reject, the stage fails with `EXTERNAL_ACTION_REJECTED`, or follows `on_reject` if declared. Fixture: `fixture-cv` ("submit application" handler) | backend |
| FR-361 | **Webhook triggers and tools on agents** are covered by FR-337 and FR-358. The on-call fixture uses both: a signed alert webhook starts the run, and the investigator has `read` tools and the executor a `destructive` one | backend |

**FR-343–FR-361 acceptance (one per fixture, all on the Fake provider)**
- **`fixture-parallel`:** both branches run, as two `stage.run` jobs; the join waits for the slower one, and the PM stage gets both outputs.
- **`fixture-veto`:** the risk guard vetoes, the run routes back once, then ends with outcome `vetoed` on the second veto.
- **`fixture-forecast`:** each forecaster's recorded input contains none of its siblings' outputs, the aggregator gets 3 labelled estimates, and §4.74's Brier example stores 0.09.
- **`fixture-editorial`:** the editor routes to `research` and then `write`. With `limits.revisions: 2`, the third route is refused and escalates.
- **Variants:** `variants: { count: 3, select: gate }` stores v1–v3, and picking v2 makes v2 the stage output.
- **`fixture-factcheck`:** a rejected claim feeds back with its findings, and the retry passes.
- **`fixture-cv`:**
  - the run pins master CV v2;
  - when v3 is locked mid-run, the run still reads v2;
  - submitting waits on an `external_action` gate, and nothing is sent before approval.
- **`fixture-postmortem`:** completion schedules follow-ups at +1d, +5d and +20d (with a fake clock). The 1d follow-up writes feedback entries, and a later run's prompt contains them.
- **`fixture-qa`:**
  - 200 items run, with at most 8 at a time;
  - 3 failed items are skipped under 5%;
  - 1,001 items give `MAP_TOO_MANY_ITEMS`.
- **`fixture-callsim`:** 2 participants alternate and stop at the phrase within `max_turns`.
- **`fixture-trends`:** a weekly schedule fire collects 7 days of runs.
- **`fixture-oncall`:** `restart-pod` raises `tool_approval`. On approve, the loop resumes with no repeat of earlier LLM calls; on reject, the model gets the denial text.
- **Tool tiers:** a stage listing `restart-pod` with `tool_policy: { max_tier: write }` fails validation with `DEF_TOOL_TIER` at `stages[i].agent.tools[j]`, and the SDLC definition still validates with its `read` repo tools (FR-358).

### 3.8 Engine UI (M72)

All in `frontend/src/engine/`, definition-driven (0.7.0 FR-304, FR-305).

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-362 | **Start run.** For engine-controlled pipelines, the Pipeline tab has a **Start run** button (Contributor and up). It opens a dialog with:<br>- a pipeline picker and an approval mode;<br>- an input editor per declared input artifact: a form for flat object schemas (string, number, boolean, enum, date), else a JSON editor with schema errors shown inline;<br>- a JSON file upload.<br>Submitting creates the run; errors map through the shared error copy. A run waiting on input shows **Submit input** with the same editor | frontend |
| FR-363 | **New constructs in the stage list and timeline:**<br>- parallel groups and fan-out branches as lanes, labelled by branch;<br>- map stages as a progress bar (done, failed, total), drilling into a paged item list;<br>- variants as tabs, with the chosen one marked;<br>- conversation transcripts as a chat-style view;<br>- follow-ups as their own section with due times;<br>- veto and route transitions as labelled entries | frontend |
| FR-364 | **Gates.** The gate inbox and gate dialog show `tool_approval` (tool, tier, redacted arguments, the agent's reason), `external_action` (the payload) and `variant_select` (the variants side by side, with a pick). Only Owners see the approve control for the first two | frontend |
| FR-365 | **Scores and feedback.** Prompt comparison shows mean scores per metric. A **Feedback** section on the Pipeline tab lists entries per agent key, with add (Contributor) and delete (Owner or author) | frontend |
| FR-366 | **Triggers.** A **Triggers** section on the Pipeline tab (Owner): create, pause and delete schedules, with a preview of the next 3 fire times; create, rotate and delete webhooks. A new secret is shown once with a copy button and a signing example | frontend |

**FR-362 acceptance**
- Given `fixture-parallel`, `fixture-qa`, the variants fixture and `fixture-callsim` served by MSW, then the stage list shows two lanes, a map progress bar (200 total, 3 failed), v1–v3 tabs with the choice marked, and the transcript, with no fixture-specific code in `src/engine/` (FR-363).
- Given a pending `tool_approval` gate, then an Owner sees the tool, its tier and the redacted arguments with approve and reject, and a Contributor sees the card without approve (FR-364).
- Given scored agent runs, then prompt comparison shows the mean score per metric. Given a Contributor, then they can add feedback but not delete another user's entry (FR-365).
- Given an Owner creating a webhook, then the secret shows once with a copy button, and reloading the page shows the webhook without its secret. A new schedule previews its next 3 fire times in its timezone (FR-366).
- Given the coach pipeline, when a Contributor opens Start run, then the `session_log` editor renders from its schema. An invalid date shows the schema error inline, and a valid submit lands on the new run.
- Given a Viewer, then no Start run button.

### 3.9 Release housekeeping (M73)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-367 | Version 0.8.0 in the three version sources, and a changelog entry ("Coaching pack and engine features": the rename, run control, triggers, the coach pack, new pipeline constructs). The docs guide **Pipelines and packs** gains the definition reference for every new construct (§4.49 amended), and there are guides for **Triggers and webhooks** and **Impulse coaching pack** (FR-342), plus the rename runbook (FR-327). CLAUDE.md and STACK.md are updated for the new names and projects. At release: `docs/history/0.8.0.md`, with SPEC and PROGRESS moved to `docs/0.8.0/` | frontend + docs |

**FR-367 acceptance**
- Given the release commit, then `scripts/ci/check-version.sh 0.8.0` passes, the changelog's newest entry is 0.8.0 with its date, and the docs site builds with the new guides and passes its link check.

## 4. Domain rules

### 4.49 Definition shape (amended)

New optional fields, each validated, and left out of the canonical JSON when empty:
- **pipeline level:**
  - `concurrency.runs: one | many`;
  - `limits: { revisions, then }`;
  - `followups: [{ stage, after }]`;
  - `trigger.input`, `trigger.on_active`.
- **artifact level:** `input: true`, `scope: run | project`.
- **stage level:**
  - `parallel`, `veto` / `on_veto`, `fan_out`, `aggregate`, `routes`, `variants`;
  - `feedback`, `writes_feedback`, `map` / `reduce`, `conversation`, `tool_policy`;
  - `requires: [{ input }]`;
  - input `from_runs`;
  - `validate: [{ agent }]`.
- **pack registration:** tool `tier`, handler/tool `external`, pipeline `RunControl`.

New validator codes: `DEF_BRANCH_INPUT`, `DEF_ROUTE_TARGET`, `DEF_TOOL_TIER`, `DEF_FANOUT_AGENT`, `DEF_MAP_SOURCE`, `DEF_CONVERSATION_PARTICIPANTS`, `DEF_FOLLOWUP_STAGE`, `DEF_SCOPE_INPUT` (a project-scope artifact produced by a stage). A stage combining more than one of `parallel`, `fan_out`, `map`, `conversation` and `variants` gives `DEF_STAGE_KIND`.

### 4.62 Run driver

`stage.run(run, stageExecution)`:
1. Stop if the run is final, or if this execution is already finished (idempotent).
2. Evaluate the stage's `requires`. A missing input → `waiting_on_input`, stop. Unmet guards → the stage fails with their reasons.
3. Check the gate. If an entry gate is needed and not approved → `waiting_on_gate`, stop.
4. Run the stage by kind. Agent → `AgentStageRunner` (validate, retry, then `then:`); handler → `StageHandlerRunner`; manual → stop, except a manual stage whose only work is its gate, which passes once the gate is approved (as `approve` in `fixture-linear`); construct → §4.64–§4.69.

Engine runs write no `state_transitions` rows (that table is the SDLC's phase history); their history is the stage executions, agent runs, gates and artifacts, which the timeline shows. A failed `stage.run` job leaves the run active, so `retry` can re-queue the stage. Retry is refused (409 `CONFLICT`) while the run has a pending gate. Approving an engine escalation gate re-queues the driver, which runs the stage again as a new agent run from attempt 1; rejecting it fails the run.
5. Apply the veto, then the transition: route, loop or `next`, checked against `limits.revisions`.
6. Queue `stage.run` for the next stage, or mark the run `completed` and queue its follow-ups.

Example (`fixture-linear`):
1. Start: `draft` runs, then `check` runs.
2. Entering `approve` needs a gate → `waiting_on_gate`.
3. On approve, the driver is queued, `publish` (a handler) runs, and the run is `completed`.

That gives 4 stage executions, 2 agent runs and 1 gate.

### 4.63 Velocity load rules (coach)

Inputs:
- `plan_draft.weeks[]`, each `{ week, sessions: [{ date, type, intensity: easy | moderate | hard | race, distanceKm?, durationMin? }] }`;
- `athlete_profile` (`baselineWeeklyKm`, `raceDate?`, `velocity?` overrides);
- the last 4 weeks of `session_log` actuals.

Week volume is total `distanceKm`, or total `durationMin` when the athlete profile sets `volumeUnit: minutes`. Defaults, with their override keys:

| Rule | Default | Finding when broken |
| --- | --- | --- |
| R1 weekly increase | volume(w) ≤ volume(w−1) × 1.10. Week 1 compares with the last actual week, or `baselineWeeklyKm` | `R1: week 2 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)` |
| R2 recovery | ≥ 48 h between the starts of two `hard`/`race` sessions | `R2: hard sessions on 2026-12-01 and 2026-12-02 are 24 h apart (min 48 h)` |
| R3 hard count | ≤ 3 `hard` sessions per plan week | `R3: week 3 has 4 hard sessions (max 3)` |
| R4 taper | With a `raceDate`: a week ending 8–14 days before it ≤ 70% of the peak of the previous 4 weeks; a week ending within 7 days ≤ 50% | `R4: week 4 volume 48.0 km is 80% of peak 60.0 km within 14 days of the race (max 70%)` |
| R5 rest | ≥ 1 day with no session in every plan week | `R5: week 1 has no rest day` |

Worked example (§3.3 acceptance):
- **Inputs:** last actual week 50 km, no race date.
- **Attempt 1:** week 1 = 50, week 2 = 61, with hard sessions on Tue and Wed → R1 and R2 fail.
- **Attempt 2:** week 2 = 58 → R1 (+16%) fails.
- **Attempt 3:** week 2 = 56 → R1 (+12%) fails.

After `max_attempts: 3`, the result is `then: escalate`: one `escalation` gate. If instead attempt 2 had week 2 = 55 (+10%) with Tue/Thu hard sessions, every rule would pass.

### 4.64 Parallel join

The group is complete when every branch's last stage is complete (`join: all`). A failed branch, with the default `on_branch_failure: fail`, fails the group: the other branches' running jobs finish, but no further stages start. With `continue`, the join completes with the failed branches marked, and downstream stages see only the successful outputs. Branch stages may not declare `veto`. A branch failure recorded in the same instant as a sibling's stage completion may let that sibling start its next stage. An input submitted while the run is inside a group resumes the waiting branch.

Example: branch A finishes at t+3 and branch B at t+7, so the join is at t+7. If A fails at t+2, the group fails at t+2, B's current stage finishes, and B's next stage doesn't start.

### 4.65 Fan-out isolation

A branch's prompt is built only from:
- the stage's declared `inputs` (pinned versions);
- context providers, called with `branch` set;
- the branch agent's own prompt.

The runner fails a branch whose rendered input contains a sibling branch's output text: `FANOUT_ISOLATION_BROKEN`. Only sibling outputs of at least 32 characters (trimmed) are compared, so short answers such as `yes` or `{}` don't trip it. Disagreement findings of an aggregate are stored on its stage execution (with veto findings). A fan-out stage may not declare `routes` or `on_invalid.then: escalate` (`DEF_STAGE_KIND`). Branches never see each other's outputs, even after a retry.

### 4.66 Revision cap

Every backward transition adds 1 to `run.revisions`: a loop, `on_reject`, a route to an earlier stage, or a veto `to:`. A transition that would make `revisions > limits.revisions` is refused, and `then` applies.

Example: `revisions: 2`. Route to `research` (1), then route to `write` (2), then another route would make 3 → refused, `then: escalate`. Per-loop `max_iterations` still applies on its own. Approving the escalation re-runs the stage; a further backward transition is refused again, so every revision past the cap needs a decision. A route is read from the output's top-level `route` field; an invalid or missing route fails the stage with `VALIDATION`. A veto agent answers `{"findings":[…]}` or `{"approve":true}`.

### 4.67 Variants

`count` 2–5. Each variant is a separate agent run with the same inputs and the variant label in its prompt context. With `select: gate`, the stage waits on `variant_select` and the decider picks one. With an agent selector, the agent gets every variant and returns `{ choice: "v2", reason }`; a choice outside v1…vn fails with `VARIANT_CHOICE_INVALID`. Only the chosen variant moves downstream.

### 4.68 Map-reduce limits

- Items are 0-indexed and run in index order, at most `max_parallel` at a time.
- `max_failed` is either a count or a percentage of the total, rounded down.
- With `on_item_failure: skip`, the stage fails when failed items exceed `max_failed`.
- With `fail`, the first failure fails the stage.

Examples:
- 200 items, `max_failed: 5%` → 10 allowed. 3 failures pass, 11 fail the stage.
- 1,001 items with `max_items: 1000` → `MAP_TOO_MANY_ITEMS`, and no item runs.

### 4.69 Conversation

Turn order is the opener, then the participants in declared order, cycling. After each turn the stop checks run in this order: (1) the stop phrase, case-insensitive, appears in the reply; (2) the stop guard passes on the transcript; (3) `turns = max_turns`.

Example: 2 participants, `max_turns: 6`, phrase "goodbye". Customer, agent, customer, then the agent says "Goodbye" → stops at turn 4.

### 4.70 Follow-ups

`after` is an absolute duration from the run's `completed_at`: `1d` = 24 h, and no timezone applies. A delayed job's `run_after` is set to `completed_at + after`. A follow-up that fails retries as a normal job and never changes the run's status. A follow-up stage is a plain agent stage with an output (no gate, parallel, fan-out, variants or veto), never reached by the main flow (`DEF_FOLLOWUP_STAGE`); its prompt may use `{{OUTCOME_SCORES}}` and `{{VARIANT}}` (the label). Its outputs are artifact versions with variant `followup:<duration>`, and its stage execution carries the same label. Cancelling a completed run that has pending follow-ups cancels them and returns 200 (the run stays `completed`); with none pending it is 409 `RUN_NOT_ACTIVE`.

Example: completed 2026-12-01 18:00Z with `[1d, 5d, 20d]` → due 2026-12-02 18:00Z, 2026-12-06 18:00Z and 2026-12-21 18:00Z.

### 4.71 Schedules

- **Format:** cron has 5 fields (minute, hour, day of month, month, day of week) and is evaluated in the schedule's IANA timezone.
- **DST:** a time that doesn't exist (spring forward) fires at the next valid minute. A time that occurs twice (fall back) fires once, at the first.
- **Catch-up:** missed fires after downtime fire once, then resume.
- **Record:** every fire writes one `SCHEDULE_FIRED` event (`scheduleId`, `pipelineId`, `dueAt`, `outcome`: `started`, `skipped`, `submitted` or `failed`, `runId`, `errorCode`). A schedule whose pipeline declares `on_active: submit` and has a value for its `trigger.input` submits it to the active run instead of skipping.
- **Firing:** the background sweeper claims each due schedule with a guarded update of `next_fire_at` and queues `trigger.fire` in the same transaction, so a fire runs once across instances. A schedule whose timezone the host can't resolve is paused.

Example: `0 6 * * 1` in Europe/London fires every Monday at 06:00 local, which is 06:00Z in winter and 05:00Z in summer.

### 4.72 Webhook signature

- **Header:** `X-Tevrin-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`.
- **Delivery id:** a separate `X-Tevrin-Delivery: <uuid>` header.
- **Checks:** `|now − t| ≤ 300 s`, a constant-time compare, and both the current and the previous secret are accepted (24 h rotation overlap). A delivery id already seen in the last 24 h is a replay.

- **Limits:** the raw body is at most 256 KB (413, `VALIDATION`). An unknown, deleted or malformed webhook id gets the same 401 as a bad signature. A missing or invalid `X-Tevrin-Delivery` is 400 `VALIDATION`. The delivery is claimed only after the signature verifies.

Example: secret `whsec_test`, t = 1764590400, body `{"a":1}` → v1 = HMAC-SHA256("whsec_test", `1764590400.{"a":1}`). The test vector is computed once and committed in the tests.

### 4.73 Tool tiers and approval in the middle of a stage

Only `read` and `write` tools run straight away, and only within `max_tier`. A destructive call goes through these states: `pending_approval` (state saved, gate raised, job deferred) → `approved` (the tool runs once, and its result is appended to the saved messages) or `denied` (the denial text is appended) → `resumed` (the loop continues from the saved messages). A redelivered job in `pending_approval` re-defers without calling the model. The saved state is redacted and capped at 1 MB (same cap as stored inputs, D-0.7.0-47). A loop that would need more fails with `TOOL_STATE_TOO_LARGE`.

### 4.74 Outcome scoring

The `probability_brier` kind needs `prediction` in [0, 1] and `value` in {0, 1}. The score is (prediction − value)².

Examples:
- prediction 0.7, outcome 1 → 0.09;
- prediction 0.2, outcome 1 → 0.64.

Mean scores in prompt stats are plain means over the scored targets. `number` scores are stored as given.

### 4.75 Feedback injection

Entries for (pipeline, agent key, project) are taken newest first, up to `limit` (default 10, maximum 50) and capped at 4,000 characters in total (oldest dropped first). Each is rendered as `- [<date> <source>] <text>`. Deleted entries are never injected. The placeholder is optional in templates (`{{AGENT_FEEDBACK?}}`), so a first run with no entries renders it empty. A stage writes one entry per string, or object with a `text` field, in its output field; the writes are idempotent on redelivery. A resumed agent run keeps the entry ids recorded for its first prompt.

### 4.76 Source-of-truth pinning

At run start, each `scope: project` artifact is pinned to its newest locked version. Every stage reads that version.

Example: master CV v2 is locked when the run starts. A user locks v3 while the run is at the fit scorer. The CV tailor still reads v2, and a run started afterwards pins v3.

### 4.77 Rename compatibility

| Old | New | Read old until |
| --- | --- | --- |
| `aide_pat_…` tokens | `tevrin_pat_…` | 0.9.0 |
| `aide.auth` cookie | `tevrin.auth` (reissued on first use) | 0.9.0 |
| `aide-plan` block in plans | `tevrin-plan` | always (stored data) |
| `aide_merges` (stored, API) | `tevrin_merges` | input accepted until 0.9.0 |
| `AIDE_*` env vars read by Tevrin, `ConnectionStrings:Aide` | `TEVRIN_*`, `ConnectionStrings:Tevrin` | 0.9.0 (warning at startup). Not renamed: `AIDE_RUN_MIGRATIONS` in generated projects, the data-protection application name and purposes (D-0.8.0-16) |
| Activity source and meter `Aide`, tags `aide.*` | `Tevrin`, `tevrin.*` | not read (telemetry only; dashboards updated in the runbook) |
| Images `aide-api` and `aide-web`; k8s and Railway names; database `aide` | `tevrin-*`; database `tevrin` | renamed by the runbook (FR-327) |

## 5. Data

All migrations are additive (expand only), except one data rewrite (`aide_merges` → `tevrin_merges`).

| Table | Key fields | Constraints | Notes |
| --- | --- | --- | --- |
| `pipeline_runs` | + `revisions` (int), `outcome` (nullable string: `vetoed`), `pinned_artifacts` (jsonb: key → artifact id and version); `subject_id` nullable | unique (`pipeline_id`, `subject_id`) only where `subject_id` is not null | `status` adds `waiting_on_input` (generic) |
| `artifacts` | + `scope` (`run`/`project`), `variant` (nullable string), `item_index` (nullable int); `release_version_id` nullable | unique (`pipeline_run_id`, `type`, `version`, `variant`, `item_index`) for run scope; (`project_id`, `type`, `version`) for project scope | SDLC rows unchanged |
| `stage_executions` | + `branch` (nullable), `item_index` (nullable), `variant` (nullable), `followup` (nullable), `veto_findings` (jsonb), `parent_execution_id` (nullable, map item → its map stage execution) | index (`pipeline_run_id`, `stage_id`, `branch`, `item_index`); unique (`parent_execution_id`, `item_index`) where the parent is set | Map progress (done, failed, total) is counted from the parent's item rows (D-0.8.0-24) |
| `agent_runs` | + `tool_loop_state` (jsonb, redacted, ≤ 1 MB), `validator_findings` (jsonb), `feedback_ids` (uuid[]), `item_index` (nullable int), `turn_index` (nullable int, 1-based), `turn_role` (nullable) | | Turn columns let the timeline show a conversation while it runs (D-0.8.0-24) |
| `pipeline_schedules` | `id`, `project_id`, `pipeline_id`, `cron`, `timezone`, `inputs` jsonb, `paused`, `next_fire_at`, `last_fire_at`, `created_by` | index (`next_fire_at`) where not paused | |
| `pipeline_webhooks` | `id`, `project_id`, `pipeline_id`, `secret` (encrypted, `ISecretProtector`), `previous_secret`, `previous_secret_expires_at`, `created_by`, `xmin` | | The secret is never returned after creation |
| `webhook_deliveries` | (`webhook_id`, `delivery_id`) (PK), `received_at`, `status`, `run_id` | retention 30 days | Replay check uses the last 24 h; an older row with the same key is reused (D-0.8.0-18) |
| `agent_feedback` | `id`, `pipeline_id`, `agent_key`, `project_id`, `source`, `text` (≤ 2,000 chars), `run_id?`, `agent_run_id?`, `created_by?`, `created_at`, `deleted_at?` | index (`project_id`, `pipeline_id`, `agent_key`, `created_at desc`) | Soft delete |
| `outcome_scores` | `id`, `pipeline_run_id`, `agent_run_id?`, `artifact_id?`, `metric`, `kind`, `value`, `prediction?`, `score`, `observed_at`, `source`, `created_by` | exactly one target | |
| `approval_gates` | `type` adds `tool_approval`, `external_action`, `variant_select`; + `payload` (jsonb, redacted) | | |

Retention is unchanged except for webhook deliveries (30 days).

## 6. API

All existing endpoints keep their routes. The one breaking value change is `mergeStrategy` (D-0.8.0-10). The contract and client are regenerated.

| Method | Route | Response | Errors | Auth |
| --- | --- | --- | --- | --- |
| POST | `/api/projects/{id}/pipeline-runs` | 201 run | `RUN_CONTROLLED_BY_HOST → 409`, `RUN_ALREADY_ACTIVE → 409`, `SOURCE_ARTIFACT_MISSING → 409`, `INPUT_INVALID → 400`, `VALIDATION → 400`, `NOT_FOUND → 404` | Contributor |
| POST | `/api/pipeline-runs/{runId}/cancel` · `/rollback` · `/retry` | 200 run | `RUN_NOT_ACTIVE → 409`, `INVALID_TARGET_VERSION → 400`, `RUN_CONTROLLED_BY_HOST → 409` | Owner (cancel, rollback) · Contributor (retry) |
| POST | `/api/pipeline-runs/{runId}/inputs/{artifactKey}` | 201 artifact version | `INPUT_INVALID → 400`, `RUN_NOT_ACTIVE → 409`, `NOT_FOUND → 404` | Contributor |
| GET · POST | `/api/projects/{id}/pipelines/{pipelineId}/artifacts/{key}` · `…/versions` | list · 201 version | `INPUT_INVALID → 400`, `VALIDATION → 400`, `NOT_FOUND → 404` | Viewer · Contributor |
| POST | `/api/projects/{id}/pipelines/{pipelineId}/artifacts/{key}/versions/{version}/lock` | 200 version | `ARTIFACT_NOT_DRAFT → 409`, `CONFLICT → 409`, `NOT_FOUND → 404` | Owner |
| GET · POST | `/api/projects/{id}/pipeline-schedules` | list · 201 | `SCHEDULE_INVALID → 400`, `TRIGGER_NOT_SUPPORTED → 409` | Viewer · Owner |
| PATCH · DELETE | `/api/projects/{id}/pipeline-schedules/{scheduleId}` | 200 · 204 | `SCHEDULE_INVALID → 400`, `NOT_FOUND → 404` | Owner |
| GET · POST | `/api/projects/{id}/pipeline-webhooks` | list (no secrets) · 201 `{ id, url, secret }` | `TRIGGER_NOT_SUPPORTED → 409` | Viewer · Owner |
| POST · DELETE | `/api/projects/{id}/pipeline-webhooks/{webhookId}/rotate` · `/{webhookId}` | 200 `{ secret }` · 204 | `NOT_FOUND → 404` | Owner |
| POST | `/api/hooks/pipelines/{webhookId}` | 202 `{ runId }` | `WEBHOOK_SIGNATURE_INVALID → 401`, `WEBHOOK_REPLAY → 409`, `INPUT_INVALID → 400`, `RUN_ALREADY_ACTIVE → 409` | signature |
| GET · POST | `/api/projects/{id}/pipelines/{pipelineId}/agents/{agentKey}/feedback` | list · 201 | `VALIDATION → 400` | Viewer · Contributor |
| DELETE | `/api/projects/{id}/agent-feedback/{feedbackId}` | 204 | `FORBIDDEN → 403` | Owner or author |
| GET · POST | `/api/pipeline-runs/{runId}/outcomes` | list · 201 score | `VALIDATION → 400`, `NOT_FOUND → 404` | Viewer · Contributor |
| GET | `/api/pipeline-runs/{runId}/stages/{stageId}/items?cursor=&limit=` | paged map items | `VALIDATION → 400` | Viewer |

Gate decisions for the new gate types use the existing gate endpoints. A `variant_select` approve takes `{ choice }`. Hub events add `stageBranchUpdated` and `mapProgress` on the project groups.

### Error codes

| Code | Status | Meaning | User-facing copy |
| --- | --- | --- | --- |
| `VARIANT_CHOICE_INVALID` | 400 | A `variant_select` approve (or a selector agent) chose a label outside v1…vn | "That isn't one of the stage's variants. Pick one of v1 to vn." |
| `RUN_CONTROLLED_BY_HOST` | 409 | This pipeline's runs are started and moved by its own pages | "This pipeline is run from its own pages, not from here." |
| `RUN_ALREADY_ACTIVE` | 409 | The project already has an active run of this pipeline | "This project already has a run in progress for this pipeline." |
| `RUN_NOT_ACTIVE` | 409 | The run has finished | "This run has already finished." |
| `INPUT_INVALID` | 400 | The input doesn't match its schema (details list paths) | "Some input values aren't valid. Check the highlighted fields." |
| `SOURCE_ARTIFACT_MISSING` | 409 | A project-wide artifact the run needs has no locked version | "Lock a version of {artifact} before starting a run." |
| `SCHEDULE_INVALID` | 400 | Bad cron or timezone | "That schedule isn't valid. Check the cron expression and timezone." |
| `TRIGGER_NOT_SUPPORTED` | 409 | The pipeline doesn't declare that trigger | "This pipeline can't be started that way." |
| `WEBHOOK_SIGNATURE_INVALID` | 401 | Bad, missing or stale signature | (API only) |
| `WEBHOOK_REPLAY` | 409 | Delivery id already received | (API only) |

Job and run failure codes, which aren't HTTP errors: `VALIDATOR_INVALID_OUTPUT`, `FANOUT_ISOLATION_BROKEN`, `VARIANT_CHOICE_INVALID`, `MAP_TOO_MANY_ITEMS`, `TOOL_STATE_TOO_LARGE` and `EXTERNAL_ACTION_REJECTED`. They appear on stage executions and in the timeline. The frontend maps every new code in `src/lib/errors.ts`.

## 7. Pages and UI

| Route | Change | States |
| --- | --- | --- |
| `/projects/[projectId]/pipeline` | **Start run** and **Submit input** dialogs (FR-362); lanes, map progress, variants, transcripts and follow-ups (FR-363); **Feedback** (FR-365) and **Triggers** (FR-366) sections | loading · empty · error · not found · forbidden (actions hidden by role) |
| `/projects/[projectId]/pipeline/replay` | Shows branch, item, variant and follow-up labels, and conversation turns | as 0.7.0 |
| `/gates` | Tool approval, external action and variant selection cards (FR-364) | as 0.7.0 |
| Account → API tokens | Tokens shown with their prefix (`tevrin_pat_`, or `aide_pat_` for old tokens) | as today |

Devices: desktop-first, usable at 360 px; lanes stack vertically on narrow screens. Accessibility: WCAG 2.2 AA, with the form editor labelled from the schema `title`/`description`. Styles follow the 0.6.0 design system.

## 8. Non-functional requirements

- **Behaviour:** the SDLC is unchanged apart from the rename (FR-328's mapped equality). Golden prompt files may change only where `aide` appears in a prompt, in the same re-baseline commit and under the same check.
- **Performance:**
  - one driver hop adds at most 2 row reads and 1 write beyond the stage's own work;
  - map stages respect `max_parallel` and the worker's concurrency, and 1,000 items queue in under 2 s;
  - schedule polling costs one indexed query per minute.
- **Security:**
  - webhook secrets are encrypted at rest, shown once, and compared in constant time;
  - tool arguments and saved tool-loop state are redacted with `SecretRedaction`;
  - destructive tools and external actions never run without an approved gate in any mode;
  - fan-out isolation is asserted at runtime;
  - the feedback text is the user's own, and it is redacted before injection.
- **Observability:** structured logs and spans per driver hop, branch, map item, conversation turn, tool approval, webhook delivery and schedule fire, all tagged `tevrin.*`.
- **Tests:**
  - no real LLM calls (rule 10);
  - every §4 example is a unit test;
  - every fixture runs end to end in `Tevrin.Engine.Tests`;
  - the coach pack has its own test project;
  - the integration tests cover the run-control endpoints, webhooks (signature, replay, rotation) and schedules (with a fake clock).
- **Compatibility:** every §4.77 row has a test reading the old form.

## 9. Environments

Unchanged: dev loop, Docker Desktop Kubernetes, Railway `preview` and `production`. The names change (FR-327), and the Railway and database rename is a manual runbook step for Ben. No new accounts or services. The webhook endpoint is public by design (signature-protected) and goes through the existing web proxy. No new NuGet packages are expected: cron parsing is a small internal parser (D-0.8.0-11).

## 10. Decisions and assumptions

| ID | Decision | By | Status |
| --- | --- | --- | --- |
| D-0.8.0-1 | Full rename: code, config, images, infrastructure and wire names (FR-323–FR-329) | Ben (interview) | agreed |
| D-0.8.0-2 | The coach pack is hosted in Tevrin, and a project owns each run (an athlete or training block); auth, permissions, worker and engine UI are reused | Ben (interview) | agreed |
| D-0.8.0-3 | Coach inputs arrive through the API, a Start run upload in the engine UI, and a signed webhook | Ben (interview) | agreed |
| D-0.8.0-4 | Engine features for the other packs are proven on fixture pipelines only; no other product pack | Ben (interview) | agreed |
| D-0.8.0-5 | One reviewed snapshot re-baseline for the rename (FR-328); `tevrin-plan` is written and `aide-plan` is still read | Ben (interview) | agreed |
| D-0.8.0-6 | Railway and database renames follow a runbook Ben runs; the SDLC stays host-controlled | Ben (interview) / Claude | agreed / assumed |
| D-0.8.0-7 | Old names are read through 0.8.x and removed in 0.9.0 (§4.77); the project token budget and audit hooks apply to every pipeline | Ben (interview) / Claude | agreed / assumed |
| D-0.8.0-8 | Run control is declared by the pack in C# (`RunControl`), not in YAML, so the SDLC hash doesn't change | Claude | assumed |
| D-0.8.0-9 | Committed migration files may change only by the mechanical rename substitutions, checked by CI (amends rule 5 once) | Claude (follows D-0.8.0-1) | assumed |
| D-0.8.0-10 | Breaking contract change: `mergeStrategy` returns `tevrin_merges` and accepts `aide_merges` as input | Claude (follows D-0.8.0-1) | assumed |
| D-0.8.0-11 | Cron is parsed by a small internal 5-field parser with IANA timezones (`TimeZoneInfo`), not a new package | Claude | assumed |
| D-0.8.0-12 | Velocity rule thresholds are the §4.63 defaults, overridable per athlete (`athlete_profile.velocity`) | Claude | assumed (Q1) |
| D-0.8.0-13 | Destructive tools and external actions always need an Owner-approved gate, autonomous mode included; no decision within the timer counts as reject | Claude | assumed |
| D-0.8.0-14 | Conversation stages allow 2–6 turn-based participants; unordered multi-agent "economy" interaction stays out | Claude | assumed |
| D-0.8.0-15 | The project names stay `Tevrin.Api`, `Tevrin.Domain` and `Tevrin.Infrastructure` (direct mapping), even though `Tevrin.Domain` is the SDLC/product domain next to `Tevrin.Engine.Domain` | Claude (from the brief) | assumed |
| D-0.8.0-16 | `AIDE_RUN_MIGRATIONS` keeps its name: Tevrin's API doesn't read it (it migrates through the efbundle job), and it is the variable Tevrin sets in the projects it generates, whose own scripts read it, so renaming it would stop their migrations. It goes on the FR-329 allowlist; the data-protection application name `aide` and purpose strings `Aide.Secrets.*` stay too, since renaming them breaks decryption of stored secrets and cookies | Claude (M65c) | assumed |
| D-0.8.0-17 | The generic workload, service and job names (`api`, `web`, `db`, `db-migrate` in Kubernetes; `api`, `web`, `postgres` on Railway) keep their names: they carry no product name, and the web proxy (`BACKEND_URL=http://api:8080`), Railway's private network (`api.railway.internal`) and the `${{postgres.*}}` references address them by name. FR-327 renames the namespace, images, data PVC, labels, Railway project, variables and database | Claude (M65e) | assumed |
| D-0.8.0-18 | Uniqueness for runs without a subject (FR-334): the 0.7.0 unique indexes on open runs, artifact versions and pending gates apply only to rows with a subject (SDLC). Subject-less rows are unique per run (artifacts: `pipeline_run_id`, `type`, `version`; pending gates: `pipeline_run_id`, `type`), and the one-active-run rule (FR-331, `concurrency.runs`) is enforced in code under an advisory lock. Webhook deliveries are keyed by (`webhook_id`, `delivery_id`), so ids don't collide across webhooks and a sender's redelivery after 24 h is accepted. The validator code `DEF_TRIGGER_NOT_SUPPORTED` now means a trigger option its kind doesn't take (e.g. `cron` in a definition, where cron belongs to the per-project schedule), since schedule and webhook triggers run from 0.8.0 | Claude (M66 review) | assumed |
| D-0.8.0-19 | Source-of-truth artifacts (FR-352) are created and locked through project-scope endpoints (§6): Contributors add versions, Owners lock them (as SDLC artifact approval). A project-scope history is keyed by (project, artifact key), so pipelines in one project that declare the same key share it. Pipelines that declare the same project-scope key must declare the same format and schema; the registry refuses a mismatch at load (`DEF_SCOPE_INPUT`). A new version supersedes open drafts, and a version older than the newest locked one can't be locked. A run's start problem `SOURCE_ARTIFACT_MISSING` carries the artifact title in `reason` | Claude (M67) | assumed |
| D-0.8.0-20 | The coach pack's id is `coach`, so its guard is `coach.velocity-load-rules` (guard names start with the pack id). A definition may declare several triggers as a list (`triggers: [...]`, at most one webhook and one schedule) instead of the single `trigger:`; the coach declares manual, webhook (`session_log`) and schedule. The guard reads the run's pinned `athlete_profile` and its `session_log` versions through a pack-declared inputs source the host implements. Velocity override keys: `maxWeeklyIncreasePct`, `minHoursBetweenHard`, `maxHardPerWeek`, `taperPctWithin14Days`, `taperPctWithin7Days`, `minRestDaysPerWeek`; plan sessions are compared by date. The guard returns one finding per broken instance (each week or pair); a plan week ends on its first session date + 6 days; breach percentages round up; a plan session missing the volume field of the athlete's unit is an R1 finding, and R1 compares with the most recent earlier week with volume | Claude (M67) | assumed |
| D-0.8.0-21 | Flow construct syntax (FR-343–FR-349): a parallel group is a stage entry with `parallel:` whose branch stages are nested (plain agent, handler or manual stages: no transitions, gates, routes, aggregates or nested groups); `routes`, `on_veto.to` and `aggregate.from` target top-level stages only; `on_veto` is required with `veto`; fan-out `count` is 2–10, variants `count` 2–5, `limits.revisions` 1–100. New codes: `DEF_BRANCH_INPUT`, `DEF_ROUTE_TARGET` (unknown, later or self targets of `routes` and `on_veto.to`), `DEF_FANOUT_AGENT` (an agent key a fan-out, veto or variant selector uses that the stage doesn't bind); combining two of `parallel`/`fan_out`/`variants` on one stage, or a construct on the wrong stage kind, is `DEF_STAGE_KIND` | Claude (M68) | assumed |
| D-0.8.0-23 | Scale construct syntax (FR-355–FR-357): map and conversation stages have no `agent:`/`handler:` of their own, need an `output`, and their agents are `agents:` bindings (`DEF_FANOUT_AGENT` otherwise); `reduce` needs `map`; neither construct is allowed inside a parallel branch. A bare `over: <key>` means the artifact is the array. Limits: `max_parallel` 1–100, `max_items` 1–1000, `max_failed` `n` or `n%` (≤100%), `on_item_failure` defaults to `skip`. Conversation `stop` is optional (exactly one of `guard`/`phrase`), `opener` required, `max_turns` 1–100, an agent takes part once. `from_runs` is an `inputs:` entry `- from_runs: {…}`; `window` uses the follow-up duration format; `status` is completed, failed or cancelled (default completed) | Claude (M70) | assumed |
| D-0.8.0-24 | Map and conversation data (FR-355, FR-356): a map item's stage execution points at its map stage execution (`parent_execution_id`), unique per item index, so the driver can open every item up front and count progress by grouping the items; no counters on the parent. Conversation turns record `turn_index`/`turn_role` on their agent runs | Claude (M70) | assumed |
| D-0.8.0-25 | Map driver (FR-355): every item execution opens up front and at most `max_parallel` run, queued in index order as items end; the last item to end re-queues the map execution, which runs `reduce` with the FR-346 list (labels `item-<i>`). Zero items reduce over an empty list and the stage completes. A failed map leaves the run active like fan-out, and a retry reopens the failed items. Item agents get `{{MAP_ITEM}}` and `{{MAP_INDEX}}`, the reduce agent `{{<OUTPUT>_ALL}}`; item isolation reuses `FANOUT_ISOLATION_BROKEN`. Item handlers can't return Waiting (the item fails). `StageHandlerResult.Output` is added (additive pack contract) so handler items and reduces store outputs. Item rows don't count as iterations and aren't the retry, gate or input re-queue target | Claude (M70) | assumed |
| D-0.8.0-26 | Conversation driver (FR-356): one `stage.run` job per turn on the stage's own execution; the transcript is rebuilt from the accepted turn agent runs of the current stage entry (no per-turn artifact) and stored once as the stage output on stop. A participant's reply is its output text, or the `text` field of a `{ text }` JSON answer. Participants get `{{TRANSCRIPT}}` (one block per turn, `(no turns yet)` before turn 1) and `{{ROLE}}`, plus the stage's and their binding's inputs. The stop guard gets the transcript as the stage output. A failed turn fails the job and the run stays active; a retry resumes at that turn and keeps earlier turns. No new turn starts while the run has a pending gate; approving an escalation reruns the turn from attempt 1 | Claude (M70) | assumed |
| D-0.8.0-22 | Validator agents (FR-350): each validator is its own recorded agent run, keyed per attempt; a `reject` verdict with no `error` findings still rejects (with a generic reason); the verdict shape is checked in code. Outcome scores (FR-353): `source` is required, and a `number` score may not carry a `prediction`; prompt-comparison means average each target's scores first, so a target scored twice counts once; artifact targets are attributed to their producing agent run | Claude (M69) | assumed |

## 11. Open questions

| ID | Question | Who | Blocks |
| --- | --- | --- | --- |
| Q1 | Are the Velocity defaults right (+10% weekly, 48 h between hard sessions, ≤ 3 hard per week, taper 70%/50%, one rest day)? Does Impulse use km or minutes? | Ben | nothing (defaults apply, overridable) |
| Q2 | Will the Impulse app send session logs by webhook? If so, what payload shape, so the `session_log` schema can match it? | Ben | nothing (the schema in FR-339 is used until then) |
| Q3 | Which models should the coach stages use (the research notes suggest Sonnet, Opus and Haiku), or should they follow the project's LLM settings? | Ben | nothing (project settings apply) |
| Q4 | Is 1,000 the right cap for map items and windowed runs? | Ben | nothing (default applies) |

## 12. Milestones

1. **M65 Rename** (FR-323–FR-329): projects, namespaces and types, the mechanical migration check, config fallbacks, wire identifiers with read-both, images, k8s and IaC, the runbook, the re-baseline commit, the leftover-name test and docs. It goes first, so all new code uses the new names. The snapshots are unchanged after the re-baseline.
2. **M66 Run control and triggers** (after M65; FR-330–FR-337): `RunControl`, the start, cancel, rollback, retry and input endpoints, the `stage.run` driver, runs without a subject, schedules and webhooks, and `fixture-linear`.
3. **M67 Coach pack** (after M66; FR-338–FR-342, FR-352 for `athlete_profile`): the pack, guards, tests, docs guide and the end-to-end worked example. FR-352 (source-of-truth artifacts) is built here, because the coach needs it.
4. **M68 Flow constructs** (after M66; FR-343–FR-349): parallel, veto, fan-out, aggregate, routes, the revision cap and variants, with their fixtures.
5. **M69 Quality and learning** (after M68; FR-350, FR-351, FR-353, FR-354): the validator agent, feedback store, outcome scoring and follow-ups, with their fixtures.
6. **M70 Scale and conversation** (after M68; FR-355–FR-357): map-reduce, conversation and windowed run inputs, with their fixtures.
7. **M71 Tools and safety** (after M66; FR-358–FR-361): tool tiers, approval in the middle of a stage, and external-action gates, with their fixtures.
8. **M72 Engine UI** (after M67–M71; FR-362–FR-366): Start run and Submit input, the construct views, the new gate cards, scores and feedback, and triggers, with MSW tests against every fixture definition.
9. **M73 Release 0.8.0** (after M72, on Ben's confirmation; FR-367): changelog date, guides, history page, and SPEC/PROGRESS to `docs/0.8.0/`. Manual checks for Ben:
   - the rename runbook on preview, then on production;
   - old tokens and sessions still work after the deploy;
   - a coach run from the UI and from a signed webhook;
   - a weekly schedule fire;
   - the Docker Desktop redeploy and smoke test under the new names.
