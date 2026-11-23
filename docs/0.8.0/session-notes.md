# 0.8.0 build notes: context from the 0.7.0 session

Reference for starting the 0.8.0 build in a new session. The spec is [`SPEC.md`](SPEC.md) in this folder. Read it first, then this file. These notes record what isn't in the spec or the code: where things stand, how 0.7.0 was built, what went wrong, and what to watch for in 0.8.0.

Written 2026-11-23, at the end of the session that built 0.7.0's second and third build PRs and wrote the 0.8.0 spec.

---

## 1. Where things stand

### 0.7.0: merged, not yet released
- **Build PRs merged into `main`:**
  - **#50:** the engine extraction (M54–M61).
  - **#59:** the whole SDLC pack on the engine runner (M62a–f, FR-310–FR-321).
  - **#60:** the `tevrin` CLI retired (M63, FR-322), with the rebuild guide at [`docs/reference/retired-cli.md`](../reference/retired-cli.md) (snapshot commit `e8c3990`).
- **Still to do for 0.7.0, which is M64:**
  1. Ben tests on preview. The manual checks are in the root `PROGRESS.md`, "Manual checks (Ben)".
  2. On Ben's confirmation, branch `release/0.7.0` from `main` and do the release prep from CLAUDE.md "Version workflow" step 3:
     - set the changelog `date`;
     - move the root SPEC and PROGRESS to `docs/0.7.0/`, with the frozen-folder defaults in `docs/_config.yml`;
     - add the Versions row in CLAUDE.md;
     - write `docs/history/0.7.0.md`;
     - update the docs-site guides.
  3. Tag `0.7.0` only after Ben confirms the version and commit.
- **Answered open questions:** Q1 keeps the PolyForm Shield licence (D-0.7.0-46), Q2 keeps the 1 MB stored-input cap (D-0.7.0-47), and Q4 stays on JsonSchema.Net 8.0.5 so the dependency stays MIT (D-0.7.0-48).

### 0.8.0: spec written, not started
- `docs/0.8.0/SPEC.md` is on branch `build/tevrin-0.8.0` (commit `b6ec10d`), branched from `main` after #60. It has FR-323–FR-367, D-0.8.0-1 to -15, open questions Q1–Q4, and milestones M65–M73.
- **Before building:** once 0.7.0 is released, move `docs/0.8.0/SPEC.md` to the root `SPEC.md`, and create the root `PROGRESS.md`. The orchestrator does that from the template.
- **Version bump at M65 start:** `backend/Directory.Build.props` `<Version>`, `frontend/package.json` plus its lock, and a new newest entry in `frontend/src/features/marketing/content/changelog.ts` with `date: null`. `scripts/ci/check-version.sh` checks all three.
- **Build command:** `/stack-core:orchestrate-stack build Tevrin v0.8.0`.

---

## 2. The architecture as built

```
Aide.Api ──> Aide.Infrastructure ──> Aide.Domain ──> Tevrin.Pack.Sdlc ──> Tevrin.Engine.Domain
   │               └──> Tevrin.Engine.Runtime ──────────────────────────────────┘
   ├──> Tevrin.Engine.Api ──> Tevrin.Engine.Runtime
   └──> Tevrin.Pack.Sdlc
```

| Project | Holds |
| --- | --- |
| `Tevrin.Engine.Domain` (pure) | Definition model, validator, canonical-JSON hash, `PipelineStateMachine`, cascade, pack contracts (`IPipelinePack`, `IGuard`, `IStageHandler`, `IAgentTool`, `IAgentContextProvider`, `IInvalidationHook`, `IRunSettingsProvider`, `IRunStartSelector`), `StageHandlers`, `AgentBindings`, entities |
| `Tevrin.Engine.Runtime` | YAML loader, registry, `PipelineDefinitionResolver`, job queue and worker, LLM layer and Fake provider, `Agents/` (`AgentStageRunner` + partials, `AgentStageCoordinator`, `AgentStageExtension`, `AgentToolExecutor`, `AgentToolSession`, `ArtifactSchemas`), `Stages/StageHandlerRunner`, gates, notifications, redaction |
| `Tevrin.Engine.Api` | 15 generic routes (definitions, runs, stages, timeline, artifacts and diffs, gates, gate inbox, replay, prompt stats) and hub events. Host seams: `IRunAccessPolicy`, `IGateDecisionService`, `IPipelineTransitionSource`, `IRunActivityPolicy`, `IEngineProblems` |
| `Tevrin.Pack.Sdlc` | `Definitions/sdlc.pipeline.yaml` (version 3), key constants, guard, tool, context and handler names, `SdlcPack` |
| `Aide.Api/Agents/Sdlc*.cs` | The SDLC's bridge to the engine (see below) |

