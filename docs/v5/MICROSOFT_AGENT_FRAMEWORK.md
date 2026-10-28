# Microsoft Agent Framework: fit for the AIDE backend

*Research note for v5 planning, 2026-10-28. Not a spec: nothing here is a requirement until it lands in `SPEC.md`.*

## Summary

**Recommendation: don't adopt Microsoft Agent Framework (MAF) as AIDE's orchestration engine. Do consider a narrow,
spike-first adoption of the layer under it, `Microsoft.Extensions.AI` (`IChatClient`), for the provider calls and GenAI
telemetry. Revisit MCP tools as a separate v5 feature.**

- MAF's main selling points are the agent loop, multi-agent orchestration and checkpointed workflows. AIDE already
  builds these itself, with stronger guarantees than MAF offers:
  - lifecycle state machine in `Aide.Domain`
  - PostgreSQL job/outbox queue with leases and retries
  - idempotent run ids derived from job ids
  - approval gates, immutable versioned artifacts and an audit event log

  Swapping them in would mean rewriting the core while losing determinism and auditability.
- The part of AIDE that MAF could genuinely shrink is the hand-written provider layer:
  - `HttpLlmProviders.cs`, `LlmStreaming.cs` and the three stream accumulators come to about 1,300 lines
  - MAF delegates that layer to `IChatClient` implementations from the vendor SDKs (OpenAI, Anthropic, OllamaSharp)
- That swap carries real risk. AIDE's v4 behaviour is precise and covered by tests (FR-160, FR-161, FR-162), so the
  change only pays off if a spike shows the SDK clients preserve it.

## 1. What Microsoft Agent Framework is

MAF is Microsoft's open-source SDK for building AI agents and multi-agent workflows in .NET and Python, with Go in
preview. It merges Semantic Kernel's enterprise foundations with AutoGen's orchestration patterns, and both of those are
now in maintenance in its favour.

| Item | Status (as of Oct 2026) |
| --- | --- |
| 1.0 GA (.NET + Python) | 3 April 2026, with a backward-compatibility commitment for the 1.0 feature set |
| Current .NET packages | `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Harness` at 1.25.x (released) *(secondary source)* |
| Provider packages | `Microsoft.Agents.AI.OpenAI` and `Microsoft.Agents.AI.Anthropic`; Ollama through OllamaSharp's `IChatClient` |
| Prerelease | `Microsoft.Agents.AI.Foundry`, `Microsoft.Agents.AI.DurableTask`, .NET AG-UI server, DevUI, Foundry observability and evaluations |
| Release cadence | Fast: roughly 25 minor versions in 7 months. Breaking changes are mostly in preview and hosting packages |

### Building blocks (.NET)

- **`AIAgent` / `ChatClientAgent`**: an agent is instructions plus tools plus a chat client.
  - `ChatClientAgent` wraps any `Microsoft.Extensions.AI.IChatClient`, so any provider with an `IChatClient` works.
  - This is the key point for AIDE: the provider abstraction underneath MAF is `Microsoft.Extensions.AI`, which can be
    adopted on its own, without the agent framework.
- **Function tools**: C# methods exposed as tools, with an automatic tool-call loop (MEAI's `FunctionInvokingChatClient`).
- **Middleware**: hooks around agent runs and function calls, for logging, safety, compliance and approval.
- **Sessions, memory and context providers**:
  - serialisable conversation state
  - custom `ChatHistoryProvider`
  - memory backends: Foundry, Mem0, Redis, Neo4j or custom
- **Workflows**:
  - a typed graph of *executors* run in *supersteps*, with branching, fan-out/fan-in and human-in-the-loop requests
  - checkpointing at the end of each superstep (`CheckpointManager`, `RestoreCheckpointAsync`, `ResumeStreamingAsync`)
