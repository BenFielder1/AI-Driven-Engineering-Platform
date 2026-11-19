# Tevrin: extracting a reusable agent orchestration engine

Context notes for building the 0.7.0 spec. This is freeform research and suggestions, not a spec. It covers what the current codebase already gives us, what's tied to the software lifecycle, a target structure, how each hard-coded concept becomes generic, an order of work, pitfalls, and the pipelines this engine should be able to run afterwards.

Based on a read of `main` of `BenFielder1/AI-Driven-Engineering-Platform` (Tevrin 0.6.x: ASP.NET Core on .NET 10, EF Core 10 on PostgreSQL 18, Next.js frontend).

---

## Goal

Turn Tevrin's orchestration into a reusable engine: the state machine, job queue, approval gates and run logging. Each new project then needs only agent definitions and a pipeline config. Today's software lifecycle becomes the first "pack" on the engine, so Tevrin becomes a platform rather than a single app. Building a new pipeline should take a weekend.

---

## Current state: what's already generic and what's hard-coded

### Ready to lift out almost as-is

- **Postgres job queue**: `Aide.Api/Jobs/JobWorker.cs`, `JobContracts.cs`, `JobEnqueuer.cs`, `Aide.Domain/Jobs/JobPolicy.cs`, `Aide.Domain/Entities/Job.cs`.
  - Claims jobs with `FOR UPDATE SKIP LOCKED`, and has leases, retries, dead letters, deferral (`JobDeferredException`) and job-ended/succeeded hooks.
  - Job types are already plain strings. `IJobHandler` plus `JobHandlerRegistration` is already a plugin-style registration.
  - No Redis or broker, by design. Keep it that way.
- **LLM layer**: `Aide.Infrastructure/Agents/LlmContracts.cs` (`ILlmProvider`, `LlmRequest`, `LlmCompletion`), `LlmResolver.cs`, `LlmStreaming.cs`, `LlmTools.cs`, `HttpLlmProviders.cs`, `Aide.Infrastructure/Llm/*`.
  - Anthropic, OpenAI, Ollama, plus `FakeLlmProvider` for deterministic tests. Keep the Fake provider as part of the engine, because it's what makes pipelines testable.
- **Artifacts**: `Aide.Domain/Artifacts/*` (`ArtifactIntegrity`, `ContentHash`, `LineDiff`, `InvalidationCascade`, `RestoreRules`, `ArtifactRules`).
  - Versioned, hash-checked, diffable, with an invalidation cascade and restore. All domain-agnostic apart from the `ArtifactType` enum.
- **Run logging**: `Aide.Domain/Entities/AgentRun.cs` already records the model, provider, prompt version, input artifacts, user feedback, credential and tokens.
  - It already has `AgentKey` alongside the `AgentKind` enum, so it's halfway to string keys.
- **Gate mechanics**: `Aide.Domain/Gates/GateRules.cs` (`GateFollowUp`, `GateResolution`), `GateTimers.cs`, approval links, notifications (in-app, email, Slack, Teams, signed webhooks), audit events, `SecretRedaction`, `PromptTemplate`.
- **Architecture tests**: `tests/Aide.UnitTests/Architecture/DomainDependencyTests.cs` is the pattern for enforcing that the engine never depends on a pack.

### Hard-coded to the software lifecycle

