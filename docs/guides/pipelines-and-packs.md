---
title: Pipelines and packs
parent: Guides
nav_order: 11
---

# Pipelines and packs

Since 0.7.0 Tevrin runs on a reusable **pipeline engine**. The engine owns the state machine, the job queue, approval gates, the artifact store, the LLM layer and the run log. A **pipeline definition** (a YAML file) says what the stages are. A **pack** supplies the named C# pieces the definition refers to. The software lifecycle is the first pack, `sdlc`, and users see the same product.

## Engine and pack

| Part | Project | Holds |
| --- | --- | --- |
| Engine domain | `Tevrin.Engine.Domain` | Definition model, validator, hash, state machine, cascade. Pure: no EF Core, ASP.NET Core or Npgsql |
| Engine runtime | `Tevrin.Engine.Runtime` | YAML loader, registry, job queue, LLM providers, artifact store, gates, notifications, run log, agent stage runner |
| Engine API | `Tevrin.Engine.Api` | The generic read endpoints, gate decisions and the SignalR events |
| SDLC pack | `Tevrin.Pack.Sdlc` | `sdlc.pipeline.yaml`, key constants, guard names, `SdlcPack` |
| Host | `Tevrin.Api`, `Tevrin.Domain`, `Tevrin.Infrastructure`` | Composes the pack: guard implementations, settings, start selector, project permissions |
| Engine UI | `frontend/src/engine/` | Pipeline views that render any definition |

**The dependency rule:** no `Tevrin.Engine.*` assembly references the host projects (`Tevrin.Api`, `Domain`, `Infrastructure`) or `Tevrin.Pack.*`, and a pack references only the engine. An architecture test in `Tevrin.Engine.Tests` fails the build if that changes. A second test fails if an engine source file contains an SDLC key. The frontend has the same rule: an ESLint rule stops `src/engine/` importing `src/features/` or `src/app/`.

## A definition

The SDLC lifecycle is [`sdlc.pipeline.yaml`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/backend/src/Tevrin.Pack.Sdlc/Definitions/sdlc.pipeline.yaml). A small example that runs in the engine tests is [`fixture-review.pipeline.yaml`](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/backend/tests/Tevrin.Engine.Tests/Fixtures/fixture-review.pipeline.yaml). It runs draft, check, gate, summarise: an agent drafts, a second agent checks the draft (a guard validates the report, with retry and feedback), a gate approves the report, and a third agent summarises.

```yaml
pipeline: fixture-review
version: 1
title: Fixture review
trigger: { kind: manual }
start: [draft]
artifacts:
  draft: { title: Draft, format: json, schema: draft.schema.json, approval: auto_lock }
stages:
  - id: check
    title: Check
    status: checking
    agent: { key: checker, prompt: fixture.check }
    inputs: [draft]
    output: report
    validate: [{ guard: fixture.report-consistent }]
    on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }
```

(Shortened. The full file declares every artifact, status and stage.)

### Top-level keys

| Key | Meaning |
| --- | --- |
| `pipeline` | Id, kebab-case, unique across packs |
| `version` | Integer |
| `title` | Display title |
| `trigger` | `{ kind: manual }`. `schedule` and `webhook` parse but are refused |
| `approval_modes` | Modes the pipeline offers: `strict`, `advisory`, `autonomous` |
| `default_approval_mode` | The mode a new run uses |
| `start` | Stage ids a run can start in. The first is the default. A pack's start selector may pick another |
| `settings` | Names of per-project values the pack supplies, such as `review_cycles` |
| `artifacts` | The artifacts, by key (below) |
| `statuses` | The pack's own statuses, each mapped to a generic run status |
| `status_changes` | Named status-only moves (below) |
| `rollback_status` | Status a rollback sets. Without it, the target stage's status is used |
| `concurrency` | `exclusive: [{ job_type, scope: project }]`. A second active job of that type in the same project is refused. `scope: run` is refused by the validator in 0.7.0 (`DEF_PARSE`, D-0.7.0-37) |
| `stages` | The stages, in order |

### Artifacts

| Key | Meaning |
| --- | --- |
| `title` | Display title |
| `format` | `markdown` (default) or `json` |
| `schema` | Optional JSON Schema file the pack embeds. Agent output is checked against it before it is stored |
| `approval` | `required` (default: a person approves) or `auto_lock` |
| `cascade` | `downstream` (default), `self_and_downstream` or `none`: what approving a new version invalidates |

Dependencies come from stage `inputs`. Artifact A depends on B when the stage that outputs A lists B as an input, directly or through other artifacts.

### Statuses and status changes

`statuses` maps each custom status to a generic one: `active`, `running`, `waiting_on_gate`, `completed`, `failed` or `cancelled`. `completed`, `failed` and `cancelled` are final. A stage's own `status:` is the status while the run is in it.

A `status_changes` entry moves a run's status without changing stage.

| Key | Meaning |
| --- | --- |
| `stage` | The stage it applies to |
| `from` | Statuses it may start from |
| `to` | The new status |
| `completes_run` | Ends the run when true |
| `error_code` | The code returned when the current status isn't in `from` |
| `error_message` | Optional text for that error. Placeholders: `{stage}`, `{status}`, `{change_stage}`, `{from}` |
| `reason` | Optional default reason recorded for the move. Defaults to the name in words |
| `wrong_stage_message` | Optional. A request naming another stage is refused with `WRONG_PHASE` and this text. Without it the named stage is ignored |

### Stages

A stage is an **agent stage** (`agent`), a **code stage** (`handler`) or a **manual stage** (neither: the work happens through pack endpoints, as SDLC execution and release do). A stage can't have both `agent` and `handler`.

| Key | Meaning |
| --- | --- |
| `id`, `title`, `status` | Identity, display title and the pack status while in the stage |
| `agent` | `{ key, prompt?, model?, tools?, job_type? }`. `prompt` defaults to the pack's prompt for the key. `job_type` defaults to `agent.run` |
| `handler` | `<pack>.<name>`, an `IStageHandler` |
| `inputs` | Artifact keys the stage reads |
| `output` | The artifact key the stage writes |
| `requires` | Checks before entering, evaluated in order, all unmet reasons returned together. Entries: `{ artifact_locked: key }`, `{ no_pending_gates: { except: [gate] } }`, `{ guard: pack.name }` |
| `validate` | Guards run on the stage's output, as `{ guard: pack.name }` |
| `on_invalid` | `{ retry_with_feedback, max_attempts (1-10), then: escalate or fail }` |
| `gate` | `{ type, modes, approves?, approval_links? }`. The gate must be approved to enter the stage in the listed modes |
| `on_success` | The stage that follows. Without it, the next stage in the list |
| `on_reject` | A stage id, or `{ to, max_iterations }` |
| `loop` | `{ to, on, max_iterations, requires?, title? }` (below) |
| `enqueue_on_enter` | A job type queued in the same transaction as an advance into the stage. The engine never assumes an agent stage enqueues a job |

A `loop` goes back to an earlier stage when the stage's outcome equals `on`. `max_iterations` is 1 to 100, or `{ setting: <name> }` to read a per-project value. `requires` is the loop's own entry check (a loop never checks the target stage's `requires` or gate). `title` names the loop in transition reasons and errors, and defaults to "Loop".

An `on_reject` that points backwards is also a loop, so it needs `max_iterations`.

## Validator codes

The validator reports every problem at once, each with a code and the YAML path, for example `DEF_UNKNOWN_GUARD at stages[1].validate[0].guard`.

| Code | When |
| --- | --- |
| `DEF_PARSE` | The YAML doesn't parse, a key has the wrong type, or a value is out of range |
| `DEF_DUPLICATE_ID` | Two stages, artifacts, statuses or status changes share an id |
| `DEF_UNKNOWN_STAGE`, `DEF_UNKNOWN_ARTIFACT`, `DEF_UNKNOWN_STATUS` | A reference names something not declared |
| `DEF_UNKNOWN_GUARD`, `DEF_UNKNOWN_HANDLER`, `DEF_UNKNOWN_TOOL`, `DEF_UNKNOWN_JOB_TYPE` | No pack registered that name |
| `DEF_UNKNOWN_PROMPT` | An agent stage's prompt isn't one its pack registered, doesn't parse, requires `{{FEEDBACK}}`, or needs a placeholder that is neither a stage input nor a registered context name. Checked only for a pack that registers prompts |
| `DEF_STAGE_KIND` | A stage has both `agent` and `handler` |
| `DEF_UNREACHABLE_STAGE` | No path from a start stage reaches it |
| `DEF_UNCAPPED_LOOP` | A loop, a backward `on_reject` or a backward `on_success` has no `max_iterations` |
| `DEF_LOOP_FORWARD` | `loop.to` isn't an earlier stage |
| `DEF_SCHEMA_NOT_FOUND` | `schema` names a file the pack doesn't embed, or it isn't valid JSON Schema |
| `DEF_ARTIFACT_NO_PRODUCER` | An artifact is read but no stage outputs it and no pack declares a producer |
| `DEF_UNKNOWN_SETTING` | `{ setting: x }` names a setting not in `settings` |
| `DEF_TRIGGER_NOT_SUPPORTED` | `trigger.kind` is `schedule` or `webhook` |

These are start-up and readiness errors, not API errors. See [Pipeline definitions](../operations/pipeline-definitions.md).

## Hash and pinning

Each definition has a **hash**: the SHA-256 of its canonical JSON. The canonical JSON is the parsed model with keys sorted and no extra whitespace. Comments, key order and formatting don't change the hash, and any changed value does. Defaults (such as an artifact's `format: markdown`) are written into the canonical JSON, so writing one out explicitly doesn't change the hash.

The canonical JSON escapes only the quote, the backslash and control characters U+0000 to U+001F. Every other character is raw UTF-8, so the hash doesn't depend on the .NET version.

The first time a hash is used, its canonical content is stored in `pipeline_definitions`. A run stores the hash it started with and always loads that stored definition. Editing the YAML therefore never changes a run in progress. If the stored definition names a guard, handler or tool the current build no longer registers, moves on that run return 409 `DEFINITION_UNAVAILABLE`. Reads still work.

## Extension points

A pack implements `IPipelinePack` (an `Id` and `Register(IPackRegistry)`). Registering names tells the validator what exists. The implementations are bound by name in the host's DI.

| Extension point | Registered with | Used for |
| --- | --- | --- |
| Definition, schema, prompt | `AddDefinition`, `AddSchema`, `AddPrompt` | Embedded YAML, JSON Schema and prompt templates |
| `IGuard` | `AddGuard("pack.name")`, then `AddPipelineGuard<T>("pack.name")` | `requires` and `validate` checks. Returns pass, or the reasons it failed |
| `IStageHandler` | `AddStageHandler`, then `AddPipelineStageHandler<T>` | Code stages |
| `IAgentTool` | `AddTool`, then `AddPipelineTool<T>` | Tools an agent stage lists in `agent.tools` |
| Job types | `AddJobType` | Types the pack's job handlers process |
| Settings | `AddSetting`, plus an `IRunSettingsProvider` | Per-project values for `{ setting: x }` |
| Artifact producers | `AddArtifactProducer` | A key that a job (not a stage) writes |
| `IAgentContextProvider` | `AddContextName` plus a DI service | Extra prompt context |
| `IInvalidationHook` | A DI service | Pack effects when an approval invalidates artifacts (the SDLC resets steps) |
| `IRunStartSelector` | A DI service | Choosing among several `start` stages |

Guard, handler and tool names start with the pack id (`sdlc.plan-has-steps`). A malformed or wrongly namespaced name throws when the pack registers. The host calls `AddPipelineRegistry()` and `AddPipelinePack<T>()` once.

### Writing a guard

1. Add the name in your pack's `Register`: `registry.AddGuard("velocity.load-rules")`.
2. Implement `IGuard`. `EvaluateAsync` returns `GuardResult.Pass`, or `GuardResult.Fail(...)` with user-facing reasons.
3. Bind it in the host: `services.AddPipelineGuard<LoadRulesGuard>("velocity.load-rules")`.
4. Use it in the definition under `requires` or `validate`.

A `validate` guard receives the stage's output as a `StageOutputFact` in `context.Facts`. Without step 1 the validator reports `DEF_UNKNOWN_GUARD`.

## Validate and retry

When a guard or the artifact schema rejects an agent's output, the stage reruns the agent with the findings appended as feedback, up to `max_attempts`. The findings are the guard's reasons followed by schema errors (path and message), capped at 8,000 characters. After the last attempt, `then: escalate` raises an `escalation` gate on the run, and `then: fail` marks the agent run and its job failed. With `max_attempts: 3` and `then: escalate`, three failing attempts give three agent runs, two with feedback, and one pending escalation gate.

## The SDLC as the first pack

`sdlc.pipeline.yaml` holds the lifecycle: Analysis (a start stage only), Refinement, Specification, Planning, Execution, Review and Release. Its entry gates, statuses and the review loop equal the 0.6.1 rules, and `SdlcLifecycleTransitionTests` keeps a unit test for every allowed and forbidden transition. The characterisation snapshots under `backend/tests/Tevrin.IntegrationTests/Characterisation/Snapshots/` record the whole flow, and CI fails if any changes (`scripts/ci/check-snapshots-frozen.sh`).

In 0.7.0 the SDLC runs on the engine runner. All eight SDLC agents run as agent stages, or as agents bound to a stage (`agents:`), through the same `AgentStageRunner` that new packs use. The SDLC adds its own hooks, context providers and tools (`SdlcAgentExtension`), so the prompts, run records, events, retries and error codes stay the same as in 0.6.1. Golden prompt tests pin the prompts. 0.6.1's separate SDLC runner has been removed. A project that predates pipeline runs gets its run when an agent first needs one.

## Not supported yet

- Schedule and webhook triggers: modelled and validated, but refused at load.
- Starting, advancing, cancelling or rolling back a run through the generic API. SDLC runs still use the project endpoints. The generic API reads runs and decides gates.
- Parallel stages, fan-out, agent-to-agent conversation, tool permission tiers, outcome scoring and a per-agent feedback store.
- Uploading or editing definitions at runtime. Definitions ship inside pack assemblies.
- Any pack other than SDLC, and packaging (NuGet and npm packages, a pack template, command-line tooling).
- Re-running a recorded run against a new prompt version. Replay is read-only.

## Packages

The loader uses YamlDotNet 18.1.0 and the schema check uses JsonSchema.Net, pinned at 8.0.5 because later releases use a commercial licence. Both are referenced by `Tevrin.Engine.Runtime` only.

See also [Architecture](../reference/architecture.md) and [Pipeline definitions](../operations/pipeline-definitions.md).

## Other packs

Since 0.8.0 a second pack, `coach`, runs the `impulse-coach` pipeline. See the [Impulse coaching pack](impulse-coaching-pack.md) guide.