- **Orchestrations**: built-in sequential, concurrent, handoff, group chat and Magentic-One patterns.
- **Declarative agents and workflows**: YAML definitions.
- **MCP**: agents discover and call tools on MCP servers.
- **A2A**: agent-to-agent protocol. 1.0 support was "coming soon" at GA.
- **Harness agent** (`HarnessAgent`): a local runtime with shell, file-system and messaging access, Claude Code / Copilot
  style.

### Provider support relevant to AIDE

AIDE's providers are Anthropic, OpenAI, Ollama and Fake.

| Provider | MAF / MEAI route | Function tools | Notes |
| --- | --- | --- | --- |
| Anthropic | `Anthropic` SDK + `Microsoft.Agents.AI.Anthropic` | ✅ | Client-managed history only. One AutoGen migration guide still lists Anthropic as "planned", which conflicts with the GA post. The provider docs page shows it as available |
| OpenAI | `OpenAI` SDK + `Microsoft.Agents.AI.OpenAI` | ✅ | Chat Completions and Responses APIs |
| Ollama | `OllamaSharp` (`OllamaApiClient : IChatClient`) | ✅ | No MCP or hosted tools |
| Fake | Implement `IChatClient` | n/a | Simple to write. AIDE's scripted fakes would need porting |

MAF also opens the door to Gemini, Bedrock, Azure OpenAI, Foundry and Mistral almost for free, if that is ever wanted.

## 2. What AIDE does today (the comparison baseline)

| Concern | AIDE implementation | MAF equivalent |
| --- | --- | --- |
| Provider abstraction (CLAUDE.md rule 10) | `ILlmProvider` / `IToolCallingLlmProvider` (`Aide.Infrastructure/Agents/LlmContracts.cs`, `LlmTools.cs`) | `IChatClient` (MEAI) |
| HTTP + streaming (FR-161) | `HttpLlmProviders.cs`, `LlmStreaming.cs`. Own SSE/NDJSON parsing, `IdleTimeoutStream` (2 min idle), 30 min whole-call ceiling, usage from stream events, transient/permanent error classification, secret redaction | Vendor SDKs' streaming. Idle timeout, error classification and redaction would still be AIDE's job |
| Tool loop (FR-71) | `LlmToolLoop`: 25-call `TOOL_LIMIT`, forced final answer, `MaxTurns = 28`, budget hook between turns, tool-unsupported fallback context, every call logged to `tool_calls` | `FunctionInvokingChatClient` (auto loop) or a manual loop over `IChatClient` |
| Long outputs (FR-160, FR-162) | Per-model `max_tokens`/`max_completion_tokens`/`num_predict`; up to 5 continuations with overlap-stitching and a context-window check | `ChatOptions.MaxOutputTokens`; no continuation feature, so AIDE keeps its own |
| Agent run lifecycle (FR-22–FR-26) | `AgentRunner`: one run row per job, run id derived from job id (redelivery-safe), budget refusal, `RUN_*` events, retries 2/8/32 s, dead-letter hook | MAF has no persisted run record. AIDE keeps this |
| Orchestration | Domain state machine (Refinement → … → Release), gates, `ExecutionJobHandler` running one step attempt per job, Guardian checks, 4th rejection → escalation gate, resume after commit (D17) | Workflows + checkpoints, orchestrations, HITL requests |
| Specialists (FR-120, FR-121) | One code-generator run with a composed prompt (base → composition → role → pack skills), recorded `agent_key` and `packs[]` | Multiple `ChatClientAgent`s with instructions, or declarative YAML agents |
| Durable state | PostgreSQL (EF Core), `xmin` concurrency, outbox, immutable artifacts | `CheckpointManager`: in-memory or file in the docs; Cosmos in Python; no PostgreSQL store; custom store possible. `DurableTask` is prerelease and targets Azure Durable Task Scheduler |
| Observability | OpenTelemetry (ASP.NET, HTTP, Npgsql), JSON logs, run cost and tokens in the DB | MEAI `UseOpenTelemetry()` emits GenAI semantic-convention spans and metrics (model, tokens, tool calls) |
| Tests (rule 10: no real provider calls) | `FakeLlmProvider` + scripted responders, stub streams asserting exact request bodies | Fake `IChatClient`. The existing scripts and stub-stream tests need rework |

