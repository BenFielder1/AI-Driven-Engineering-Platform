# Tevrin — Specification (0.7.0)

2026-11-17 · Ben Fielder · **0.7.0**, built on the 0.6.1 codebase in this repo. Earlier versions: [`docs/0.1.0/SPEC.md`](docs/0.1.0/SPEC.md) … [`docs/0.6.1/SPEC.md`](docs/0.6.1/SPEC.md). Stack: [`STACK.md`](STACK.md). Source: Ben's 0.7.0 brief (2026-11-17), the research notes in [`docs/0.7.0/tevrin-engine-extraction.md`](docs/0.7.0/tevrin-engine-extraction.md) (all suggestions, order-of-work steps 1–5), the interview answers in §10, and a survey of the lifecycle, gate, job, agent and artifact code.

> **How this spec relates to 0.1.0–0.6.1.** 0.7.0 is an increment on the released 0.6.1 app. **Everything in the earlier specs stays in force unless this document amends it.** Amended items are listed in §3.0 and marked *(amends …)*. New requirements continue from **FR-260**, domain rules from **§4.49**, decisions use **`D-0.7.0-n`**, and milestones continue from **M54**. Earlier tests stay green unless an amendment changes what they assert; then update the test and name the amendment in it.

## 1. Overview

0.7.0 extracts Tevrin's orchestration into a reusable **engine**: the state machine, job queue, approval gates, artifact store, LLM layer and run log. A pipeline is then described by a **definition** (YAML) plus a small **pack** of named C# extension points. Today's software lifecycle becomes the first and only pack, **SDLC**. Tevrin becomes a platform, but users see the same product.

The work follows steps 1–5 of the research notes:

1. **Characterisation tests.** Run the whole SDLC flow end to end on the Fake LLM provider and snapshot every transition, gate, artifact and job. Every later milestone keeps the snapshot unchanged.
2. **Lift out the generic pieces.** Move the job queue, LLM layer, artifact rules, gates, notifications, audit, redaction and run log into new `Tevrin.Engine.*` projects. Namespace moves only.
3. **String keys.** `AgentKind`, `ArtifactType` and `GateType` become string keys, with the current values as constants in the SDLC pack.
4. **Generic state machine.** A definition model, loader and validator, and a generic `Apply(run, transition)` with guards. The SDLC lifecycle is written as a definition plus guards. Old and new machines run side by side until they agree everywhere, then the old one is removed.
5. **Generic API and UI.** Definition-driven read endpoints and gate decisions, and engine views (run timeline, stage list, artifact viewer and diff, gate inbox, run replay) that work for any definition with no frontend code.

Success means:
- **The SDLC works exactly as in 0.6.1.** The characterisation snapshot from M54 is unchanged at the end of every milestone, and every 0.1.0–0.6.1 test stays green (renamed or moved where the code moved).
- `Tevrin.Engine.*` assemblies never reference `Aide.*` or `Tevrin.Pack.*`. An architecture test enforces it.
- The SDLC lifecycle is the YAML file `sdlc.pipeline.yaml` plus named guards and handlers. Nothing in the engine mentions a phase, a plan, a review or a release.
- `LifecycleStateMachine`, `PhaseOrder`, `GateRules.RequiredGate` and the `Phase`-keyed `InvalidationCascade` table no longer exist.
- A fixture definition in the engine tests (not a product pack) runs end to end: agent stage → code-guarded stage with retry and feedback → gate → agent stage. It also renders in the engine UI with no frontend change.
- Each run is pinned to its definition hash, so editing the YAML never changes a run already in progress.
- Every agent run records its input, output, tokens, latency, model and prompt version, so the run replay shows it. Each stage has timings and each run has a cost total.

**In scope (0.7.0):** characterisation tests (§3.1) · engine projects and the lift (§3.2) · string keys (§3.3) · pipeline definitions and packs (§3.4) · the generic state machine and switch-over (§3.5) · generic stage execution and run logging (§3.6) · generic API (§3.7) · engine UI (§3.8) · release housekeeping (§3.9).

**Out of scope (0.7.0):**
- **Any pack other than SDLC.** The coaching pack (Impulse) and the other candidate pipelines (debate desk, forecasting, QA lab, job applications, on-call triage, content studio, agent economy) are future work. Engine tests use fixture definitions only.
- Packaging (step 7): NuGet and npm packages, a `dotnet new tevrin-pack` template, and `tevrin pipeline validate` / `tevrin run start` in the CLI.
- Running schedule and webhook triggers. They are modelled and validated but refused at load (§4.49, D-0.7.0-3).
- Starting, advancing, cancelling or rolling back runs through the generic API. SDLC runs still start and move through the existing project endpoints (D-0.7.0-4).
- Parallel stages, fan-out, ensembles, map-reduce, agent-to-agent conversation, tool permission tiers, mid-stage approval of tool calls, outcome scoring and a per-agent feedback store. They are added when a pack needs them (research notes: "let the engine grow out of three real packs").
- Uploading or editing definitions at runtime. Definitions ship inside pack assemblies.
- Renaming the existing `Aide.*` projects, namespaces, tables or stored identifiers (0.6.0 §4.29 stays). Only new projects use `Tevrin.*`.
- Deciding the engine's licence (Q1).
- Any change to SDLC behaviour, prompts, copy, scoring or the review decision table.

## 2. Users and permissions

Unchanged roles. Engine views and endpoints use the project's existing permissions:

