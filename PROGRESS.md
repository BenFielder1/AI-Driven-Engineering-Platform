# Build progress (0.7.0)

Spec: `SPEC.md` (0.7.0; 0.1.0–0.6.1 in `docs/`) · Research: `docs/0.7.0/tevrin-engine-extraction.md` · Stack: `STACK.md` · Branch: `build/tevrin-0.7.0` · Started: 2026-11-17

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

**Invariant for every milestone after M54:** the characterisation snapshots (FR-260/FR-261) are unchanged.

## Environment
- Packs (STACK.md): `fe-nextjs`, `be-aspnet`, `data-efcore-postgres`, `vcs-github`, `deploy-railway`, `local-docker-desktop-k8s`. All installed.
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben).
- VCS CLI auth: gh yes (BenFielder1). Railway deploys run only through `.github/workflows/deploy.yml`.

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-260 | SDLC characterisation snapshots (Fake providers) | 3.1, 4.58 | backend | M54 | done | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) |
| FR-261 | Snapshot freeze: diff on change, CI check from M54 | 3.1 | backend + CI | M54 | done | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) |
| FR-262 | `Tevrin.Engine.*`, `Tevrin.Pack.Sdlc`, `Tevrin.Engine.Tests` projects | 3.2 | backend | M55 | done | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) |
| FR-263 | Architecture tests (engine never references Aide/Pack) | 3.2 | backend | M55 | done | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) |
| FR-264 | Job queue moved to the engine | 3.2 | backend | M55 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) (SDLC-specific rules stay in the SDLC side by design: D-0.7.0-21, -25, -31) |
| FR-265 | LLM layer + Fake provider moved | 3.2 | backend | M55 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) (SDLC-specific rules stay in the SDLC side by design: D-0.7.0-21, -25, -31) |
| FR-266 | Generic artifact rules moved | 3.2 | backend | M55 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) (SDLC-specific rules stay in the SDLC side by design: D-0.7.0-21, -25, -31) |
| FR-267 | Gate mechanics, notifications, audit, redaction, prompt template moved | 3.2 | backend | M55 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) (SDLC-specific rules stay in the SDLC side by design: D-0.7.0-21, -25, -31) |
| FR-268 | AgentRun and rules moved to run log | 3.2 | backend | M55 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) (SDLC-specific rules stay in the SDLC side by design: D-0.7.0-21, -25, -31) |
| FR-269 | Agent string keys (`SdlcAgents`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-270 | Artifact string keys (`SdlcArtifacts`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-271 | Gate string keys (`SdlcGates`, engine `escalation`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-272 | Engine job types + pack-registered types | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-273 | Generic trigger strings, `phase_entered` alias | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-274 | Same stored values, string columns, contract strings | 3.3 | backend + data + frontend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-275 | Definition model + YAML loader | 3.4, 4.49 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-276 | Definition validator (all errors, codes, paths) | 3.4, 4.49 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-277 | Definition hash, `pipeline_definitions`, pinning | 3.4, 4.50 | backend + data | M57/M58 | done | [CI 44303824125](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44303824125) |
| FR-278 | `IPipelinePack`, `IPackRegistry`, extension points | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-279 | Artifacts in the definition (format, schema, approval, cascade) | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-280 | Stage kinds (agent, code, manual) | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-281 | Transitions (next, on_success, on_reject, loop) | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-282 | Guards (`requires`, `validate`, `on_invalid`) | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-283 | Gates on stages by approval mode | 3.4, 4.53 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-284 | Triggers modelled; only manual runs | 3.4 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-285 | Stage statuses, custom statuses, status changes | 3.4, 4.57 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-286 | Concurrency block | 3.4, 4.59 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-290 | `sdlc.pipeline.yaml`, SDLC guards, readiness check | 3.4, 4.51, 4.52 | backend | M57 | done | [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638) |
| FR-287 | `PipelineStateMachine.Apply` | 3.5, 4.51 | backend | M58 | done | [CI 44303824125](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44303824125) |
| FR-288 | `RequestAdvance` parity (gate needed, GATE_PENDING, unmet reasons) | 3.5, 4.52 | backend | M58 | done | [CI 44303824125](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44303824125) |
| FR-289 | Definition-driven cascade + SDLC invalidation hook | 3.5, 4.54 | backend | M58 | done | [CI 44318219243](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44318219243) |
| FR-291 | Parity mode + combinatorial parity test | 3.5, 4.56 | backend | M58 | done | [CI 44318219243](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44318219243) (6,271,314 combinations; integration suite with Throw=true) |
| FR-292 | Switch-over: run is source of truth, project columns copied | 3.5 | backend + data | M58 | done | [CI 44364665535](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44364665535) |
| FR-293 | Old state machine removed, transition tests rewritten | 3.5 | backend | M58 | done | [CI 44364665535](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44364665535) |
| FR-294 | `pipeline_runs` + backfill | 3.5, 5 | backend + data | M58 | done | [CI 44303824125](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44303824125) |
| FR-295 | `AgentStageRunner`, context providers, tools | 3.6 | backend | M59 | todo | |
| FR-296 | Validate / retry with feedback / escalate | 3.6, 4.55 | backend | M59 | todo | |
| FR-297 | Run logging: input, latency, stage executions, totals | 3.6, 5 | backend + data | M59 | done | [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853) |
| FR-298 | Prompt comparison stats | 3.6, 6 | backend | M59 | partial | query [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853); endpoint in M60 |
| FR-299 | Definition endpoints | 3.7, 6 | backend | M60 | todo | |
| FR-300 | Run, stages, timeline endpoints | 3.7, 6 | backend | M60 | todo | |
| FR-301 | Run artifacts, diff, gates, gate decisions | 3.7, 6 | backend | M60 | todo | |
| FR-302 | Gate inbox endpoint + hub events | 3.7, 6 | backend | M60 | todo | |
| FR-303 | Replay endpoint | 3.7, 6 | backend | M60 | todo | |
| FR-304 | `src/engine/` + lint boundary | 3.8 | frontend | M61 | todo | |
| FR-305 | Definition-driven components | 3.8 | frontend | M61 | todo | |
| FR-306 | Pipeline tab + replay | 3.8, 7 | frontend | M61 | todo | |
| FR-307 | Gate inbox page | 3.8, 7 | frontend | M61 | todo | |
| FR-308 | SDLC stepper reads the definition | 3.8 | frontend | M61 | todo | |
| FR-309 | Version 0.7.0, changelog, CLAUDE.md, STACK.md, docs guide, history | 3.9 | frontend + docs | M54, M62 | partial | bump: [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) |

## Phases
- [x] M54 Version and characterisation (FR-260, FR-261, FR-309 bump): snapshots 8dce91a, re-sorted d4d84f8 (D-0.7.0-22); freeze step in CI
- [x] M55 Engine projects and the lift (FR-262–FR-268): a483584, 2312ec8, CI green [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760). Types keyed by SDLC enums finish in M56/M58 (D-0.7.0-21)
- [x] M56 String keys (FR-269–FR-274) and the remaining lift: 39b7f24, efb38aa, ade145d, [CI 44170479903](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44170479903). FR-264–FR-268 finish in M58 for the types that still need `Phase`
- [x] M57 Definitions and packs (FR-275–FR-286, FR-290): 13f282d, 15d4ff7, [CI 44277530638](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44277530638)
- [x] M58 Generic state machine (FR-287–FR-289, FR-291–FR-294): M58a runs, dual write and backfill; M58b engine machine; M58c parity (6,271,314 combinations); M58d switch-over and removal (9213cafb, a725469, 2012cf2, ac894a2, 7f479da, [CI 44364665535](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44364665535)). Build note: CA1859 only fires in Release builds, so briefs ask for `dotnet build -c Release -warnaserror`
- [ ] M59 Stage execution and run logging (FR-295–FR-298): M59a run logging done (cd28064, [CI 44407552853](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44407552853)); M59b stage runner next
- [ ] M60 Generic API (FR-299–FR-303)
- [ ] M61 Engine UI (FR-304–FR-308)
- [ ] M62 Release 0.7.0 (FR-309), after Ben confirms on preview

## Decisions
See SPEC §10 (D-0.7.0-1 … D-0.7.0-19). Build-time decisions are added below.

- **M54 build note (orchestrator):** the snapshots were produced by CI run [44108300811](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44108300811) on 645bc4f (every other job green; the characterisation shard failed only because no snapshots were committed yet) and committed in 8dce91a. That commit also swept in 21 renames M55 had staged with `git mv`, so its CI run ([44111802972](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44111802972)) fails to build. No amend or force-push (CLAUDE.md): the M55 commit repairs it, and its CI run is the first to compare against the snapshots. From now on the orchestrator commits with explicit pathspecs while an agent is working.

- **D-0.7.0-20** (Build, M55, Claude, assumed): seams for moving the job worker, enqueuer, sweeper and channel senders into `Tevrin.Engine.Runtime` (M55 part 2):
  - **DbContext:** the engine defines `IEngineDbContext`, which exposes the engine's `DbSet`s (jobs, events, notifications, deliveries, approval links) and `Database`. `AideDbContext` implements it, and DI registers it as the same scoped instance.
  - **Job failure classification:** `IJobExceptionClassifier`, which returns a deferral or retry hint for an exception. The Aide side registers one for the GitHub, GitLab and Bitbucket rate-limit exceptions.
  - **Telemetry and log scopes:** the engine takes an `ActivitySource` and a scope factory through DI. The Aide registrations pass the existing source name and scope keys, so traces, tags and log messages stay the same. The one visible difference is the worker's log `Category`, which becomes `Tevrin.Engine.Runtime.Jobs.JobWorker` (no filter or test uses it).
  - **Secrets:** `ISecretProtector` moves to the engine as an interface. Its Data Protection implementation stays in `Aide.Infrastructure/Security/SecretProtector.cs`.
  - **Rule:** nothing that holds SDLC values moves (`NotificationType`, `EventTypes`, notification messages); those wait for M56.
- **D-0.7.0-21** (Build, M55, Claude, assumed): the LLM layer, `PromptTemplate`, `AgentRunRules`, `Artifact`/`ArtifactRules`/`RestoreRules`, `ApprovalGate`/`GateRules`/`GateFollowUp`/`GateResolution` and the `AgentRun` entity are keyed by SDLC enums. They move in M56 (string keys) or M58 (`Phase`), not M55. FR-264–FR-268 are completed across M55–M56.

- **D-0.7.0-22** (Build, M55, Claude, assumed): the characterisation recorder now treats arrays made only of id labels as sets, sorted by kind and then number. CI run [44125867549](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44125867549) on the namespace-only commit a483584 returned `resetStepIds` in a different order (an unordered query), with no behaviour change. This is a one-time normalisation change: those arrays in the committed snapshots are re-sorted, nothing else in them changes, and the freeze base in `scripts/ci/check-snapshots-frozen.sh` moves to that commit. This is the only allowed snapshot edit in 0.7.0.

- **D-0.7.0-23** (Build, M56, Claude, assumed): `Tevrin.Pack.Sdlc` is the bottom of the SDLC side. It references only the engine, and `Aide.*` references it. The SDLC key constants live there: `SdlcAgents`, `SdlcArtifacts`, `SdlcGates`, `SdlcJobTypes`. The engine owns `EngineGates.Escalation` and `EngineJobTypes`. An architecture test checks that the pack references no `Aide.*`. Where the SDLC guards and handlers in M57 need `Aide.Domain` rules, the host composes them.
- **D-0.7.0-24** (Build, M56, Claude, assumed; amends D-0.7.0-7): the OpenAPI contract stays byte-identical. DTO fields become strings, but a schema transformer still emits the SDLC enum values, so the generated client and the frontend's types don't change. Config keyed by agent keeps binding as it does today.

- **M56 build notes (orchestrator):** M56 was split into M56a (agent, artifact and gate keys), M56b (job types and triggers) and M56c (the remaining lift) after an agent stalled. CI run [44152885599](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44152885599) on 39b7f24 failed on two things. Test seeds that printed enum names now use `KeyNames.ToPascalCase`; an audit found that no production text had changed. The snapshot normaliser missed an all-digit SHA prefix, and now labels it. Review also caught the `REVIEW_STARTED`/`RELEASE_STARTED` event payloads switching to `stage_entered`; they keep `phase_entered`. Logs keep the 0.6.1 PascalCase values (`LoggedKey`). Unknown stored key values now load instead of throwing, which only matters for corrupt data.

- **D-0.7.0-25** (Build, M56c, Claude, assumed): more seams and a dependency for the engine lift.
  - **New seams:** `ILlmCatalog`, the engine's view of the SDLC provider catalogue, registered as the same singleton. `IApprovalLinkPolicy`, which says which gates get one-click links; `SdlcApprovalLinkPolicy` implements it.
  - **`NotificationOptions` split:** the engine's `NotificationChannelOptions` binds the same section, so channel settings are configured as before. The SDLC sweep settings stay on the Aide side.
  - **Dependency:** `Microsoft.Extensions.Http` is added to `Tevrin.Engine.Runtime`, giving it `IHttpClientFactory` without ASP.NET Core.
  - **Log categories:** they now follow the moved types (`ApprovalLinkService`, `EmailChannelSender`, `AnthropicModelInfo`), as with the worker in D-0.7.0-20.
  - **Architecture test:** a new test fails if an engine source file contains an SDLC key literal.
- **M58 scope (from M56c):** blocked by `Phase`: the `AgentRun` entity, `RestoreRules`, `GateRules` (`RequiredGate`, `GuardedEntry`, approve/reject/cancel) and `SdlcArtifactRules` (producing phase, `CanCreateDraft`). Staying on the SDLC side because they hold SDLC values or project rules: `AgentModelRules`, `PromptLibrary`, the fake responders, `LlmOptionsValidator`, `LlmProviderCatalog`, `LlmResolver`, `NotificationDispatcher`, `NotificationComposer`, `NotificationJobHandler`, `GateTimerService` and `NotificationSweeper`. The sweeper's single loop mixes delivery retries with the SDLC sweeps; splitting it would change timing.

- **D-0.7.0-26** (Build, M57a, Claude, assumed): definition packages and model details.
  - **Packages:** YamlDotNet 18.1.0 (MIT) and JsonSchema.Net **8.0.5** go in `Tevrin.Engine.Runtime` only. 8.0.5 is the last MIT release; 9.x moved to the commercial OSMF EULA. JsonSchema.Net's compile assets are private (`PrivateAssets`) because its root `Json` namespace clashed with Aide's helpers. Upgrading past 8.x needs a decision (open question Q4).
  - **Model additions to §4.49:** `rollback_status`; `loop.requires`, which carries `sdlc.review-cycle-ready`; and `on_reject: { to, max_iterations }`. `agent.prompt` is optional and defaults to the pack's prompt for the agent key.
  - **Artifact defaults** (amends §4.49, which marks only `schema` as optional): `format` defaults to `markdown`, `approval` to `required` and `cascade` to `downstream`. Defaults are written into the canonical JSON, so writing one out explicitly doesn't change the hash.
  - **Deferred to M59:** checking prompt names against the prompts the pack registers (a readiness check).
  - **Validator:** values that fit no specific code are `DEF_PARSE`. M57a review fixes: a backward `on_success` is `DEF_UNCAPPED_LOOP`; schema `$ref`s must resolve; `escalation` can't be an entry gate; canonical JSON escapes only the quote, backslash and C0 controls (so the hash doesn't depend on the .NET version); M58 must read stored canonical JSON back with a JSON reader, not the YAML loader, because YAML treats U+2028 as a line break. Previously: `"`, `\` and C0 controls. The loader reads YAML through the event parser only, with no tags, alias expansion capped at 1 MB, a nesting depth of 64 and one document.

- **D-0.7.0-27** (Build, M57b, Claude, assumed): how the SDLC definition and guards are put together.
  - **Facts:** guards read an `SdlcLifecycleFacts(LifecycleState, LifecycleSnapshot)` fact, which M58 loads with `LifecycleSnapshotLoader`.
  - **When `requires:` runs:** only on `advance` and `loop`. Never at run start, rollback, fallback or a new version, because `sdlc.start-only` always fails.
  - **Loops (M57b review):**
    - A `loop` checks only `loop.requires` (`sdlc.review-cycle-ready`). It never checks the target stage's `requires:` or its entry gate, so a strict-mode review cycle needs no `plan_approval`, as in 0.6.1.
    - The caller supplies the loop's outcome. For the SDLC, the `review` loop also fires on escalation `continue` (`GateFollowUp.StartReviewCycle`) while the decision is `escalation_required`, so `loop.on` isn't matched against the review decision alone.
    - The loop counter for the `review_cycles` cap is `projects.review_cycle`, which rollback restores and a new version resets to 0. The run's iteration count must follow the same rules, not count up forever.
  - **Inputs:** stage `inputs` follow what the 0.6.1 handlers actually read. Refinement lists `stack_profile`, so the cascade reaches `project_request`.
  - **Jobs on entering a stage:** the model has no "job on entry" setting, and 0.6.1 enqueues `release` on entering the manual release stage while enqueuing nothing for refinement or analysis. M58 adds an optional stage field `enqueue_on_enter: <job_type>`; the SDLC definition sets it to match `PhaseJobs`, and the engine never assumes "agent stage means enqueue".
  - **Spec fix:** in §4.54, approving an implementation plan falls back to `execution`, not `review`, as in 0.6.1 (corrected). The SDLC `IInvalidationHook` supplies the step reset.
  - **Missing inputs:** M59's context builder must allow one (`stack_profile` doesn't exist for versions that don't infer their stack).
  - **Testing:** `SdlcRunSettingsProvider` needs an integration test.

- **D-0.7.0-28** (Build, M58a, Claude, assumed): pipeline runs, the backfill and the dual write (FR-277, FR-292, FR-294).
  - **No SDLC definition (M58a review, accepted):** if the registry has no valid SDLC definition, every save that touches a project or adds a transition, gate, agent run or artifact throws. That includes saves from the job worker. Readiness (`/api/health/ready`) fails in the same case, so Railway and Kubernetes keep traffic off the pod. This fails closed on purpose.
  - **Contract step:** making the `pipeline_run_id` columns NOT NULL in a later version must first re-run the link pass, because rows written by 0.6.1 replicas during an overlapping deploy are linked only on the next start.
  - **Stored status:** `pipeline_runs.status` holds only `active`, `completed`, `failed` or `cancelled`; `waiting_on_gate` and `running` are derived when a run is read (§4.57).
  - **Links:** `pipeline_run_id` on `state_transitions`, `approval_gates`, `agent_runs` and `artifacts`, and `state_transitions.kind`, stay nullable in 0.7.0 (tightening is a later contract step). `kind` maps `Advance`→`advance`, `ReviewCycle`→`loop`, `Rollback`→`rollback`, `InvalidationFallback`→`fallback`, `ReleaseNoop`→`noop`, the three release status moves→`status_change`, and `NewVersion` and project creation→`start` (the first transition of a run). Rows written before 0.7.0 keep `kind` null.
  - **Backfill in the host, not the migration:** the hash can't be computed in SQL. `PipelineRunBackfillService` runs at start-up before the job worker (skipped while migrations are pending). It stores the SDLC definition and creates one run per release version that lacks one: the current version from the project's phase, status, checkpoint, approval mode and review cycle, and earlier versions `completed` at `release`/`released` (at their last transition checkpoint). It then links rows by release version. `imported` baselines get no run. Inserts use `ON CONFLICT DO NOTHING` and links fill only nulls, so it is idempotent and safe on several replicas. The lazy path creates missing runs but doesn't link old rows, because a raw `UPDATE` would bump `xmin` on gates and artifacts the unit of work may hold.
  - **Dual write as a `SaveChanges` interceptor** (`PipelineRunSyncInterceptor`, after release-version stamping): every added or changed project copies its state onto its current version's run, in the same transaction as the project update, and new linked rows get their run. This covers every writer (lifecycle, rollback, release, review cycle, approval mode, seed) with no per-writer calls. The run is always loaded fresh for the save, and links read run ids without tracking, so the dual write never adds a concurrency failure of its own. Storing the definition, closing superseded runs and the lazy backfill run as their own idempotent statements before EF's transaction. A new version's run is pinned to the registry hash computed at runtime. Synchronous `SaveChanges` touching projects or linked rows throws `NotSupportedException` (no caller uses one).
  - **Catching up after a 0.6.1 build ran against the data** (deploy rollback, then redeploy): the start-up pass closes open runs of superseded versions with their released final state, creates missing runs, re-syncs current runs from their project, and links every unlinked row of every version. The lazy path also closes superseded runs before creating a new one.
  - **Event order** (CI run 44294566932): per-row events written in one save share a timestamp and took their order from unordered queries, so it depended on physical row order, which the new column writes changed. The order is now explicit (`EventOrder`) and reproduces the snapshots: `ARTIFACT_INVALIDATED` (cascade and rollback) newest artifact first, then highest version; `GATE_CANCELLED` (artifact approval, rollback, review start) oldest gate first; `ARTIFACT_SUPERSEDED` lowest version first.

- **D-0.7.0-29** (Build, M58b, Claude, assumed): the engine state machine's messages and the definition fields it needs.
  - **Messages:** `TransitionMessages` holds 0.6.1's exact texts (they still say "phase"), with stage ids and titles filled in.
  - **New optional definition fields:** `loop.title` (default "Loop"), and `status_changes.*.reason` and `error_message`. Their defaults are written into the canonical JSON.
  - **SDLC definition:** sets `title: Review cycle`, the release status-change texts, and `enqueue_on_enter` on specification, planning, review and release (the same jobs as `PhaseJobs`). These change the SDLC hash before any run is pinned to it.
  - **Engine error codes:** `PHASE_PRECONDITION_FAILED`, `GATE_PENDING`, `WRONG_PHASE` and `INVALID_TARGET_VERSION` are added, with the same values as the host's codes.
  - **Cascade hook:** `IInvalidationHook` gains `FallbackStages`; the real SDLC step-reset hook comes in M58d.
  - **Not machine moves:** entering `analysis` and starting a new version go through `start`, the start selector and the SDLC service.
  - **For the parity test:** keep the `review` iteration count below `review_cycles`, because the engine's loop cap is a backstop 0.6.1 doesn't have. Pass `review_required` as the review-cycle outcome.
- **D-0.7.0-30** (Build, M58c, Claude, assumed): the review loop's cap never refuses what 0.6.1 allows (FR-291, FR-292).
  - **Engine:** a loop's `max_iterations` cap refuses only a loop that would otherwise be allowed. A loop already refused (outcome, `loop.requires`) gets no extra "limit reached" reason, so a refused SDLC review cycle reports exactly 0.6.1's reasons (e.g. approving the cycle-3 optimisation plan before the max-cycles escalation is decided).
  - **SDLC:** `review_cycles` (`SdlcRunSettingsProvider`, and the parity mapper through `SdlcReviewLoop`) is `max(maxReviewCycles + extraReviewCycles, review_cycle + 1)`, so the backstop never refuses a cycle 0.6.1 allows (e.g. after an Owner lowers `maxReviewCycles` below the current cycle). The review decision rules (escalation at the max) stay the real limit.


- **D-0.7.0-31** (Build, M58d, Claude, assumed): the switch-over.
  - **Who decides:** lifecycle decisions go only through the engine (`SdlcLifecycle`, using the pure `SdlcMachine`), and fail closed with no valid SDLC definition. `LifecycleStateMachine`, `PhaseOrder`, `TransitionKind`, the phase-keyed `GateRules` lookups, `InvalidationCascade.Effect`, `PhaseJobs` and the parity code are removed.
  - **Reading state:** decisions read stage, status and checkpoint from the current `pipeline_runs` row. Writers still write the project columns, and the interceptor copies them onto the run in the same save. When the run and the project disagree, the project copy wins and the disagreement is logged at Information level. That happens when a concurrent request has committed, or a write bypassed EF (an overlapping 0.6.1 replica, or raw SQL in test seeds). There is no eager re-sync, which the M58d review found could write stale state; the interceptor re-syncs the run on the next save that changes the project.
  - **What stays:** the `Phase` enum stays as the SDLC's typed stage id (DTOs and contract), and a test pins its order to the definition. Step resets stay in `InvalidationCascade.Plan`, because the hook context doesn't carry the steps.
  - **Status changes:** a status change naming another stage is refused with the definition's `wrong_stage_message`, which reproduces 0.6.1's `WRONG_PHASE` text.
  - **Tests:** the transition tests were rewritten as `SdlcLifecycleTransitionTests`: 40 methods and 648 cases, with the same expected results.

- **D-0.7.0-32** (Build, M59a, Claude, assumed; amends SPEC §5): run logging data (FR-268, FR-297, FR-298).
  - **No `stage_id` column on `agent_runs`:** `AgentRun` moved to the engine with `string Stage` mapped to the existing `phase` column, which already holds the stage id (same values). DTOs keep their `Phase` type and wire values.
  - **Migration `RunLogging`** (expand only): `agent_runs` + `input` text, `input_truncated` boolean NOT NULL DEFAULT false, `latency_ms` bigint; new `stage_executions` (FK to `pipeline_runs` ON DELETE CASCADE, index (`pipeline_run_id`, `entered_at`)).
  - **Input:** `[role]` blocks for the system prompt and user message, redacted with `SecretRedaction` (the bound key too), capped at 1 MB of UTF-8 on a character boundary. Text past 2 M characters is dropped before redaction (flagged truncated). A redaction timeout leaves the input null and never fails the run. `latency_ms` is the time to the first streamed output (text, refusal or tool call) of the last attempt that streamed, via `LlmFirstTokenProbe`; null for the Fake provider and whole-JSON answers.
  - **Stage executions:** written by `PipelineRunSyncInterceptor` through the engine `StageExecutionWriter`, in the save of each new transition: a transition that changes stage, or a `start`/`loop`/`rollback`/`fallback` (re-entering the same stage), closes the open row with its kind and opens a new one (iteration = entries into that stage in the run + 1, checkpoint = the transition's). The iteration counts entries, so it differs from `projects.review_cycle` after a rollback (rollback restores the review cycle; a re-entered review gets the next iteration); FR-300's iteration count shows entries. A run that ends closes its row with its final status (`completed`). Superseded runs closed by SQL have their rows closed by `CloseEndedAsync` (lazy path and start-up backfill).
  - **No history backfill:** runs started before M59 have no rows for earlier stages, and the stage they are in when this deploys gets no row (its entry time is unknown); their next transition opens the first row.
  - **Queries:** `RunLogQueries` (engine runtime, scoped): `RunTotalsAsync`, `PromptStatsAsync(pipelineId, projectIds)` (fail closed: the project set is required, an empty set returns nothing) and `PromptStatsForAllProjectsAsync` (administrators only), aggregated in SQL; averages from `RunLogStats` (cost rounded to 6 places, latency averaged over runs that recorded one). Failure rate = failed / finished, where finished = succeeded + failed (queued, running and cancelled runs are left out; 0 when none finished). The endpoint is M60.
  - **Reads skip the input:** list, detail, refinement, stack-analysis and job-ended reads project the columns they need (`AgentRunProjections.WithoutInput`); the runner and the job-ended hook attach the projected row, so only changed columns are written and the stored input stays.

- **Watch (flaky test):** `DeliveryTimeoutTests.FR49_An_SMTP_server_that_never_answers_times_out_as_a_transient_failure` failed twice in local unit runs on this memory-starved machine and passes on its own. It is green on CI so far; if it fails there, give it a `FakeTimeProvider` or wider margins (the M58c review also flagged wall-clock tests in `LlmStreamingTests`, `LlmKeyCheckTests` and `CheckFailureLogTests`).

## Manual checks (Ben)
- A full SDLC project on preview behaves as on 0.6.1 (strict and advisory).
- An existing 0.6.1 project mid-review continues after the 0.7.0 deploy.
- Pipeline tab and replay on a real run; gate inbox.
- Docker Desktop redeploy and smoke test.
  - **M58c review notes for M58d:**
    - With `review_cycles` at least `review_cycle + 1`, the engine's loop cap never binds for the SDLC, so the backstop is effectively off there. The SDLC review decision rules remain the only limit, as in 0.6.1.
    - `projects.review_cycle` stays the authoritative review counter. The review job counts it up and rollback restores it, as in 0.6.1, and it is mirrored into `pipeline_runs.iterations`. M58d must not derive it from the engine's `RunTransition.Iterations`, which counts entries into `review` and isn't compared by parity.
    - **Accepted exception to FR-291's "every lifecycle move":** the dev seed (`DevSeeder.cs`) writes phase and status directly. It is dev-only and predates 0.7.0.