## 3. Feasibility

**Technically feasible.**

- MAF targets modern .NET, and AIDE is on .NET 10 with central package management.
- The packages are MIT-licensed, have no Azure dependency for the core and provider packages, and need no new
  infrastructure.
- Nothing in the core requires Foundry, Cosmos or Azure Functions.

Constraints:

1. **Persistence and durability don't match.**
   - MAF workflow checkpoints are an opaque state blob tied to the workflow topology. Executor and agent ids must stay
     stable across deployments, or resume fails.
   - AIDE's durability is relational and domain-shaped: steps, runs, artifacts and events.
   - Using MAF checkpoints would need a custom PostgreSQL `CheckpointManager` store, and it would add a second source of
     truth beside the domain state machine. That breaks the spirit of rule 8: lifecycle changes go only through the
     domain state machine.
2. **Exactly-once is not offered.**
   - Microsoft states that recovery "does not imply exactly-once execution of external effects".
   - AIDE already solves this for its side effects: run ids derived from job ids, and step commits detected on
     redelivery (D17). MAF wouldn't add anything here.
3. **The `DurableTask` integration is prerelease and Azure-centred** (Durable Task Scheduler and Azure Functions). AIDE
   deploys to Railway and Docker Desktop Kubernetes, so this is a non-starter.
4. **v4 provider behaviour must survive a swap.** Each of these needs proof in a spike:
   - per-model output limits (FR-160)
   - streaming with idle and whole-call timeouts (FR-161)
   - cost and usage taken from streamed usage events
   - stop-reason mapping (`max_tokens`, `length`) that drives continuations (FR-162)
   - Ollama `num_predict` and tool-support detection
   - key redaction in error text and the `LlmProviderException` transient/permanent split that drives job retries
5. **Churn.** Weekly-ish releases and a stream of preview packages mean pinning versions and budgeting upgrade work.
   AIDE's own provider code currently moves only when AIDE needs it to.
6. **CLAUDE.md rule 10 still holds.** Calls stay behind one abstraction, are never made from endpoints and are faked in
   tests. The abstraction would become `IChatClient`, or an AIDE interface over it. `AgentRunner` stays the only caller.

## 4. Possible use cases, ranked

### A. Provider layer on `Microsoft.Extensions.AI` (`IChatClient`). *Worth a spike.*

**What:** replace the HTTP clients and stream accumulators with vendor-SDK `IChatClient`s:

- `OpenAI` and `Anthropic` SDKs through their MEAI adapters
- OllamaSharp for Ollama

`ILlmProvider` becomes a thin adapter, or `IChatClient` becomes the abstraction. Keep `LlmToolLoop`, continuations,
budgets and `AgentRunner` as they are, and do **not** enable automatic function invocation. AIDE's loop rules (TOOL_LIMIT,
forced final answer, budget hook, tool-call log) stay AIDE's.

**Gains:**

- Roughly 1,000+ fewer lines of protocol code to maintain.
- Vendor SDKs track API changes such as new stop reasons, Responses API features and thinking blocks.
- Easier to add providers like Gemini or Bedrock.
- Standard `ChatResponseUpdate` streaming.

**Costs and risks:**

- Behaviour drift at the edges: usage events, stop reasons, idle detection and error mapping.
- AIDE's FR-161 stub-stream tests assert wire-level details, so they need rethinking or must move to an HTTP-handler
  level fake under the SDK.
- More transitive dependencies.
- Keys are still resolved per project and per connection (FR-79), so clients must be created per run, not as DI
  singletons.

**Spike exit criteria:** with MEAI clients, all FR-160, FR-161 and FR-162 acceptance tests pass unchanged against
recorded or stubbed provider streams, and secrets never appear in exception text.