**The SDLC on the engine (0.7.0 §3.10).** All eight SDLC agents run through `AgentStageRunner`:
- `SdlcAgentStageJob` plus `SdlcStageLocator` find the run and its pinned definition. The locator creates a missing run on demand, links unlinked 0.6.1 agent runs, and fails permanently with `INVALID_STATE` or `DEFINITION_UNAVAILABLE` otherwise.
- `SdlcAgentExtension` (partials `.Call`, `.Commit`, `.Finish`) reproduces 0.6.1 exactly. It uses:
  - the separate commit policy;
  - checks in 0.6.1's order (before render, then before the call);
  - run events (`SdlcRunEvents`) and the token commit;
  - AideJson output and the store override (`AgentDraftWriter`);
  - corrective re-runs keyed `corrective`, or `build-fix-<check>-n` and `step-<id>-attempt-<n>`;
  - the stack prompt for specialists (`TransformPromptAsync`).
- **Stageless agents** are `agents:` bindings (D-0.7.0-42): `repository_analyst` on `refinement`; `code_generator` and `build_fix` on `execution`.
- **Execution loop and build check** are `IStageHandler`s (`SdlcExecutionStageHandler` `sdlc.execution`, `SdlcBuildCheckStageHandler` `sdlc.build-check`). Execution declares them as `handlers: [sdlc.execution, sdlc.build-check]`, so it stays a `manual` stage (D-0.7.0-43). `SdlcStageHandlerJob` dispatches through `StageHandlerRunner`, and pre-v3 runs call the handler directly.
- **Output checks** are `validate:` guards (D-0.7.0-44). `SdlcPlanValidGuard` and `SdlcReplyCheckGuard` apply the attempt's own check, so code review's split and build fix's "second reply accepted" both hold. On runs pinned to v1 or v2, the host checks still run instead.
- **Old runner gone:** `AgentRunner` and `ArtifactAgentJob` are deleted. The helpers that are still needed live in `SdlcAgentRuns` and `ArtifactAgentSpec`.

**SDLC definition hashes.** Runs pinned to v1 or v2 are re-pinned to v3 at start-up by `SdlcRunRepin` (`Aide.Infrastructure/Persistence/SdlcRunRepin.cs`), before the backfill and the job worker. The rollback caveat is in `docs/operations/pipeline-definitions.md`.

| Version | Hash | Committed fixture |
| --- | --- | --- |
| v1 | `90f1f065…` | `backend/tests/Aide.IntegrationTests/Pipelines/Fixtures/sdlc-v1.pipeline.yaml` |
| v2 | `d2f1c98b…` | `backend/tests/Aide.IntegrationTests/Pipelines/Fixtures/sdlc-v2.pipeline.yaml` |
| v3 | `a3079621…` | current |

**Architecture tests:**
- `Tevrin.Engine.Tests/Architecture/EngineDependencyTests.cs`: the engine never references `Aide.*` or a pack, and there are no SDLC key literals in engine source.
- `Aide.UnitTests/Architecture/DomainDependencyTests.cs`.
- `Aide.UnitTests/Architecture/AgentCallBoundaryTests.cs`: an IL scan that allows only `SdlcAgentExtension` to call LLM providers. The exemption is an explicit type list, not a prefix.

**Freeze checks** (`scripts/ci/check-snapshots-frozen.sh`): the characterisation snapshots are frozen to base `d4d84f8`, and the golden prompt files (`Aide.UnitTests/Agents/Golden/*.txt`) to base `b593268`.

---

## 3. Notes for the 0.8.0 milestones