- **`Phase` enum** (`Aide.Domain/Lifecycle/Enums.cs`) and **`PhaseOrder.Next()`** (`PhaseOrder.cs`): a fixed linear sequence, Analysis → Refinement → Specification → Planning → Execution → Review → Release.
- **`LifecycleStateMachine.UnmetEntryConditions`** (`LifecycleStateMachine.cs`): a `switch` on the phase being entered, with SDLC-specific rules ("technical specification locked", "working copy prepared", "all plan steps completed", "review decision release_eligible").
- **`GateRules.RequiredGate(mode, phase)`**: gates are mapped to phases in code (spec_approval before Planning, plan_approval before Execution in strict mode, pre_release before Release).
- **Enums**: `AgentKind` (`Aide.Domain/Agents/Enums.cs`), `ArtifactType` (`Aide.Domain/Artifacts/Enums.cs`), `GateType` (`Aide.Domain/Gates/Enums.cs`), `AgentJobTrigger`, and the `JobTypes` constants (`Aide.Domain/Jobs/JobTypes.cs`).
- **SDLC-only `TransitionKind`s**: `ReviewCycle`, `ReleaseNoop`, `ReleaseCompleted`, `NewVersion`, `ReleaseAwaitingMerge`, `ReleaseMergeAbandoned`.
- **`ProjectStatus`**: Building, Reviewing, Releasing, Released, AwaitingMerge. Status is derived from the phase via `PhaseOrder.StatusFor`.
- **Agent job handlers**: `Aide.Api/Agents/*` (`AgentRunner`, `AgentJobHandlers`, `ArtifactAgentJob`, `ExecutionJobHandler`, `RepositoryAnalysisJobHandler`, `StackAnalysisJobHandler`). The generic "run an agent, write an artifact" logic is mixed in with repository and git context.

Phases are already stored as snake_case strings (the comment on `Phase` says numeric values only give the order), so moving from enums to string keys should need little or no database migration.

---

## Target structure

```
Tevrin.Engine.Domain     pure: PipelineDefinition, generic state machine, gate/guard rules, artifact rules
Tevrin.Engine.Runtime    job queue, AgentRunner, LLM providers, artifact store, gates, notifications, run log
Tevrin.Engine.Api        generic endpoints + SignalR: runs, stages, artifacts, gates, timeline
@tevrin/engine-ui        React: run timeline, artifact viewer/diff, gate inbox, run replay
Tevrin.Pack.Sdlc         today's Tevrin: git hosts, deploys, stack catalog, release/merge logic
Tevrin.Pack.Coach        first new pack (Impulse coaching engine)
```

Rules:
- Packs depend on the engine. The engine never depends on a pack. Add an architecture test for this on day one, in the same style as `DomainDependencyTests`.
- `Tevrin.Engine.Domain` stays free of EF Core and ASP.NET Core, the same rule as `Aide.Domain` today.

---

## What a pack looks like

A pack has two parts: a declarative pipeline definition, and a small set of named C# extension points for anything the YAML shouldn't express.

### 1. Pipeline definition (YAML)

Example, the coaching engine:

```yaml
pipeline: impulse-coach
version: 1
trigger: { kind: manual }          # or schedule / webhook
approval_mode: advisory            # strict | advisory | autonomous

artifacts:
  session_log:   { schema: schemas/session_log.json }
  performance:   { schema: schemas/performance.json }
  plan_draft:    { schema: schemas/plan.json }
  coach_message: { schema: schemas/message.json }

stages:
  - id: analyse
    agent: { key: performance-analyst, prompt: prompts/analyst.md, model: sonnet }
    inputs: [session_log]
    output: performance

  - id: plan
    agent: { key: programme-planner, prompt: prompts/planner.md, model: opus }
    inputs: [performance]
    output: plan_draft
    validate:                       # deterministic code that can reject the plan
      - guard: velocity.load-rules  # registered C# class
    on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }

  - id: review
    gate: { type: plan_approval, modes: [strict, advisory] }

  - id: write
    agent: { key: coach-voice, prompt: prompts/coach.md, model: haiku }
    inputs: [plan_draft]
    output: coach_message
```