### B. GenAI OpenTelemetry. *Cheap if A happens; low value alone.*

**What:** MEAI's `UseOpenTelemetry()` pipeline emits spans and metrics that follow the OpenTelemetry GenAI semantic
conventions: model, token counts, tool calls and latency. These would flow into AIDE's existing OpenTelemetry setup and
Railway tracing.

**Gain:** per-call LLM visibility alongside HTTP and Npgsql spans.

**Caveat:**

- Prompt and response content capture must stay off, or be redacted. Prompts contain repo content.
- Without use case A, AIDE can emit equivalent spans itself from `AgentRunner` in a few dozen lines.

### C. MCP tools for specialists. *Candidate v5 feature, not a reason to adopt MAF.*

**What:** let specialists call MCP servers, for example a docs server for a pack, or a GitHub, GitLab or Bitbucket MCP
server for issue context, in addition to the credential-free repo snapshot tools (FR-71).

**Gain:** extensible tools without AIDE coding each one.

**Caveats:**

- This is a security change. MCP servers can carry credentials and reach the network, which the current snapshot tools
  deliberately avoid.
- It needs its own spec section covering allow-lists, per-project configuration, audit of each call and secret handling.
- It can be built on the official MCP C# SDK (`ModelContextProtocol`) and AIDE's own tool loop, so MAF isn't required.

### D. Middleware-style cross-cutting policy. *Low value.*

MAF's run and function middleware would suit redaction, budget checks and tool approval. AIDE already does all three
inside `AgentRunner` and `LlmToolLoop`, so moving them is churn without new capability.

### E. Workflows or orchestrations for execution (parallel steps, review panels). *Not recommended.*

**What:**

- MAF concurrent workflows could run independent plan steps in parallel.
- A group-chat or Magentic orchestration could run a multi-reviewer Code Review (security, spec compliance, stack
  reviewer).

**Why not:**

- Both are achievable inside AIDE's job model: fan out jobs and join on step status, or run several review agent runs and
  merge their findings.
- That route keeps gates, events, budgets, idempotency and the audit trail.
- MAF orchestrations hold conversation state in memory or in checkpoints, outside the domain model.
- Free-form agent-to-agent chat also works against AIDE's deterministic, reviewable lifecycle.

If v5 wants parallel execution or panel reviews, specify them in AIDE's own terms.

### F. Durable workflows, Foundry hosted agents, Harness agent, AG-UI, DevUI. *Not relevant.*

| Option | Reason |
| --- | --- |
| Durable workflows | Prerelease and Azure-bound |
| Foundry hosted agents | Azure-bound, and they move the agent definition out of AIDE |
| Harness agent | Shell and file-system access conflicts with the Guardian and the credential-free snapshot design |
| AG-UI | AIDE's UI talks to its own API and SignalR, not to agents directly |
| DevUI | A local debugger and preview only |

## 5. Pros and cons

### Pros

- Official Microsoft .NET SDK with a GA backward-compatibility commitment, MIT-licensed and active.
- The `IChatClient` base is becoming the standard .NET LLM abstraction, with broad provider coverage (Anthropic, OpenAI,
  Ollama, Gemini, Bedrock, Azure, Mistral).
- Vendor SDKs would own wire protocols and streaming, so AIDE stops maintaining its own SSE and NDJSON parsers.
- OpenTelemetry GenAI conventions come built in.
- MCP support is first class.
- Rich orchestration patterns are available if AIDE ever wants open-ended multi-agent behaviour.
- Semantic Kernel and AutoGen are converging here, so it's the strategic .NET agent stack.

### Cons

- **Overlaps with AIDE's core value.** AIDE *is* a governed orchestration engine. MAF workflows and orchestrations would
  duplicate the lifecycle, job queue and checkpointing with weaker guarantees: no PostgreSQL store, opaque checkpoints, no
  exactly-once.