### M65 rename: traps found while writing the spec
- **Size:** about 915 C# files with an `Aide` namespace, 5 projects and 55 migration files plus the model snapshot. The EF snapshot and migration designers hold CLR type-name strings such as `"Aide.Domain.Entities.Project"`.
- **`Aide*` types:** `AideApiFactory`, `AideAuthOptions` (with `SmartScheme = "Aide"`), `AideDbContext`, `AideDbContextModelSnapshot`, `AideJobPayloadSerializer`, `AideJobServiceCollectionExtensions`, `AideJson` and `AideTelemetry` (`SourceName = "Aide"`).
- **`EngineDependencyTests` uses an `Aide` prefix:** it matches names equal to `Aide` or starting with `Aide.` (around lines 71–74). After the rename, a `Tevrin.*` prefix would also match the engine itself, so list the host assemblies explicitly (FR-323). Update `AgentCallBoundaryTests` and `DomainDependencyTests` in the same way.
- **Wire and stored names found** (FR-326 and FR-329 must cover them all):
  - `aide_merges`: the merge-strategy enum value, stored and in the API contract, so a breaking change (D-0.8.0-10);
  - `aide_pat_`: the token prefix, in 52 code and test hits;
  - the `aide.auth` cookie, which also appears in `frontend/src/server/api-proxy.test.ts`;
  - `aide-plan`, the fenced block in stored plans, which feeds content hashes and the snapshots;
  - the telemetry tags `aide.run_id`, `aide.project_id`, `aide.stage`, `aide.run_status` and `aide.user_id`;
  - the env vars `AIDE_RUN_MIGRATIONS` (74 hits) and `AIDE_IMPORT_ROOT`, the config key `ConnectionStrings:Aide` (`DependencyInjection.ConnectionStringName = "Aide"`) and the database name `aide`;
  - string identifiers in the code: `aide-platform`, `aide-system`, `aide-stack-profile`, `aide-snapshots`, `aide-scope-check`, `aide-repo-init`, `aide-preview`, `aide-db`, `aide-api` and `aide_api_token`. Check each one: some are persisted or appear in Git or on external platforms.
  - the Kubernetes names in `deploy/k8s` (namespace `aide`; `aide-api`, `aide-web` and `aide-data`) and the images `aide-api` and `aide-web`.
- **Order inside M65:** project and namespace renames first, as a mechanical commit. Then the migration-check script, the config fallbacks, the wire names with read-both, the infrastructure and runbook, and the snapshot re-baseline (FR-328) last, as a single commit. The golden-prompt freeze base (`b593268`) also needs moving if any prompt contains `aide`.
- **Branch prefix:** `tevrin/` for project branches was already done in 0.6.1, so it isn't part of this rename.