Concepts the definition needs to cover from the start:
- **Artifacts**: a type key, a JSON schema, and dependencies derived from stage `inputs` (which drives the invalidation cascade).
- **Stages**: either an agent stage (key, prompt, model, inputs, output, tools) or a code stage (a named `IStageHandler`).
- **Transitions**: linear by default. Allow explicit `on_success` / `on_reject` / loop-back with `max_iterations`.
- **Guards**: a `requires:` list before entering a stage (built-ins like "artifact X locked", plus named `IGuard`s), and `validate:` after a stage produces its output.
- **Gates**: declared on the stage, with the approval modes that require them.
- **Triggers**: manual, schedule or webhook.
- **Approval mode**: set for the whole run, with the gate list deciding what it means.

### 2. Extension points (C#)

```csharp
public interface IGuard         { Task<GuardResult> CheckAsync(StageContext ctx, CancellationToken ct); }
public interface IStageHandler  { Task<StageOutcome> RunAsync(StageContext ctx, CancellationToken ct); }  // non-LLM stages
public interface IAgentTool     { ToolSpec Spec { get; } Task<string> InvokeAsync(JsonElement args, CancellationToken ct); }

public interface IPipelinePack
{
    string Id { get; }
    void Register(IPackRegistry registry);  // guards, handlers, tools, custom job types
}
```

A pack is then: the YAML, a prompts folder, JSON schemas, and one `IPipelinePack` class. For the SDLC pack, the `UnmetEntryConditions` switch turns into a handful of `IGuard` classes, and deployments, merge polling, tracker imports, managed databases and so on become custom job types the pack registers.

---

## How each hard-coded piece becomes generic

| Today | In the engine |
|---|---|
| `Phase` enum + `PhaseOrder.Next` | Stage IDs from the definition, plus a transition graph (`on_success`, `on_reject`, loop back with `max_iterations`) |
| `UnmetEntryConditions` switch | A `requires:` list on each stage: built-in conditions like "artifact X locked" plus named `IGuard`s |
| `GateRules.RequiredGate(mode, phase)` | A `gate:` block on the stage, listing which approval modes need it |
| `GateFollowUp` | Stays the same: approve → transition, reject → rerun agent with feedback, escalate (continue / rollback / accept). Already generic |
| `AgentKind` / `ArtifactType` / `GateType` enums | String keys. The current values become constants inside the SDLC pack |
| `JobTypes` constants | A few engine job types (`stage.run`, `agent.run`, `gate.timer`, `trigger.fire`) plus job types each pack registers |
| `AgentJobTrigger` | Generic triggers: `user_requested`, `stage_entered`, `gate_rejected`, `artifact_rejected`, `artifact_corrupted`, `validation_failed` |
| Project phase/status/checkpoint columns | A `runs` table: pipeline ID, **pinned definition hash**, current stage, status, checkpoint |
| `ProjectStatus` derived from the phase | Generic run status (active, waiting_on_gate, running, completed, failed, cancelled), plus custom statuses a pack can declare |
| `ReviewCycle`, `ReleaseAwaitingMerge`, etc. | Become loops and custom states declared by the SDLC pack, not engine concepts |
| Checkpoint and rollback rules | Stay in the engine: the checkpoint increments on each forward transition, and rollback targets an earlier checkpoint. Already mostly generic |

Each run is pinned to the hash of the exact definition it started with, so editing the YAML never breaks runs already in progress.

---

## Order of work

Replace things gradually inside the existing repo, keeping everything working at each step. The existing integration tests (`tests/Aide.IntegrationTests`, including `Lifecycle/AdvancePhaseTests`, `Gates/GateDecisionTests`, `Agents/*`, `Jobs/JobWorkerTests`) and unit tests (`Lifecycle/LifecycleStateMachineTests`) are the safety net.