- **Second source of truth.** Checkpoint blobs beside domain state work against rule 8 and the audit trail.
- **Topology-coupled resume.** Executor and agent ids must stay stable across deploys, which makes prompt and specialist
  catalogue changes (FR-120–FR-122) riskier.
- **Migration cost on tested behaviour.** FR-160, FR-161 and FR-162 and their stub-stream tests, the Fake provider
  scripts, error classification and redaction all need rework.
- **Churn.** Fast release cadence, many prerelease packages, and occasional breaking changes in hosting and preview
  areas.
- **Azure gravity.** Durable, hosted and observability features lean on Foundry, Azure Functions and Cosmos, none of which
  AIDE uses.
- **Dependency weight.** The OpenAI, Anthropic and OllamaSharp SDKs and MEAI add transitive packages to audit.

## 6. Suggested next steps (if v5 takes this up)

1. **Spike (use case A), time-boxed, on a branch.**
   - Implement `ILlmProvider` / `IToolCallingLlmProvider` over `IChatClient` for Anthropic only.
   - Run the existing FR-160, FR-161 and FR-162 tests unchanged.
   - Record the gaps: usage, stop reasons, idle timeout, error mapping, redaction.
   - Decide go or no-go on that evidence.
2. **If go:**
   - Add a SPEC decision (D-V5-x) that `IChatClient` is the provider transport and `ILlmProvider` remains AIDE's
     abstraction.
   - Migrate OpenAI, then Ollama.
   - Turn on GenAI OpenTelemetry (use case B) with content capture off.
3. **Spec MCP tools (use case C)** as a separate v5 feature with its own security requirements, independent of MAF.
4. **Don't adopt MAF workflows or orchestrations.** Record that as a v5 decision so it isn't revisited by default. Any
   parallel execution or review panels are specified in AIDE's job and gate model.

## 7. Confidence and open points

- **Primary sources:** release status and the 1.0 feature list come from Microsoft's announcement. Checkpoint semantics
  and provider tables come from Microsoft Learn.
- **Secondary sources:**
  - current package versions (1.25.x) and the post-1.0 change list come from a third-party overview
  - Durable Task details come from a third-party post

  Check NuGet before relying on any version number.
- **To verify in the spike:**
  - whether the Anthropic `IChatClient` exposes streamed usage and the `max_tokens` stop reason faithfully
  - whether OllamaSharp honours `num_predict` through `ChatOptions.MaxOutputTokens`
- **Conflicting reports:** sources disagree on Anthropic and Ollama connector maturity (the GA post vs an AutoGen
  migration guide). The current provider docs list both as available.

## Sources

- [Microsoft Agent Framework Version 1.0 (DevBlogs, Apr 2026)](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/)
- [Workflows: Checkpoints (Microsoft Learn)](https://learn.microsoft.com/agent-framework/workflows/checkpoints)
- [Model providers (Microsoft Learn)](https://learn.microsoft.com/en-us/agent-framework/agents/providers)
- [AnthropicClientExtensions.AsAIAgent (API reference)](https://learn.microsoft.com/en-us/dotnet/api/anthropic.anthropicclientextensions.asaiagent)
- [Microsoft Agent Framework in October 2026: What Changed Since 1.0 (Developers Digest)](https://www.developersdigest.tech/blog/microsoft-agent-framework-whats-new-2026)
- [Agent Framework workflows survive process restarts via Durable Task (Start Debugging, May 2026)](https://startdebugging.net/2026/05/agent-framework-durable-workflows-checkpoint-restart/)
- [Microsoft Agent Framework 1.0: AI agents in C# (Start Debugging, Apr 2026)](https://startdebugging.net/2026/04/microsoft-agent-framework-1-0-ai-agents-in-csharp/)
- [Microsoft Agent Framework 1.0 multi-agent orchestration guide (Noqta)](https://noqta.tn/en/blog/microsoft-agent-framework-1-0-multi-agent-orchestration-guide-2026)