### M66 onwards: engine gaps to build on
- **What run control means here:** an engine-controlled pipeline is one whose runs the engine starts and moves itself. The SDLC is host-controlled: its own job handlers queue each agent, and its project endpoints move the lifecycle. Nothing in the engine drives a run today; `stage.run` (FR-332) is new.
- **Runs are tied to a subject today.** Artifacts and gates take the release version from `pipeline_runs.subject_id` (the SDLC's release version), and there is a unique (`pipeline_id`, `subject_id`) index. FR-334 makes the subject optional.
- **Schedule and webhook triggers** are modelled but refused (`DEF_TRIGGER_NOT_SUPPORTED`, D-0.7.0-3). The job queue already supports delayed jobs (`run_after`).
- **Definition fields** (§4.49 in `Tevrin.Engine.Domain/Pipelines/PipelineDefinition.cs`):
  - `agent: { key, prompt, model, tools, job_type }`, where `model` already exists;
  - `agents:` entries take their own `validate`/`on_invalid`;
  - `handlers:` lists.
  - New fields must be left out of the canonical JSON when empty, or every existing hash changes. Pin hashes in tests, as `AgentBindingHashTests` does.
- **`PipelineDefinitionResolver.MissingNames`** decides whether a stored definition can still run. Every name a new construct can reference must be added there (tools, guards, handlers, agents and so on). The reviewer caught this twice in 0.7.0 (binding tools, then binding guards).
- **Engine defaults for a new pack:** `AgentStageExtension` defaults are the engine's own policy, so the coach needs no extension. The SDLC's host hooks (`SdlcAgentStageHooks`: budget and tokens) are scoped to the `sdlc` pipeline. D-0.8.0-7 makes the budget and audit apply to every pipeline, so generalise that registration.
- **Prompt registration:** packs register prompts (D-0.7.0-33), and the validator checks prompt placeholders only for packs that register prompts. The SDLC registers none and keeps its `EmbeddedPromptLibrary`.

---

## 4. How the 0.7.0 build was run (keep doing this)

- **Commit gate** (CLAUDE.md):
  1. Static `stack-reviewer` on every step, with the requirement IDs, base SHA and the key question for that step.
  2. Fix Critical and Major findings.
  3. Commit with explicit pathspecs.
  4. Push the build branch, then wait for CI on the head commit.
  - Never run the full `dotnet test`, Docker or E2E locally.
  - Push only build and release branches; never `main`.
  - Ben confirms tags and releases, and production happens only when he asks.
- **Review brief that worked:** ask "will the characterisation snapshots stay unchanged?" and have the reviewer trace the change against the old code path at a named commit. The reviews caught real behaviour changes every time: event order, attempt counts, the order of checks before the call, cancellation after a paid call, unscoped redelivery, and missing names in `MissingNames`.
- **One thing per commit,** with CI between risky commits, so a failure points at one change. Push docs-only commits on their own: CI skips `*.md` and `docs/**`, and the last code commit's run counts.
- **Decisions:** record each one in the spec's §10 as it happens, with the person who made it. Material choices went to Ben through `AskUserQuestion`: the agent binding style, FR-314 and FR-319 built as written, the CLI snapshot as a doc only, and the rename depth.
- **Expected-value changes:** pinned hashes, or a version 2→3 assertion, were the only allowed changes to existing test expectations. Each carries a comment naming the decision.

---

## 5. Machine and tooling lessons

- **Memory is short:** agents run one `dotnet` command at a time. Use `dotnet build <one project> -c Release -warnaserror`, because analyzers such as CA1859 fire only in Release. Run unit tests filtered by namespace, and engine tests on their own (no Docker).
- **Agents stall on big writes:** the stream watchdog fires after 600 s with no progress. Every brief says to write files in small pieces, a skeleton then small edits. When one stalls, resume it with `SendMessage`; if it stalls twice, re-brief a fresh agent with a narrower scope and the current `git status`.
- **Long-running agents build huge contexts** (500k+ tokens). Switch to a fresh agent with a pointed brief for big new pieces; it can read the earlier moves as worked examples.
- **Parallel agents in one working tree:** give each a disjoint set of files it owns. If two touch the same file, as the M62f stage-handler and guard agents did, make one agent responsible for checking the merge. When committing only part of the tree, use explicit paths, and check nothing else slipped in. One agent had updated a hash pin in an engine test file that belonged with the other agent's YAML change.
- **Prefer `dotnet build-server shutdown`** when a build seems to hang.

### CI quirks
- **Pushing cancels older runs:** a newer push to the same branch cancels the run already in progress (concurrency group). To test a risky commit on its own, wait for its run before pushing again.
- **`gh run watch` sometimes exits early.** Poll instead:
  ```bash
  until [ "$(gh run view $id --json status -q .status)" = "completed" ]; do sleep 30; done
  ```
- **Slow shard:** the `rest` integration shard takes 15–18 minutes; its step timeout was raised from 18 to 22 minutes. The slowdown was the runner, about 30% across unrelated tests. Rebalancing the shards is an open follow-up.
- **Docs link check runs only on PRs** (`Build docs site + internal link check`). A Markdown link written in prose (e.g. quoting an old `[CLI](../guides/cli.md)`) is a real link, so put quoted links in code spans.
- **The `main` ruleset has no required status checks,** so renaming CI jobs needs no protection change.
- **Seeding pinned runs in integration tests:** `pipeline_runs.definition_hash` has a foreign key to `pipeline_definitions`, so a test that pins a run to a hash must first store that definition. Use the committed fixture YAML plus a hash assert.
- **Watch these flaky tests:** `DeliveryTimeoutTests.FR49_…` and the YAML alias-bomb timing test. Both failed only locally under memory pressure.

---

## 6. Open follow-ups carried into 0.8.0

- Rebalance the integration test shards (`.github/workflows/ci.yml`; `rest` is the slowest).
- 0.8.0 spec open questions Q1–Q4 (Velocity defaults and units, the Impulse webhook payload, coach models, the map-item cap).
- After the rename, the old names are read until 0.9.0 (§4.77). Plan their removal in the 0.9.0 spec.
- A `/product#cli-and-api` anchor no longer exists (it is now `#api`) since the CLI removal, in case anything external linked to it.

---

## 7. Candidate packs, for after 0.8.0

The research notes list eight packs (`docs/0.7.0/tevrin-engine-extraction.md`, "What could be built on the engine afterwards"). 0.8.0 builds the engine features for all of them except the agent economy, and ships only the coach as a product pack. The order recommended in this session:

1. **#5 Impulse coaching:** 0.8.0.
2. **#4 Job applications:** a validator agent, source-of-truth artifacts and a human gate before an external action. Low cost.
3. **#7 Content studio:** routes, a revision cap and variants.
4. **#2 Forecasting desk:** fan-out, aggregation and Brier scoring.
5. **#1 Market research desk:** parallel stages, veto, follow-ups and the feedback store.
6. **#3 QA lab:** map-reduce, conversations and scheduled aggregation.
7. **#6 On-call triage:** tool tiers and approval in the middle of a stage. This is a security feature: review it carefully, and keep destructive tools on fixtures until a real pack needs them.
8. **#8 Agent economy:** last; a stretch test.

Each future pack should need only YAML, prompts, schemas and a few guards or handlers. If engine code has to change, that's a gap: fix it in the engine, not in the pack.
