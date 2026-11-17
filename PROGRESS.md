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
| FR-264 | Job queue moved to the engine | 3.2 | backend | M55 | partial | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) (rest in M56/M58, D-0.7.0-21) |
| FR-265 | LLM layer + Fake provider moved | 3.2 | backend | M55 | partial | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) (rest in M56/M58, D-0.7.0-21) |
| FR-266 | Generic artifact rules moved | 3.2 | backend | M55 | partial | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) (rest in M56/M58, D-0.7.0-21) |
| FR-267 | Gate mechanics, notifications, audit, redaction, prompt template moved | 3.2 | backend | M55 | partial | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) (rest in M56/M58, D-0.7.0-21) |
| FR-268 | AgentRun and rules moved to run log | 3.2 | backend | M55 | partial | [CI 44132705760](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44132705760) (rest in M56/M58, D-0.7.0-21) |
| FR-269 | Agent string keys (`SdlcAgents`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-270 | Artifact string keys (`SdlcArtifacts`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-271 | Gate string keys (`SdlcGates`, engine `escalation`) | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-272 | Engine job types + pack-registered types | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-273 | Generic trigger strings, `phase_entered` alias | 3.3 | backend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-274 | Same stored values, string columns, contract strings | 3.3 | backend + data + frontend | M56 | done | [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308) |
| FR-275 | Definition model + YAML loader | 3.4, 4.49 | backend | M57 | todo | |
| FR-276 | Definition validator (all errors, codes, paths) | 3.4, 4.49 | backend | M57 | todo | |
| FR-277 | Definition hash, `pipeline_definitions`, pinning | 3.4, 4.50 | backend + data | M57/M58 | todo | |
| FR-278 | `IPipelinePack`, `IPackRegistry`, extension points | 3.4 | backend | M57 | todo | |
| FR-279 | Artifacts in the definition (format, schema, approval, cascade) | 3.4 | backend | M57 | todo | |
| FR-280 | Stage kinds (agent, code, manual) | 3.4 | backend | M57 | todo | |
| FR-281 | Transitions (next, on_success, on_reject, loop) | 3.4 | backend | M57 | todo | |
| FR-282 | Guards (`requires`, `validate`, `on_invalid`) | 3.4 | backend | M57 | todo | |
| FR-283 | Gates on stages by approval mode | 3.4, 4.53 | backend | M57 | todo | |
| FR-284 | Triggers modelled; only manual runs | 3.4 | backend | M57 | todo | |
| FR-285 | Stage statuses, custom statuses, status changes | 3.4, 4.57 | backend | M57 | todo | |
| FR-286 | Concurrency block | 3.4, 4.59 | backend | M57 | todo | |
| FR-290 | `sdlc.pipeline.yaml`, SDLC guards, readiness check | 3.4, 4.51, 4.52 | backend | M57 | todo | |
| FR-287 | `PipelineStateMachine.Apply` | 3.5, 4.51 | backend | M58 | todo | |
| FR-288 | `RequestAdvance` parity (gate needed, GATE_PENDING, unmet reasons) | 3.5, 4.52 | backend | M58 | todo | |
| FR-289 | Definition-driven cascade + SDLC invalidation hook | 3.5, 4.54 | backend | M58 | todo | |
| FR-291 | Parity mode + combinatorial parity test | 3.5, 4.56 | backend | M58 | todo | |
| FR-292 | Switch-over: run is source of truth, project columns copied | 3.5 | backend + data | M58 | todo | |
| FR-293 | Old state machine removed, transition tests rewritten | 3.5 | backend | M58 | todo | |
| FR-294 | `pipeline_runs` + backfill | 3.5, 5 | backend + data | M58 | todo | |
| FR-295 | `AgentStageRunner`, context providers, tools | 3.6 | backend | M59 | todo | |
| FR-296 | Validate / retry with feedback / escalate | 3.6, 4.55 | backend | M59 | todo | |
| FR-297 | Run logging: input, latency, stage executions, totals | 3.6, 5 | backend + data | M59 | todo | |
| FR-298 | Prompt comparison stats | 3.6, 6 | backend | M59 | todo | |
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
- [ ] M56 String keys (FR-269–FR-274): keys and job types/triggers done (39b7f24, efb38aa, [CI 44160448308](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/44160448308)); M56c (remaining lift) in progress
- [ ] M57 Definitions and packs (FR-275–FR-286, FR-290)
- [ ] M58 Generic state machine (FR-287–FR-289, FR-291–FR-294)
- [ ] M59 Stage execution and run logging (FR-295–FR-298)
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

## Manual checks (Ben)
- A full SDLC project on preview behaves as on 0.6.1 (strict and advisory).
- An existing 0.6.1 project mid-review continues after the 0.7.0 deploy.
- Pipeline tab and replay on a real run; gate inbox.
- Docker Desktop redeploy and smoke test.
