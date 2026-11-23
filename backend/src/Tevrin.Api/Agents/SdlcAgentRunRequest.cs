using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Api.Agents;

/// <summary>
/// One agent invocation requested by a job handler (FR-23). <see cref="Inputs"/> are the prompt placeholder values
/// (<c>PROJECT_REQUEST</c>, …); <see cref="InputArtifacts"/> are the artifact versions they came from.
/// <see cref="UserFeedback"/> is stored on the run and fills <c>{{USER_FEEDBACK?}}</c> unless an input overrides it.
/// <see cref="RunKey"/> distinguishes several runs inside one job (e.g. a corrective re-run, FR-28). FR-318–FR-320: it runs
/// on the engine runner through <see cref="SdlcAgentStageJob"/> (<see cref="SdlcAgentStageCall"/>).
/// </summary>
public sealed record AgentRunRequest
{
    public required Guid ProjectId { get; init; }

    public required string Agent { get; init; }

    public required JobExecution Job { get; init; }

    public IReadOnlyDictionary<string, string?> Inputs { get; init; } = new Dictionary<string, string?>();

    public IReadOnlyList<InputArtifactRef> InputArtifacts { get; init; } = [];

    public Guid? StepId { get; init; }

    public string? UserFeedback { get; init; }

    public string? RunKey { get; init; }

    /// <summary>Output token limit per call; default: the run's provider and model limit (FR-160, <see cref="ILlmOutputLimits"/>).</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// FR-71: the repository the run's read-only repo tools see (<see cref="RepoScope.None"/>: no tools). Used by the
    /// Repository Analyst, refinement, spec and plan (base commit) and the code generator and review (working tree).
    /// </summary>
    public RepoScope Repo { get; init; }

    /// <summary>Warnings known before the run (e.g. <c>summary_wait_timeout</c>), recorded with the run's own (FR-70, FR-71).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// FR-121: the plan step's agent (a code generator run). With a composition that has it, the run is that specialist (its
    /// prompt is <see cref="Domain.Stack.StackPrompt.Compose"/> of the base template); <c>agent_key</c> records it, and a
    /// <c>generalist</c> step of a composed project (an optimisation plan) records <c>generalist</c>.
    /// </summary>
    public string? SpecialistAgentKey { get; init; }

    /// <summary>§4.23: the 0.2.0 agent key the step was planned with (its <c>legacy_agent_key</c>), recorded in <c>RUN_STARTED</c>.</summary>
    public string? LegacyAgentKey { get; init; }

    /// <summary>
    /// FR-120, FR-121: the version's resolved composition (resolved once by the job, <see cref="Features.Stack.VersionStackLoader"/>).
    /// The run records its catalog version and the packs it loaded (<c>[]</c> for agents that load no pack skills); a
    /// specialist step (<see cref="SpecialistAgentKey"/> in play) gets <see cref="Domain.Stack.StackPrompt.Compose"/> of the base template.
    /// </summary>
    public Domain.Stack.StackResolution? Stack { get; init; }

    /// <summary>FR-172: the size of the rendered <c>source_items</c> the prompt carries; over the budget of the run's model the run fails with <c>SOURCE_ITEMS_TOO_LARGE</c>, without calling the provider.</summary>
    public int? SourceItemsBytes { get; init; }
}

/// <summary>A succeeded run: its id, the provider's text, what it cost and when it completed.</summary>
public sealed record AgentRunOutcome(Guid RunId, string Text, string StopReason, LlmProvider Provider, string Model, string PromptVersion, int InputTokens, int OutputTokens, DateTimeOffset CompletedAt)
{
    /// <summary>The warnings recorded on the run (FR-70, FR-71).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// What a run stores in <c>agent_runs.output</c>: the provider's text, why it stopped, and the run's warnings (FR-70,
/// FR-71; omitted when none). FR-160: <see cref="MaxOutputTokens"/> is the output limit its calls used; FR-162:
/// <see cref="Continuations"/> is how many continuations its answer took (0–5). Both are <c>null</c> for runs stored before
/// 0.4.0 (no migration: they live in the output JSON).
/// </summary>
public sealed record RunOutput(string Text, string StopReason)
{
    public IReadOnlyList<string>? Warnings { get; init; }

    public int? MaxOutputTokens { get; init; }

    public int? Continuations { get; init; }
}