1. **Characterisation tests (about 1 week).** Use the Fake provider to run the full SDLC flow end to end and snapshot the sequence of transitions, gates and artifacts it produces. Every later step must keep this snapshot unchanged.
2. **Lift out the generic pieces (1–2 weeks).** Move the job queue, LLM layer, artifact integrity/diff/cascade, notifications, audit and redaction into `Tevrin.Engine.*`. Namespace moves only, no behaviour changes. Add the "engine never references a pack" architecture test.
3. **Switch to string keys (about 1 week).** Replace `AgentKind`, `ArtifactType` and `GateType` with strings plus constants in the SDLC pack. Use EF value conversions. Expect at most a small migration.
4. **Generic state machine (2–3 weeks).** Build the definition model, loader, validator (unknown guard names, unreachable stages, loops with no cap, schema references that don't resolve) and a generic `Apply(run, transition)` with guards. Write the SDLC lifecycle as a definition plus guards, then run the old and new state machines side by side across the whole test suite and assert they decide the same things. Only switch over once they agree everywhere. This is the riskiest step, which is why the two run in parallel first.
5. **Generic API and UI (1–2 weeks).** The run timeline, gate inbox, artifact viewer/diff and run replay should be driven by the definition, so a new pack gets a working UI without any frontend code.
6. **First new pack as the test (a weekend).** Build the coaching engine using only YAML, prompts and one `velocity.load-rules` guard. Any time you have to change engine code, you've found a gap. Fix it in the engine, not in the pack.
7. **Packaging (later).** Publish NuGet packages and an npm UI package, add a `dotnet new tevrin-pack` template, and add `tevrin pipeline validate` / `tevrin run start` to the CLI.

### Run logging to build in from the start

Log every agent's input, output, tokens, latency, model and prompt version for each run, so you can replay runs and compare prompt versions. `AgentRun` has most of this already. Add per-stage timings and a run-level cost total. Being able to see what happened is what makes multi-agent systems possible to debug.

---

## Pitfalls

- **Don't generalise ahead of real needs.** Fan-out, ensembles and running agents in parallel are needed by some of the pipelines below, but add them when you build those packs, not now. Let the engine grow out of three real packs.
- **Leave the domain-specific parts in the SDLC pack.** Git hosts (GitHub, GitLab, Bitbucket), deployments (Vercel, Netlify, Cloudflare, GitHub Pages, Render, Railway), managed databases, the stack catalog/composition, tracker imports and release eligibility don't belong in the engine.
- **Don't let the YAML turn into a programming language.** Once you need conditions inside it, use a named guard written in C# instead.
- **Keep the one-execution-per-project rule configurable.** It's enforced by a partial unique index on `jobs` today. Some pipelines will want it per run, and others will want parallel stages.
- **Plan the `aide` identifiers.** Internal names still use `Aide.*`. The engine projects are a natural point to start using `Tevrin.*` without a big rename of the existing app.
- **Watch the licence.** PolyForm Shield stops others offering a competing product, but if you publish engine packages, decide whether the engine gets the same licence or a more permissive one.

---

## What could be built on the engine afterwards

Eight candidate pipelines. Each one teaches a different way of arranging agents, which makes them good tests of whether the engine is general enough. The engine features each one needs are listed so they can be prioritised when the time comes.

### 1. Adversarial debate: market research desk
**Scanner → Bull analyst + Bear analyst (argue against each other) → Risk manager → Portfolio manager → Post-mortem agent**
- The scanner flags tickers or events. Two analysts each build the strongest case for and against.
- The risk manager can veto on position size or concentration. The PM decides, and its reasoning is logged.
- After 1, 5 and 20 days, the post-mortem agent grades past calls and writes up what each analyst got wrong, which feeds back into future prompts.
- Could plug into the existing Alpaca trading bot scaffold as the layer that makes decisions, with paper trading only.
- **Engine features needed:** parallel stages, a veto guard, scheduled delayed jobs (the post-mortems), a feedback store per agent.

### 2. Ensemble forecasting: Polymarket probability desk
**News watcher → 3 independent forecasters (different prompts/models, no shared context) → Aggregator → Calibration tracker**
- The forecasters must not see each other's output. The aggregator combines their estimates and flags big disagreements.
- The tracker scores every forecast once the market resolves (Brier score), so you can see which forecaster is actually calibrated.
- **Engine features needed:** fan-out with isolated context, aggregation stage, external outcome scoring of agent runs.

### 3. Simulation agents: contact-centre QA lab
**Customer simulator + Agent simulator → Transcript → Intent/sentiment classifier → QA scorer (against a rubric) → Coaching note writer → Trend reporter**
- Two agents role-play calls with set personas (angry customer, confused elderly caller, refund abuser), which gives unlimited synthetic data with no privacy problems.
- The scoring happens in parallel across hundreds of calls and gets summarised into weekly trends.
- Close to the day job (CCaaS), so a strong portfolio piece for .NET roles.
- **Engine features needed:** multi-turn agent-to-agent conversation stage, map-reduce over many items, scheduled aggregation.

### 4. Grounding/verification: job application pipeline
**Job scout → Fit scorer → CV tailor → Fact-checker → Cover letter writer → Tracker**
- The fact-checker matters most. It checks every claim in the tailored CV against a master CV, and rejects anything it can't trace back to it.
- A person reviews and sends each application. Nothing goes out automatically.
- **Engine features needed:** a validator agent that can reject with structured findings, a source-of-truth artifact pinned across runs, a human gate before any external action.

### 5. Rule-checked agent output: Impulse coaching engine (proposed first pack)
**Session logger → Performance analyst (trends across PBs and splits) → Programme planner (Velocity framework) → Load/safety validator → Coach-voice writer**
- The validator is deterministic code, not an LLM. It enforces periodisation rules (weekly volume increases, taper rules, minimum recovery between hard sessions). The planner has to keep revising until the plan passes.
- Could ship as a real feature in Impulse.
- **Engine features needed:** code guard on output, retry with feedback, cap on attempts, then escalate. This is the minimum set, which is why it's the first pack.

### 6. Investigation with tool permissions: on-call triage agent
**Alert watcher → Log investigator → Hypothesis generator → Runbook executor → Post-mortem writer**
- Point it at the local Docker Desktop Kubernetes cluster and break things on purpose (crash-looping pods, bad config, memory pressure).
- Permissions come in tiers. Investigators can only read. The executor can restart or scale things but has to ask before anything destructive.
- **Engine features needed:** webhook trigger, tools on agents, permission tiers per tool, an approval gate in the middle of a stage for destructive tool calls.

### 7. Editorial loop: content studio
**Topic scout → Researcher → Writer → Fact-checker → Editor → Title/thumbnail variants**
- The editor can send work back to *any* earlier stage, not just the one before it.
- Useful if the finance/graduate-life YouTube channel idea comes back.
- **Engine features needed:** routing back to any earlier stage, a cap on revision rounds across the whole run, multiple output variants.

### 8. Emergent behaviour, for fun: agent economy
- Run 5–10 agents as traders, each with a different goal (market maker, momentum chaser, value investor), in a simulated order book or a Diplomacy-style negotiation game.
- **Engine features needed:** agents talking to each other with no fixed order, a shared environment state, and ways to evaluate runs where there's no single right answer. This is the furthest from a pipeline, so it's a stretch test of the engine, not a near-term goal.

### Suggested order after the engine

1. **#5 Impulse coaching engine.** Small, useful straight away, and needs the fewest engine features. It's the acceptance test for the extraction.
2. **#1 or #2.** Strong feedback loops, and they build on the existing trading bot work. They push the engine towards parallel stages, fan-out and outcome scoring.
3. **#3 QA lab.** Best for career and portfolio value, and pushes map-reduce and agent-to-agent conversation.

Features that keep coming up across packs, roughly in the order they'll be needed:
1. Code guards with retry and feedback.
2. Parallel stages and fan-out with isolated context.
3. Scheduled and delayed jobs.
4. Scoring agent outputs against external outcomes.
5. Tool permission tiers.
6. Agent-to-agent conversation.