| Action | Who | Unauthorised |
| --- | --- | --- |
| Read pipeline definitions (`GET /api/pipelines…`) | Any signed-in user | 401 `UNAUTHENTICATED` |
| Read a run, its stages, timeline, artifacts, gates, replay | Anyone who can view the project (0.1.0 Viewer and up) | 404 `NOT_FOUND` for a project they can't see (as today) |
| Approve or reject a gate through the engine endpoints | Whoever may decide that gate today (the existing gate endpoint's rule, unchanged) | 403 `FORBIDDEN` |
| Gate inbox (`/gates`) | Signed-in users; it lists only gates on projects they can see | — |
| Read agent run inputs in the replay | Same as reading agent run outputs today (Runs page) | 404 `NOT_FOUND` |

Auth is unchanged (0.1.0–0.6.0: email and password, GitHub OAuth, API tokens).

## 3. Functional requirements

### 3.0 Amended earlier requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| CLAUDE.md rule 8, 0.1.0 §4.1 / FR-11 (lifecycle via the domain state machine, every transition unit-tested) | Lifecycle changes go only through the engine state machine running the SDLC definition. Every allowed and forbidden SDLC transition keeps a unit test, now against the definition | FR-287–FR-293 |
| 0.1.0 §4.1 phase order, status-follows-phase table; 0.5.0 §4.27 analysis first | Read from `sdlc.pipeline.yaml` (stages, `status:` per stage, start stages). Same values | FR-290, §4.57 |
| 0.1.0 §4.2 gates by approval mode (`GateRules.RequiredGate`) | A `gate:` block on the entered stage. Same table | FR-283, §4.53 |
| 0.1.0 §4.4 invalidation cascade (hard-coded table) | Derived from stage `inputs` and each artifact's `cascade` setting. Same result for every SDLC type | FR-289, §4.54 |
| 0.1.0 FR-23 agent kinds, artifact types, gate types (enums) | String keys. The same wire values, as constants in the SDLC pack | FR-269–FR-271 |
| 0.1.0 FR-13/17/20/27–29 agent job triggers | Generic trigger strings; `phase_entered` is still read and means `stage_entered` | FR-273 |
| 0.1.0 one execution job per project (`ux_jobs_project_id_active_execution`) | Declared in the definition's `concurrency` block. The index stays for the SDLC | FR-286 |
| 0.1.0 agent run record | Adds the run, the stage, the redacted rendered input and per-stage timings | FR-297 |
| CLAUDE.md Layout and Stack | New `Tevrin.Engine.*` and `Tevrin.Pack.Sdlc` projects, `frontend/src/engine/` | FR-262, FR-309 |

### 3.1 Characterisation tests (step 1)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-260 | Integration tests drive the full SDLC flow end to end on the Fake LLM, Git, deploy and tracker providers and record a **characterisation snapshot**: the ordered list of state transitions, gates (type, status, decision), artifact versions (type, version, status, content hash), jobs (type, trigger, outcome), agent runs (agent, prompt version, status) and project events, normalised by §4.58. Snapshots are committed JSON files under `backend/tests/Aide.IntegrationTests/Characterisation/Snapshots/` | backend |
| FR-261 | Each scenario in §4.58 has its own snapshot. A test fails with a readable diff when a run's snapshot differs from the committed one. Updating a snapshot needs an explicit `UPDATE_SNAPSHOTS=1` run, and no milestone after M54 may change a committed snapshot | backend |

**FR-260 acceptance**
- Given the strict scenario, when the test creates a project, approves each draft and gate, executes the plan, passes review and releases, then the snapshot lists every transition from `refinement` to `release`/`released` with checkpoints 1–6 (release is entered at checkpoint 6 and completing it adds none, 0.2.0 §4.10) and the gates `spec_approval`, `plan_approval` and `pre_release`, all approved.
- Given any scenario run twice, then both snapshots are byte-identical (no ids, timestamps or random values leak in).

**FR-261 acceptance**
- Given a code change that makes `plan_approval` also required in advisory mode, when the advisory scenario runs, then it fails and the diff shows the extra gate.
- Given CI on any commit after M54, then no file under `Snapshots/` has changed since the M54 commit (a CI step compares them with `git diff --exit-code <M54 sha> -- …`).

### 3.2 Engine projects and the lift (step 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-262 | New projects in `backend/Aide.slnx`: `Tevrin.Engine.Domain` (pure: definition model, state machine, guard, gate and artifact rules), `Tevrin.Engine.Runtime` (job queue, LLM providers, artifact store, gates, notifications, audit, run log, EF entity configurations), `Tevrin.Engine.Api` (generic endpoints and the SignalR contract), `Tevrin.Pack.Sdlc` (the SDLC definition, key constants, guards, handlers, the `IPipelinePack` class) and `tests/Tevrin.Engine.Tests` (engine tests with fixture definitions). `Aide.*` projects stay as the SDLC implementation and the host | backend |
| FR-263 | Architecture tests: (a) no `Tevrin.Engine.*` assembly references `Aide.*` or `Tevrin.Pack.*`; (b) `Tevrin.Engine.Domain` references no EF Core, ASP.NET Core or Npgsql; (c) `Tevrin.Engine.Runtime` references no ASP.NET Core; (d) the existing `Aide.Domain` rule stays. They run in `Tevrin.Engine.Tests` from M55 | backend |
| FR-264 | The Postgres job queue moves to the engine with no behaviour change: `JobWorker`, `JobContracts` (`IJobHandler`, `JobHandlerRegistration`, `JobDeferredException`, ended and succeeded hooks), `JobEnqueuer`, `JobPolicy` and the `Job` entity. `FOR UPDATE SKIP LOCKED`, leases, retries, dead letters and deferral are unchanged | backend |
| FR-265 | The LLM layer moves to the engine: `ILlmProvider`, `LlmRequest`, `LlmCompletion`, `LlmResolver`, `LlmStreaming`, `LlmTools`, the HTTP providers (Anthropic, OpenAI, Ollama), `Aide.Infrastructure/Llm/*` and `FakeLlmProvider`. The SDLC's canned fake responders (`FakeAgentResponders`, `FakePromptBlocks`, `FakeStackScripts`, `FakeToolScripts`) stay in the SDLC side and register with the engine's Fake provider | backend |
| FR-266 | The generic artifact rules move to the engine: `ArtifactIntegrity`, `ContentHash`, `LineDiff`, `RestoreRules`, `ArtifactRules` (the parts that don't name a type) and the `Artifact` entity. SDLC-only rules (`CodebaseSummaryRules`, `CodebaseSummaryWait`) stay | backend |
| FR-267 | Gate mechanics move to the engine: `GateFollowUp`, `GateResolution`, `GateTimers`, approval links, the notification channels (in-app, email, Slack, Teams, signed webhooks) and the notification sweeper, audit events, `SecretRedaction` and `PromptTemplate` | backend |
| FR-268 | `AgentRun` and its rules (`AgentRunRules`, `AgentRunWarnings`, `LlmOutputLimits`) move to the engine run log | backend |

**FR-262 acceptance**
- Given the solution after M55, then it builds with `-warnaserror` and the dependency arrows are only: Pack → Engine; `Aide.*` → Engine and Pack; Engine → nothing of ours.
- Given the characterisation suite after M55, then every snapshot is unchanged.

**FR-263 acceptance**
- Given someone adds `using Aide.Domain.Lifecycle;` in `Tevrin.Engine.Runtime`, when CI runs, then the architecture test fails and names the reference.

**FR-264–FR-268 acceptance**
- Given the moved code, then no public behaviour, table, column, job type string or wire value changes, and no migration is added.
- Given `dotnet build src/Aide.Api`, then `openapi/api.json` is unchanged. If a moved DTO's namespace changes a schema name, the client is regenerated and the frontend still type-checks.

### 3.3 String keys (step 3)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-269 | `AgentKind` is replaced by a string agent key. The SDLC pack defines `SdlcAgents` constants with today's wire values (`idea_refinement`, `spec_generator`, `plan_generator`, `code_generator`, `code_review`, `repository_analyst`, `stack_analyst`, `build_fix`). Prompt lookup uses the key (`Prompts/<key>.md`) | backend |
| FR-270 | `ArtifactType` is replaced by a string artifact key. `SdlcArtifacts` holds `project_request`, `technical_specification`, `implementation_plan`, `optimization_plan`, `codebase_summary`, `source_items`, `stack_profile` | backend |
| FR-271 | `GateType` is replaced by a string gate key. The engine owns `escalation` (every pipeline can escalate); `SdlcGates` holds `spec_approval`, `plan_approval`, `pre_release` | backend |
| FR-272 | Job types are engine constants `stage.run`, `agent.run`, `gate.timer`, `trigger.fire`, plus types a pack registers. The SDLC pack registers every 0.6.1 job type string unchanged (`idea_refinement`, `execution`, `review`, `build_check`, …). A job whose type has no registered handler fails at enqueue with an error in the log, not at claim | backend |
| FR-273 | `AgentJobTrigger` is replaced by trigger strings: `user_requested`, `stage_entered`, `gate_rejected`, `artifact_rejected`, `artifact_corrupted`, `validation_failed`. New payloads write `stage_entered`. Payloads that say `phase_entered` read as `stage_entered` | backend |
| FR-274 | The database keeps the same stored values. EF enum-to-string conversions become plain string columns. Any migration is limited to column types or lengths, and is reviewed for zero data change. API DTOs keep the same JSON values; the OpenAPI schemas change from enums to strings with the known values listed in the description (D-0.7.0-7) | backend + data + frontend |

**FR-269–FR-274 acceptance**
- Given the characterisation suite after M56, then every snapshot is unchanged.
- Given a 0.6.1 database with queued and dead-lettered jobs and agent runs, when M56's build starts on it, then every row reads back and every queued job runs (integration test seeded with 0.6.1-shaped rows, including a `phase_entered` payload).
- Given the regenerated client, then the frontend keeps narrow TypeScript unions for the SDLC keys (in `src/features/…` constants), so its type checks still catch typos.

### 3.4 Pipeline definitions and packs (step 4, part 1)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-275 | A **pipeline definition** model in `Tevrin.Engine.Domain` and a YAML loader in `Tevrin.Engine.Runtime` (YamlDotNet, safe parsing with no type tags). Top-level keys: `pipeline`, `version`, `title`, `trigger`, `approval_modes`, `default_approval_mode`, `start`, `artifacts`, `stages`, `statuses`, `status_changes`, `concurrency`, `settings`. §4.49 gives the full shape | backend |
| FR-276 | A validator rejects a definition with every problem listed at once, each with a code from §4.49 and the YAML path: parse errors, duplicate ids, unknown guard / handler / tool / job type / status / artifact / stage names, unreachable stages, loops with no cap, schema references that don't resolve, artifacts read but never produced, unsupported triggers | backend |
| FR-277 | Each definition has a **definition hash** (§4.50). The first time a hash is used, its canonical content is stored in `pipeline_definitions`. A run stores the hash it started with and always loads that stored definition (pinning) | backend + data |
| FR-278 | Pack registration: `IPipelinePack { string Id; void Register(IPackRegistry) }`. A pack registers its definitions (embedded YAML), prompts, JSON schemas, `IGuard`s, `IStageHandler`s, `IAgentTool`s, job handlers, agent context providers (`IAgentContextProvider`), invalidation hooks (`IInvalidationHook`) and run settings (`IRunSettingsProvider`). Names are namespaced by pack id (`sdlc.working-copy-prepared`) | backend |
| FR-279 | **Artifacts** in a definition: key, `title`, `format` (`markdown` or `json`), optional `schema` (JSON Schema file in the pack), `approval` (`required` or `auto_lock`) and `cascade` (`downstream`, `self_and_downstream` or `none`). Dependencies come from stage `inputs` (§4.54). When `schema` is set, an agent's output is validated against it before it's stored | backend |
| FR-280 | **Stages**: an id, `title`, and one kind: an **agent stage** (`agent: { key, prompt, model, tools[], job_type }`, `inputs`, `output`), a **code stage** (`handler: <pack>.<name>`, an `IStageHandler`), or a **manual stage** (no agent or handler; work happens through pack endpoints, as SDLC execution and release do today) | backend |
| FR-281 | **Transitions**: linear by default (`next` is the following stage). A stage may declare `on_success: <stage>`, `on_reject: <stage>` and `loop: { to: <earlier stage>, on: <outcome>, max_iterations: <int or { setting: <name> }> }` | backend |
| FR-282 | **Guards**: `requires:` before entering a stage (built-ins `artifact_locked: <key>`, `no_pending_gates: { except: [...] }`, and named `IGuard`s), and `validate:` after a stage's output (named `IGuard`s, plus the artifact schema), with `on_invalid: { retry_with_feedback, max_attempts, then: escalate | fail }` | backend |
| FR-283 | **Gates**: `gate: { type: <key>, modes: [...], approves: <artifact key or none>, approval_links: true|false }` on a stage means that gate must be approved to enter it in the listed approval modes (§4.53) | backend |
| FR-284 | **Triggers**: `trigger: { kind: manual | schedule | webhook, … }`. Only `manual` runs in 0.7.0; `schedule` and `webhook` parse and are refused by the validator with `DEF_TRIGGER_NOT_SUPPORTED` (D-0.7.0-3) | backend |
| FR-285 | **Statuses**: each stage has a `status:` (the pack's status while in it). `statuses:` declares the pack's custom statuses, each mapped to a generic run status (§4.57). `status_changes:` declares named status-only moves (`from`, `to`, optional `completes_run`, `error_code` to return when `from` doesn't match) | backend |
| FR-286 | **Concurrency**: `concurrency: { exclusive: [{ job_type, scope: project | run }] }`. The enqueuer refuses a second active job of that type in that scope. The SDLC declares `{ job_type: execution, scope: project }`, and the existing partial unique index stays as its database backstop | backend |
| FR-290 | The SDLC lifecycle is written as `Tevrin.Pack.Sdlc/Definitions/sdlc.pipeline.yaml` (§4.51 sketch), with `SdlcPack` registering the guards in §4.52, the existing job handlers and the pack's custom statuses and status changes. It passes the validator, and the app fails its readiness check (like `StackCatalogHealthCheck`) if any registered definition is invalid | backend |

**FR-276 acceptance**
- Given a definition whose `plan` stage requires `velocity.load-rules` and no pack registers it, then validation fails with `DEF_UNKNOWN_GUARD` at `stages[1].validate[0].guard`.
- Given a `loop` with no `max_iterations`, then `DEF_UNCAPPED_LOOP`. Given a stage nothing can reach, then `DEF_UNREACHABLE_STAGE`. Given three problems, then all three are reported in one result.

**FR-277 acceptance**
- Given a run started on hash H1, when the YAML is edited and the app restarts with hash H2, then the run keeps using H1's stages and gates, and a new run uses H2.
- Given H1 names a guard the current build no longer registers, then moving that run returns 409 `DEFINITION_UNAVAILABLE` and logs the missing names; reads still work.

**FR-290 acceptance**
- Given `sdlc.pipeline.yaml`, then loading and validating it gives no errors, and its stage order, statuses, gates and start stages equal 0.6.1's `PhaseOrder`, `StatusFor`, `RequiredGate` and `Start` results (unit test).

### 3.5 Generic state machine and switch-over (step 4, part 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-287 | `PipelineStateMachine.Apply(RunState, Transition, RunFacts, Definition)` in `Tevrin.Engine.Domain` returns the new stage, pack status, generic status and checkpoint, or a `DomainError`. Transition kinds: `advance`, `loop`, `rollback`, `fallback`, `status_change`, `noop` (§4.51). Pure, no I/O. Guards are evaluated before `Apply` by the runtime and passed in as `RunFacts` (named guard results plus built-in facts) | backend |
| FR-288 | `RequestAdvance` returns either an applied transition or "gate needed" (gate key), or `GATE_PENDING` when that gate is already pending, exactly as 0.6.1 does. Unmet `requires:` return `PHASE_PRECONDITION_FAILED` with every unmet reason in declared order (§4.52) | backend |
| FR-289 | The invalidation cascade is computed from the definition (§4.54). The SDLC's step resets (`AllSteps`, `StepsOfOtherPlans`) move to an SDLC `IInvalidationHook` | backend |
| FR-291 | **Parity mode** (M58 only): every lifecycle move in the app goes through a `ParityStateMachine` that calls both the old `LifecycleStateMachine` and the new engine with the SDLC definition, and throws in tests (logs an error in production) when their results differ in stage, status, checkpoint, error code or unmet reasons. A generated parity test also runs both machines over every combination in §4.56 | backend |
| FR-292 | **Switch-over**: once parity holds across the unit, integration and characterisation suites with zero differences, the app uses only the engine. The run (`pipeline_runs`) is the source of truth for stage, status and checkpoint. `projects.current_phase`, `status` and `checkpoint` stay as a copy written in the same transaction, so 0.6.1 queries and DTOs are unchanged (D-0.7.0-8) | backend + data |
| FR-293 | **Removal**: after switch-over, `LifecycleStateMachine`, `PhaseOrder`, `TransitionKind`, `GateRules.RequiredGate` / `GuardedEntry` / `GatedArtifact`, the `Phase`-keyed cascade table and `ParityStateMachine` are deleted. Every SDLC transition test in `Lifecycle/LifecycleStateMachineTests` is kept, rewritten against the engine and the SDLC definition, with the same cases and expected results (rule 8) | backend |
| FR-294 | **Runs**: a migration adds `pipeline_runs` (§5). The SDLC creates one run per release version: project creation and "new version" each start a run; the previous run ends `completed`. The migration backfills one run per existing release version (the current one from the project's phase, status and checkpoint; earlier ones `completed`/`released`) and links existing transitions, gates and agent runs to them | backend + data |

**FR-288 acceptance**
- Given advisory mode in `specification` with the spec locked, when advance is requested, then the outcome is "gate needed: `spec_approval`". Given that gate is pending, then 409 `GATE_PENDING`. (Same as 0.6.1.)

**FR-291 acceptance**
- Given the M58 commit, then the parity test reports 0 differences over all §4.56 combinations, and CI is green with parity mode on in every test.

**FR-292/FR-293 acceptance**
- Given the characterisation suite after FR-293, then every snapshot is unchanged.
- Given `grep -r "LifecycleStateMachine\|PhaseOrder" backend/src`, then nothing is found.

**FR-294 acceptance**
- Given a 0.6.1 database with a project in `review` at checkpoint 4 on its second release version, when the migration runs, then that project has two runs: the first `completed` (status `released`), the second at stage `review`, status `reviewing`, checkpoint 4, pinned to the SDLC definition hash; and its transitions, gates and agent runs point at the matching run.

### 3.6 Generic stage execution and run logging

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-295 | An engine `AgentStageRunner` runs any agent stage: load the pinned definition, collect the latest locked versions of the stage's `inputs`, ask registered `IAgentContextProvider`s for extra context, render the prompt, call the LLM through the resolver with the stage's tools, validate the output (FR-296), store it as a new draft (or auto-locked) artifact version, and record the agent run. The SDLC's repository and Git context (`AgentRepoContext`, iteration context, repo tools) become SDLC context providers and `IAgentTool`s. `AgentRunner`, `ArtifactAgentJob` and the SDLC agent job handlers call the engine runner; SDLC-specific job handlers (execution, repository and stack analysis, build check, release) stay in the SDLC side | backend |
| FR-296 | `validate:` on an agent stage: when a guard or the schema rejects the output, the stage reruns the agent with the findings appended as feedback (trigger `validation_failed`) up to `max_attempts`, then follows `then:` (`escalate` raises an `escalation` gate; `fail` marks the agent run and job failed). Where the SDLC validates output today (plan validation, build-fix plans), it is expressed this way with today's attempt counts and end result (§4.55) | backend |
| FR-297 | Run logging: `agent_runs` gains `pipeline_run_id`, `stage_id`, `input` (the rendered prompt and messages, redacted with `SecretRedaction`, capped at 1 MB with `input_truncated`) and `latency_ms` (time to first token, when streaming). A new `stage_executions` table records each entry into a stage (run, stage, iteration, checkpoint, entered and exited times, outcome). The run exposes a cost total (sum of its agent runs' `cost_estimate`) and a token total | backend + data |
| FR-298 | **Prompt comparison**: an endpoint groups a pipeline's agent runs by agent key and prompt version, with counts and average tokens, cost, latency, duration and failure rate (§6) | backend |

**FR-295 acceptance**
- Given the fixture definition `fixture-review` in `Tevrin.Engine.Tests` (draft → check → approve → summarise, with a fixture guard), when a run starts on the Fake provider, then the engine runs both agent stages, stores both artifacts and records both agent runs, with no SDLC code loaded.
- Given the characterisation suite after M59, then every snapshot is unchanged.

**FR-296 acceptance**
- Given §4.55's worked example, then the agent runs three times, the second and third prompts contain the guard's findings, and the run ends with a pending `escalation` gate.

**FR-297 acceptance**
- Given an agent run whose prompt contains `sk-ant-…`, then `input` stores it redacted.
- Given an SDLC review cycle, then `stage_executions` has rows for `execution` iteration 1, `review` iteration 1, `execution` iteration 2, `review` iteration 2, each with entered and exited times.

### 3.7 Generic API (step 5, part 1)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-299 | Definition endpoints: list registered pipelines (id, title, version, current hash, pack) and get a definition by hash (stages with titles, kinds, inputs, outputs, gates, transitions; artifacts with titles and formats; statuses) | backend |
| FR-300 | Run endpoints: list a project's runs; get a run (stage, pack status, generic status, checkpoint, hash, started and completed times, cost and token totals); list its stages with state (`pending`, `active`, `completed`, `skipped`), iteration count and timings; and a paged **timeline** that merges transitions, stage executions, agent runs, gate events and artifact versions in time order | backend |
| FR-301 | Run artifact and gate endpoints: list artifacts by key with their versions, get a version, diff two versions (`LineDiff`); list the run's gates; approve or reject a gate. Gate decisions call the same gate service as `/api/projects/{id}/gates/{gateId}/…`, with the same rules, errors, follow-ups and audit events | backend |
| FR-302 | Gate inbox endpoint: pending gates across every project the user can see, newest first, cursor-paged, filterable by pipeline and gate key. The live hub sends `pipelineRunUpdated`, `stageUpdated` and `gateUpdated` events on the existing project groups | backend |
| FR-303 | Replay endpoint: one run's timeline with each agent run's full input and output (permission as FR-300), for the replay view. Read-only; it never re-runs anything | backend |

**FR-301 acceptance**
- Given a pending `plan_approval` gate, when an Owner approves it through `/api/pipeline-runs/{runId}/gates/{gateId}/approve`, then the transition, events, notifications and snapshot are identical to approving it through the project endpoint.
- Given a Viewer, then 403 `FORBIDDEN`. Given a decided gate, then 409 `GATE_NOT_PENDING`.

**FR-300 acceptance**
- Given a run with 120 timeline entries, when the first page (50) is fetched and the cursor followed, then all 120 come back once each, in order.

### 3.8 Engine UI (step 5, part 2)

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-304 | `frontend/src/engine/` holds the engine UI (stand-in for the future `@tevrin/engine-ui` package): API hooks, `RunTimeline`, `StageList`, `ArtifactViewer`, `ArtifactDiff`, `GateInbox`, `GateDecision`, `RunReplay`, `PromptComparison`. An ESLint rule forbids imports from `src/features/**` and `src/app/**` into `src/engine/**`. Generic pieces used by both (diff view, Markdown view) move into `src/engine/` and the SDLC features import them from there | frontend |
| FR-305 | Every engine component renders from the definition and the run data only. Labels come from definition `title`s; there is no stage, artifact or gate name in `src/engine/` | frontend |
| FR-306 | A **Pipeline** tab on the project (`/projects/[projectId]/pipeline`) shows the current run's stage list, run summary (status, checkpoint, cost, tokens), timeline and artifacts, with a run picker for earlier runs. **Replay** (`/projects/[projectId]/pipeline/replay?runId=`) steps through the timeline one entry at a time, showing each agent run's input and output and each artifact version with its diff to the previous version | frontend |
| FR-307 | A **Gate inbox** page (`/gates`, nav item "Gates") lists pending gates across projects with approve and reject actions (comment required on reject, as today). Escalation gates link to the project, as their notifications do | frontend |
| FR-308 | The SDLC stage stepper and stage navigation read the stage list, order and titles from the SDLC definition instead of a hard-coded phase list. Every existing SDLC page, route and copy is unchanged | frontend |

**FR-305 acceptance**
- Given an MSW handler that serves the `fixture-review` definition and a run of it, when the Pipeline tab and replay render, then they show its stages, artifacts and gate, with no change to `src/engine/` (Vitest).

**FR-307 acceptance**
- Given two projects, one the user can't see, then the inbox lists only the visible project's gates. Given an empty inbox, then "No gates waiting for you". Given a load error, then the shared error state.

**FR-308 acceptance**
- Given every existing SDLC page test, then it passes unchanged. Given a project in `analysis`, then the stepper shows Analysis first, as in 0.6.1.

### 3.9 Release housekeeping

| ID | Requirement | Layer |
| --- | --- | --- |
| FR-309 | Version 0.7.0 in the three version sources; a changelog entry ("Pipeline engine": SDLC unchanged, Pipeline tab, gate inbox, replay); `CLAUDE.md` Layout, Stack and rule 8 updated for the engine projects; `STACK.md` notes YamlDotNet and the JSON Schema validator; a docs-site guide **Pipelines and packs** (engine architecture, definition reference from §4.49, writing a guard, the dependency rule) and an operations note on definition pinning. At release: `docs/history/0.7.0.md`, SPEC and PROGRESS to `docs/0.7.0/` | frontend + docs |

## 4. Domain rules

### 4.49 Definition shape and validation

```yaml
pipeline: <id>                # kebab-case, unique across packs
version: <int>
title: <text>
trigger: { kind: manual }     # schedule | webhook parse, refused in 0.7.0
approval_modes: [strict, advisory, autonomous]
default_approval_mode: advisory
start: [<stage id>, ...]      # first is the default; a pack IRunStartSelector picks among them
settings: [<name>, ...]       # values come from the pack's IRunSettingsProvider (per project)
artifacts:
  <key>: { title, format: markdown|json, schema?, approval: required|auto_lock, cascade: downstream|self_and_downstream|none }
statuses:
  <name>: { generic: active|running|waiting_on_gate|completed|failed|cancelled }
status_changes:
  <name>: { stage, from: [<status>], to: <status>, completes_run?: bool, error_code: <CODE> }
concurrency: { exclusive: [{ job_type, scope: project|run }] }
stages:
  - id: <id>
    title: <text>
    status: <status>
    agent?: { key, prompt, model?, tools?: [<pack>.<tool>], job_type? }   # job_type defaults to agent.run
    handler?: <pack>.<name>
    inputs?: [<artifact key>]
    output?: <artifact key>
    requires?: [{ artifact_locked: <key> } | { no_pending_gates: { except: [<gate>] } } | { guard: <pack>.<name> }]
    validate?: [{ guard: <pack>.<name> }]
    on_invalid?: { retry_with_feedback: bool, max_attempts: 1-10, then: escalate|fail }
    gate?: { type: <key>, modes: [...], approves?: <artifact key>, approval_links?: bool }
    on_success?: <stage> ; on_reject?: <stage>
    loop?: { to: <earlier stage>, on: <outcome>, max_iterations: <1-100> | { setting: <name> } }
```

Validator codes (reported together, each with its YAML path):

| Code | When |
| --- | --- |
| `DEF_PARSE` | YAML doesn't parse, or a key has the wrong type |
| `DEF_DUPLICATE_ID` | Two stages, artifacts, statuses or status changes share an id |
| `DEF_UNKNOWN_STAGE` / `DEF_UNKNOWN_ARTIFACT` / `DEF_UNKNOWN_STATUS` | A reference names something not declared |
| `DEF_UNKNOWN_GUARD` / `DEF_UNKNOWN_HANDLER` / `DEF_UNKNOWN_TOOL` / `DEF_UNKNOWN_JOB_TYPE` | No pack registered that name |
| `DEF_STAGE_KIND` | A stage has both `agent` and `handler` |
| `DEF_UNREACHABLE_STAGE` | No path from a `start` stage reaches it (via next, `on_success`, `on_reject`, `loop`) |
| `DEF_UNCAPPED_LOOP` | A `loop` (or an `on_reject` pointing backwards) has no `max_iterations` |
| `DEF_LOOP_FORWARD` | `loop.to` is not an earlier stage |
| `DEF_SCHEMA_NOT_FOUND` | `schema` names a file the pack doesn't embed, or it isn't valid JSON Schema |
| `DEF_ARTIFACT_NO_PRODUCER` | An artifact is in some `inputs` but no stage outputs it and no pack handler declares it |
| `DEF_UNKNOWN_SETTING` | `{ setting: x }` names a setting not in `settings` |
| `DEF_TRIGGER_NOT_SUPPORTED` | `trigger.kind` is `schedule` or `webhook` (0.7.0) |

Example: a definition with `loop: { to: draft }` on stage `check`, no `max_iterations`, and `validate: [{ guard: fixture.missing }]` → two errors: `DEF_UNCAPPED_LOOP` at `stages[1].loop` and `DEF_UNKNOWN_GUARD` at `stages[1].validate[0].guard`.

### 4.50 Definition hash

The hash is SHA-256 (lower-case hex) of the definition's **canonical JSON**: the parsed model serialised with sorted keys, no insignificant whitespace, LF line endings and UTF-8. Comments, key order and YAML formatting don't change it. Changing any value does.
- Two files that differ only in a comment → same hash.
- `max_attempts: 3` → `4` → different hash.

### 4.51 Generic transitions and the SDLC mapping

| Kind | From → to | Checkpoint | Notes |
| --- | --- | --- | --- |
| `advance` | stage → its `next` (or `on_success`) | +1 | `requires:` and the entry gate must be met |
| `loop` | stage → `loop.to` | unchanged | iteration +1; refused past the cap |
| `rollback` | any → an earlier or same stage | set to the target checkpoint | status becomes the pack's rollback status if declared, else the stage status |
| `fallback` | any → an earlier stage after a cascade | unchanged | status = the target stage's status |
| `status_change` | same stage, named in `status_changes` | unchanged | `from` must match, else the declared `error_code` |
| `noop` | same stage | unchanged | |

A new run starts at checkpoint 1. These are 0.6.1's checkpoint rules, made generic.

| 0.6.1 | 0.7.0 |
| --- | --- |
| `Advance` | `advance` |
| `ReviewCycle` (review → execution) | `loop` on `review` with `on: review_required`, `max_iterations: { setting: review_cycles }` |
| `Rollback` | `rollback` (SDLC rollback status `rolled_back`) |
| `InvalidationFallback` | `fallback` |
| `ReleaseNoop` | `noop` on `release` |
| `ReleaseCompleted` | `status_change` `release_completed`: `[releasing, awaiting_merge] → released`, `completes_run`, error `NOT_RELEASING` |
| `ReleaseAwaitingMerge` | `status_change` `release_awaiting_merge`: `releasing → awaiting_merge`, error `NOT_RELEASING` |
| `ReleaseMergeAbandoned` | `status_change` `release_merge_abandoned`: `awaiting_merge → releasing`, error `NOT_RELEASING` |
| `NewVersion` | a new run (start stage `refinement`, or `analysis` chosen by the SDLC start selector); errors `NOT_RELEASED`, `VERSION_IN_PROGRESS`, `STACK_INFER_NO_CODE` stay in the SDLC service |

`review_cycles` is `maxReviewCycles + extraReviewCycles` (FR-14), so the engine cap equals today's. The review decision itself (score, stall, escalation) stays in the SDLC `ReviewDecisionRules`; the cap is a backstop.

SDLC definition sketch:

```yaml
pipeline: sdlc
start: [refinement, analysis]
stages:
  - { id: analysis, title: Analysis, status: active, agent: { key: stack_analyst, job_type: stack_analysis }, output: stack_profile,
      on_success: refinement }
  - { id: refinement, title: Refinement, status: active, agent: { key: idea_refinement, job_type: idea_refinement },
      inputs: [source_items, codebase_summary], output: project_request,
      requires: [{ guard: sdlc.leaving-analysis }] }
  - { id: specification, title: Specification, status: active, agent: { key: spec_generator, job_type: spec_generation },
      inputs: [project_request, codebase_summary], output: technical_specification,
      requires: [{ artifact_locked: project_request }] }
  - { id: planning, title: Planning, status: active, agent: { key: plan_generator, job_type: plan_generation },
      inputs: [technical_specification, codebase_summary], output: implementation_plan,
      requires: [{ artifact_locked: technical_specification }],
      gate: { type: spec_approval, modes: [strict, advisory], approves: technical_specification, approval_links: true } }
  - { id: execution, title: Execution, status: building,
      requires: [{ artifact_locked: technical_specification }, { artifact_locked: implementation_plan },
                 { guard: sdlc.plan-has-steps }, { guard: sdlc.working-copy-prepared }],
      gate: { type: plan_approval, modes: [strict], approves: implementation_plan, approval_links: true } }
  - { id: review, title: Review, status: reviewing, agent: { key: code_review, job_type: review },
      inputs: [technical_specification, implementation_plan], output: optimization_plan,
      requires: [{ guard: sdlc.plan-steps-completed }],
      loop: { to: execution, on: review_required, max_iterations: { setting: review_cycles } } }
  - { id: release, title: Release, status: releasing,
      requires: [{ guard: sdlc.release-eligible }, { no_pending_gates: { except: [pre_release] } }],
      gate: { type: pre_release, modes: [strict, advisory, autonomous], approval_links: true } }
```

The exact keys (job types, inputs, guard names) are fixed during M57 to match 0.6.1 behaviour; this sketch is not normative where they differ. The loop's own entry check (optimisation plan locked with steps left, working copy prepared) is the guard `sdlc.review-cycle-ready` on the loop.

### 4.52 Entry checks

For entering stage S: evaluate S's `requires:` in declared order and collect every unmet reason (built-ins have fixed messages, guards return their own), then check S's gate when the run's approval mode is in `modes`. All unmet reasons are returned together. SDLC guards return 0.6.1's exact messages.

Example (0.6.1 parity): entering `execution` with the spec locked, the plan not locked, 0 steps and the working copy prepared → `PHASE_PRECONDITION_FAILED` with ["The implementation plan is not approved.", "The implementation plan has no steps."].

SDLC guards: `sdlc.leaving-analysis` (stack profile approved; composition pinned or generalist), `sdlc.plan-has-steps`, `sdlc.working-copy-prepared`, `sdlc.plan-steps-completed`, `sdlc.release-eligible` (latest review for this checkpoint is `release_eligible` or escalation `accept`), `sdlc.review-cycle-ready`. `analysis` is entered only as a start stage; `advance` into it is refused ("Analysis is only entered when a version that infers its stack starts.").

### 4.53 Entry gates

A stage's gate is required when the run's approval mode is in its `modes`. For the SDLC (equals 0.6.1 `RequiredGate`):

| Entering | strict | advisory | autonomous |
| --- | --- | --- | --- |
| planning | spec_approval | spec_approval | — |
| execution | plan_approval | — | — |
| release | pre_release | pre_release | pre_release |
| others | — | — | — |

Escalation gates are engine-owned and never guard entry.

### 4.54 Cascade from the definition

Artifact A **depends on** B when the stage that outputs A has B in its `inputs`, transitively. Approving a new version of B invalidates every artifact that depends on B, unless B's `cascade` is `none`. With `self_and_downstream`, the fallback stage includes B's own producing stage. The fallback stage is the earliest producing stage of the invalidated set, used only when the run is in a later stage. The pack's `IInvalidationHook` adds pack effects (SDLC: step resets).

SDLC result (must equal 0.6.1 §4.4, unit test per row):

| Approved | Invalidated | SDLC step reset | Fallback (when later) |
| --- | --- | --- | --- |
| project_request | technical_specification, implementation_plan, optimization_plan | all steps | specification |
| technical_specification | implementation_plan, optimization_plan | all steps | planning |
| implementation_plan | optimization_plan | steps of other plans | review |
| optimization_plan | — | none | — |
| codebase_summary (`none`) | — | none | — |
| source_items (`none`) | — | none | — |
| stack_profile (`self_and_downstream`) | project_request, technical_specification, implementation_plan, optimization_plan | all steps | analysis |

### 4.55 Validate and retry

Attempt n's output fails validation → if `retry_with_feedback` and n < `max_attempts`, rerun with the findings appended (trigger `validation_failed`); else follow `then`. Findings are the guard's messages plus schema errors (path and message), capped at 8,000 characters.

Example (fixture): `max_attempts: 3`, `then: escalate`. Attempt 1 fails "weekly volume +25% > 10%", attempt 2 fails the same, attempt 3 fails "taper missing" → 3 agent runs, 2 with feedback, then one pending `escalation` gate on the run. With `then: fail`, the third agent run and its job end `failed`.

SDLC: the plan generator's existing validation (`INVALID_PLAN`, one retry) becomes `validate: [{ guard: sdlc.plan-valid }]`, `on_invalid: { retry_with_feedback: true, max_attempts: 2, then: fail }`, with the same retry prompt text.

### 4.56 Parity combinations (M58)

The parity test enumerates: every stage × every pack status × every transition kind × approval mode × each boolean fact in `LifecycleSnapshot` toggled one at a time from both "all true" and "all false" × review decision (none, each value, stale checkpoint) × escalation outcome × pending gates (none, each gate, two gates) × target phase and target checkpoint (below, equal, above, invalid). For each, both machines must return the same success/failure, error code, unmet reasons, stage, status and checkpoint.

### 4.57 Run status

Generic run status: `completed`, `failed` and `cancelled` are final; otherwise `waiting_on_gate` when a gate on the run is pending, `running` when a job for the run is queued or running, else `active`. A pack status maps to a generic one through `statuses`.

SDLC statuses: `active`, `building`, `reviewing`, `releasing`, `rolled_back` and `awaiting_merge` → generic as computed above (`awaiting_merge` waits on a merge outside Tevrin, not a gate, so it's `active` or `running` while the merge poll job runs); `released` → `completed`. A run ends `completed` when `release_completed` applies; a new version's run starts at `refinement`/`analysis`, `active`, checkpoint 1.

### 4.58 Characterisation snapshots

Scenarios: (1) strict happy path; (2) advisory; (3) autonomous; (4) a gate rejected then approved (agent rerun with feedback); (5) a draft rejected; (6) two review cycles then release-eligible; (7) escalation `continue`, `rollback` and `accept`; (8) approving a new project request mid-execution (cascade and fallback); (9) rollback to checkpoint 2; (10) release refused, `awaiting_merge`, then merged; and merge abandoned; (11) new version; (12) stack inference (`analysis` → `refinement`); (13) build check with one failed attempt and a fix; (14) a corrupted artifact regenerated.

Normalisation: ids become `<type>#n` in first-seen order; timestamps, durations, tokens, costs and lease owners are dropped; content hashes stay (the Fake provider is deterministic); entries are ordered by creation sequence; JSON is written with sorted keys and LF.

### 4.59 Concurrency

Enqueueing a job type listed in `concurrency.exclusive` checks for an active (queued or running) job of that type in the same scope and refuses with `CONFLICT` semantics (the caller's existing error, e.g. `EXECUTION_IN_PROGRESS` where 0.6.1 had one). Types not listed may run in parallel.

## 5. Data

One additive migration in M56 only if column types change (FR-274), and these in M58–M59 (all additive, expand only):

| Table | Key fields | Constraints | Notes |
| --- | --- | --- | --- |
| `pipeline_definitions` | `hash` (PK, char 64), `pipeline_id`, `version`, `pack_id`, `canonical_json` (jsonb), `created_at` | PK on hash | Written on first use, never updated |
| `pipeline_runs` | `id` (v7), `project_id`, `pipeline_id`, `definition_hash` FK, `subject_id` (SDLC: release version id), `current_stage`, `pack_status`, `status` (generic), `checkpoint`, `approval_mode`, `iterations` jsonb (stage → count), `started_at`, `completed_at`, `xmin` | Unique (`pipeline_id`, `subject_id`); at most one non-final run per (`project_id`, `pipeline_id`) (partial unique index) | Source of truth for stage, status, checkpoint |
| `stage_executions` | `id` (v7), `pipeline_run_id`, `stage_id`, `iteration`, `checkpoint`, `entered_at`, `exited_at`, `outcome` | Index (`pipeline_run_id`, `entered_at`) | Per-stage timings |
| `state_transitions` | + `pipeline_run_id` (FK, backfilled, then not null), `kind` (string) | | `from_phase`/`to_phase` hold stage ids (same values) |
| `approval_gates` | + `pipeline_run_id` (FK, backfilled) | | `type` is a string key (same values) |
| `agent_runs` | + `pipeline_run_id` (nullable FK), `stage_id`, `input` (text), `input_truncated` (bool), `latency_ms` (nullable) | | Existing rows keep `input` null |
| `artifacts` | + `pipeline_run_id` (FK, backfilled) | | |

`projects.current_phase`, `status`, `checkpoint` stay, written with the run in one transaction (D-0.7.0-8). Removing them is a later contract step. Retention unchanged.

## 6. API

All existing endpoints are unchanged in route, behaviour and JSON values (FR-274 may turn enum schemas into strings). New, additive, contract and client regenerated:

| Method | Route | Response | Errors | Auth |
| --- | --- | --- | --- | --- |
| GET | `/api/pipelines` | `[{ id, title, version, hash, packId }]` | — | signed in |
| GET | `/api/pipelines/{pipelineId}/definitions/{hash}` | definition summary (FR-299) | `NOT_FOUND → 404` | signed in |
| GET | `/api/pipelines/{pipelineId}/prompt-stats?projectId=` | `[{ agentKey, promptVersion, runs, avgInputTokens, avgOutputTokens, avgCost, avgLatencyMs, avgDurationMs, failureRate }]` | `NOT_FOUND → 404` | project viewer (all visible projects when no `projectId`) |
| GET | `/api/projects/{id}/pipeline-runs` | runs, newest first | `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}` | run (FR-300) | `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/stages` | `[{ id, title, state, iterations, enteredAt, exitedAt, durationMs }]` | `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/timeline?cursor=&limit=` | `{ items: [{ at, kind, … }], next }` (limit 1–200, default 50) | `VALIDATION → 400`, `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/artifacts` | `[{ key, title, versions: [{ version, status, hash, createdAt }] }]` | `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/artifacts/{key}/versions/{version}` | artifact version | `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/artifacts/{key}/diff?from=&to=` | line diff | `VALIDATION → 400`, `NOT_FOUND → 404` | project viewer |
| GET | `/api/pipeline-runs/{runId}/gates` | gates | `NOT_FOUND → 404` | project viewer |
| POST | `/api/pipeline-runs/{runId}/gates/{gateId}/approve` · `/reject` | as the project gate endpoints | as the project gate endpoints, plus `DEFINITION_UNAVAILABLE → 409` | as the project gate endpoints |
| GET | `/api/pipeline-gates?status=pending&pipelineId=&gateType=&cursor=` | `{ items, next }` | `VALIDATION → 400` | signed in (visible projects only) |
| GET | `/api/pipeline-runs/{runId}/replay?cursor=&limit=` | timeline items with agent `input` and `output` | `NOT_FOUND → 404` | project viewer |

Live hub events (existing project groups): `pipelineRunUpdated` (run DTO), `stageUpdated` (stage DTO), `gateUpdated` (gate DTO).

### Error codes

| Code | Status | Meaning | User-facing copy |
| --- | --- | --- | --- |
| `DEFINITION_UNAVAILABLE` | 409 | The run's pinned definition names guards, handlers or tools this build doesn't register | "This run uses a pipeline definition this version of Tevrin can't run. Ask an admin to check the deployment." |

Every other error is an existing code with its existing status (`PHASE_PRECONDITION_FAILED`, `WRONG_PHASE`, `GATE_PENDING`, `GATE_NOT_PENDING`, `INVALID_TARGET_VERSION`, `NOT_RELEASING`, `NOT_RELEASED`, `VERSION_IN_PROGRESS`, `STACK_INFER_NO_CODE`, `INVALID_PLAN`, `VALIDATION`, `NOT_FOUND`, `FORBIDDEN`, `CONFLICT`). Definition validator codes (§4.49) are startup and readiness errors, not API errors.

## 7. Pages and UI

| Route | Page | Content | States |
| --- | --- | --- | --- |
| `/projects/[projectId]/pipeline` | Pipeline | Run picker, summary (pipeline title, status badge, checkpoint, cost, tokens, hash short form), stage list with states and timings, timeline, artifacts list → viewer and diff | loading · empty ("No runs yet") · error · not found |
| `/projects/[projectId]/pipeline/replay?runId=` | Replay | Previous/next stepper and a scrubber over the timeline; agent entries show input and output side by side (scrolling monospace blocks); artifact entries show the diff to the previous version | loading · error · not found |
| `/gates` | Gate inbox | Pending gates (project, pipeline, gate title, artifact link, age, overdue badge), approve / reject dialogs (reuse `gate-dialogs`) | loading · empty · error |
| Existing SDLC pages | unchanged | Stepper reads stages from the definition (FR-308) | unchanged |

Prompt comparison shows as a section on the Pipeline page ("Prompt versions"): a table per agent key. Devices: desktop-first, usable at 360px (tables scroll inside their card). Accessibility: WCAG 2.2 AA, keyboard-operable stepper and scrubber, live region for new timeline entries. Styles follow the 0.6.0 design system.

## 8. Non-functional requirements

- **Behaviour:** the characterisation snapshots are the contract. No milestone may change them (FR-261).
- **Performance:** definitions are parsed once per hash and cached. `Apply` is pure and allocation-light (< 1 ms). A lifecycle move adds at most one row read and write (`pipeline_runs`) over 0.6.1. Timeline pages answer in < 300 ms for a run with 5,000 entries (indexed by run and time).
- **Security:** definitions load only from pack assemblies (embedded resources), never from users. YAML parsing allows no type tags or anchors that expand beyond 1 MB. Agent `input` is redacted before storage and shown only to users who can see agent outputs. Engine endpoints check project permissions on every call; the gate inbox filters by visibility in SQL.
- **Observability:** structured logs for each transition (run, stage, kind, checkpoint, definition hash), each validation retry and each `DEFINITION_UNAVAILABLE`. An OpenTelemetry activity per stage execution and per agent run, tagged with run and stage.
- **Tests:** no test calls a real LLM (rule 10). Engine tests use fixture definitions and the Fake provider. Every §4 table row and worked example is a unit test.
- **Compatibility:** stored job payloads, artifact content, gate types, event types and wire values from 0.6.1 read unchanged. Rolling back the deploy to 0.6.1 after M58's migration is safe because the migration is expand-only and the project columns stay current.

## 9. Environments

Unchanged: dev loop, Docker Desktop Kubernetes, Railway `preview` and `production`. No new accounts, secrets or services. New NuGet dependencies: YamlDotNet and a JSON Schema validator (e.g. JsonSchema.Net), added to `Directory.Packages.props`.

## 10. Decisions and assumptions

| ID | Decision | By | Status |
| --- | --- | --- | --- |
| D-0.7.0-1 | Extract in place in this repo with new `Tevrin.*` projects; `Aide.*` stays as the SDLC implementation and host. Engine split into its own repo is a later step | Ben (2026-11-17) | agreed |
| D-0.7.0-2 | One feature version, 0.7.0, covering research steps 1–5. Step 7 (packaging) and every non-SDLC pack are out | Ben | agreed |
| D-0.7.0-3 | Triggers: all three modelled and validated; only `manual` runs. `schedule`/`webhook` refused with `DEF_TRIGGER_NOT_SUPPORTED` | Ben (interview) | agreed |
| D-0.7.0-4 | Generic API is read-only plus gate decisions; SDLC runs start and move through the existing project endpoints | Ben (interview) | agreed |
| D-0.7.0-5 | Generic UI is added beside the SDLC pages (Pipeline tab, replay, gate inbox); the SDLC stepper reads the definition; no SDLC page is rebuilt | Ben (interview) | agreed |
| D-0.7.0-6 | After parity, the old state machine is removed in 0.7.0; parity cases live on as definition tests | Ben (interview) | agreed |
| D-0.7.0-7 | String keys keep every stored and wire value. OpenAPI enums become strings with known values documented; the frontend keeps SDLC unions as constants | Claude | assumed |
| D-0.7.0-8 | `pipeline_runs` is the source of truth; `projects.current_phase/status/checkpoint` stay as a same-transaction copy so 0.6.1 queries, DTOs and a deploy rollback still work. Dropping them is a later contract step | Claude | assumed |
| D-0.7.0-9 | The engine's "run" is called a **pipeline run** (`pipeline_runs`, `/api/pipeline-runs`) because "runs" already means agent runs (`/api/projects/{id}/runs`, Runs page) | Claude | assumed |
| D-0.7.0-10 | One SDLC pipeline run per release version; "new version" starts a new run, and the old run ends `completed` | Claude | assumed |
| D-0.7.0-11 | Projects stay a platform concept owned by the host; every run belongs to a project, and engine permissions are the project's | Claude | assumed |
| D-0.7.0-12 | One database and one migration history: engine entity configurations live in `Tevrin.Engine.Runtime` and are applied by `AideDbContext`; migrations stay in `Aide.Infrastructure` | Claude | assumed |
| D-0.7.0-13 | Entry gates are declared on the stage they guard. A stage with only a gate is a valid manual approval stage | Claude | assumed |
| D-0.7.0-14 | The engine UI lives in `frontend/src/engine/` with a lint boundary instead of a separate npm workspace package (packaging is step 7) | Claude | assumed |
| D-0.7.0-15 | `gate.timer` and `trigger.fire` are reserved engine job types with no use in 0.7.0; the gate reminder/overdue sweep moves to the engine as a hosted sweep, unchanged | Claude | assumed |
| D-0.7.0-16 | Agent `input` is stored redacted and capped at 1 MB per run; older runs have none | Claude | assumed |
| D-0.7.0-17 | Run replay is read-only. Re-executing a run against a new prompt version is future work | Claude | assumed |
| D-0.7.0-18 | Parity mode throws in tests and logs in production, and exists only between M58's start and FR-293 | Claude | assumed |
| D-0.7.0-19 | Fan-out, parallel stages, tool permission tiers, conversation stages, outcome scoring and the feedback store wait for the packs that need them | research notes / Ben | agreed |

## 11. Open questions

| ID | Question | Who | Blocks |
| --- | --- | --- | --- |
| Q1 | Should the engine keep PolyForm Shield or get a more permissive licence when its packages are published? | Ben | nothing in 0.7.0 (packaging is out) |
| Q2 | Is 1 MB per agent run the right cap for stored inputs, given execution prompts with large repo context? | Ben | nothing (default applies) |
| Q3 | Should the gate inbox replace the Home dashboard's pending-gates card, or sit beside it? Default: beside it | Ben | nothing (default applies) |

## 12. Milestones

1. **M54 Version and characterisation** (FR-260, FR-261, FR-309 bump): version sources to 0.7.0, the §4.58 scenarios and committed snapshots, the snapshot-freeze CI step.
2. **M55 Engine projects and the lift** (after M54; FR-262–FR-268): new projects, architecture tests, namespace moves only. Snapshots unchanged.
3. **M56 String keys** (after M55; FR-269–FR-274): keys and SDLC constants, trigger strings, the 0.6.1-rows integration test, contract and client regenerated if schemas change. Snapshots unchanged.
4. **M57 Definitions and packs** (after M56; FR-275–FR-286, FR-290): model, loader, validator with every §4.49 row as a test, hashing (§4.50), pack registry and extension points, `sdlc.pipeline.yaml` and SDLC guards, readiness check, fixture definitions in `Tevrin.Engine.Tests`.
5. **M58 Generic state machine** (after M57; FR-287–FR-289, FR-291–FR-294): `PipelineStateMachine`, definition-driven cascade, `pipeline_runs` and the backfill migration, parity mode and the §4.56 parity test, switch-over, then removal of the old machine with the transition tests rewritten. Snapshots unchanged.
6. **M59 Stage execution and run logging** (after M58; FR-295–FR-298): `AgentStageRunner`, context providers and tools, validate/retry (§4.55), `stage_executions`, agent run input and latency, cost totals, prompt stats. Snapshots unchanged.
7. **M60 Generic API** (after M59; FR-299–FR-303): endpoints, hub events, contract and client regenerated, integration tests including approving a gate through both routes.
8. **M61 Engine UI** (after M60; FR-304–FR-308): `src/engine/` and its lint boundary, Pipeline tab, replay, gate inbox, prompt comparison, definition-driven stepper, MSW fixture-definition tests.
9. **M62 Release 0.7.0** (after M61, on Ben's confirmation): changelog date, docs guide "Pipelines and packs", `docs/history/0.7.0.md`, SPEC and PROGRESS to `docs/0.7.0/`, CLAUDE.md row and layout. Manual checks for Ben: a full SDLC project on preview behaves as on 0.6.1 (strict and advisory), an existing 0.6.1 project mid-review continues after the deploy, the Pipeline tab and replay on a real run, the gate inbox, and the Docker Desktop redeploy and smoke test.
