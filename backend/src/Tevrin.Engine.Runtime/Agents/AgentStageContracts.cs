using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-295: one run of an agent stage. <paramref name="Trigger"/> is a <see cref="JobTriggers"/> value;
/// <paramref name="Feedback"/> (a rejection comment, or validation findings on a retry) is given to the prompt;
/// <paramref name="Attempt"/> is 1-based and counts validation retries (§4.55).
/// </summary>
public sealed record AgentStageRequest(Guid RunId, string StageId, string Trigger, string? Feedback = null, int Attempt = 1)
{
    /// <summary>
    /// FR-310: the agent run's id from the caller (e.g. derived from its job), or <c>null</c> for a new id. A run already
    /// stored under it is continued: a succeeded one returns its stored output without a model call, a running one (a lost
    /// lease) is called again, and a failed or cancelled one fails the job with its stored error code.
    /// </summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>FR-314: extra prompt values (placeholder name → value) on top of the inputs and context, e.g. a retry's previous output.</summary>
    public IReadOnlyDictionary<string, string?>? PromptValues { get; init; }

    /// <summary>
    /// FR-315: the 1-based delivery attempt of the job running this stage, or <c>null</c> when the caller doesn't say. The
    /// default <see cref="AgentStageExtension.OnCallFailedAsync"/> uses it to fail a transient failure on the queue's last
    /// attempt (<see cref="Domain.Agents.AgentRunRules.OnProviderFailure"/>); without it, the run stays <c>running</c> and
    /// the host must fail it (e.g. when the job dead-letters) or override the hook.
    /// </summary>
    public int? JobAttempt { get; init; }

    /// <summary>FR-314: whether feedback goes into a template without <c>{{FEEDBACK}}</c> under the engine's heading (default <c>true</c>).</summary>
    public bool AppendFeedback { get; init; } = true;

    /// <summary>
    /// D-0.7.0-42: the agent to run on <see cref="StageId"/>: the stage's primary <c>agent.key</c> or one of its
    /// <c>agents:</c> bindings, whose prompt, inputs, output, tools and job type the attempt then uses (with no
    /// <c>validate:</c> guards or <c>on_invalid</c>, which belong to the stage's own output). <c>null</c>: the primary
    /// agent. A key the stage doesn't bind fails the job with <c>VALIDATION</c>. Retries keep it.
    /// </summary>
    public string? AgentKey { get; init; }

    /// <summary>
    /// FR-345: the fan-out branch this attempt runs (its variant label: the agent key, or <c>#i</c>), or <c>null</c>. The
    /// output is stored as the stage's own artifact with this variant, context providers are called with it as
    /// <see cref="AgentContextRequest.Branch"/>, and the rendered prompt must hold no sibling's output (§4.65,
    /// <c>FANOUT_ISOLATION_BROKEN</c>).
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>
    /// FR-355: the 0-based map item this attempt works on, or <c>null</c>. <see cref="AgentKey"/> is then the map's agent:
    /// it reads the stage's declared inputs (its element comes in <see cref="PromptValues"/>), its output is stored as the
    /// stage's own artifact with this item index (one collection), its agent run records the index, and the rendered
    /// prompt must hold no other item's output (§4.65).
    /// </summary>
    public int? ItemIndex { get; init; }

    /// <summary>FR-355: when the current map entry's items were opened; the isolation check compares only outputs stored
    /// since (an earlier entry's outputs aren't siblings). Set with <see cref="ItemIndex"/>.</summary>
    public DateTimeOffset? ItemsSince { get; init; }

    /// <summary>FR-355: this attempt is the map stage's <c>reduce</c> agent (<see cref="AgentKey"/>): it reads the stage's
    /// inputs and stores the stage's own output (the item outputs come in <see cref="PromptValues"/>).</summary>
    public bool Reduce { get; init; }

    /// <summary>Whatever the host passes to its own <see cref="AgentStageExtension"/> (the engine never reads it).</summary>
    public object? HostState { get; init; }
}

/// <summary>
/// FR-295: one agent run's result. <see cref="Valid"/>: the output passed validation and is stored as
/// <see cref="Artifact"/>; otherwise <see cref="Findings"/> says why (§4.55). <see cref="Prompt"/> is the rendered user
/// message that was sent; <see cref="Run"/> and <see cref="Stage"/> are what it ran for.
/// </summary>
public sealed record AgentStageOutcome(
    bool Valid,
    AgentRun AgentRun,
    Artifact? Artifact,
    string? Findings,
    string Prompt,
    PipelineRun Run,
    StageDefinition Stage)
{
    /// <summary>FR-314: when this attempt ended the stage with <c>then: fail</c>, the job's failure.</summary>
    public AgentStageFailure? Failure { get; init; }

    /// <summary>FR-310: the run had already succeeded; its stored output was returned without a model call.</summary>
    public bool Redelivered { get; init; }
}

/// <summary>How a stage's validate/retry loop ended (FR-296).</summary>
public enum AgentStageEnd
{
    /// <summary>An attempt's output was valid and stored.</summary>
    Stored = 0,

    /// <summary>Attempts ran out with <c>then: escalate</c>: a pending <c>escalation</c> gate was raised.</summary>
    Escalated = 1,

    /// <summary>Attempts ran out with <c>then: fail</c> (or no <c>on_invalid</c>): the last agent run is failed.</summary>
    Failed = 2,
}

/// <summary>
/// FR-296: every attempt of a stage, in order, and how it ended. <see cref="Gate"/> is the escalation gate when
/// <see cref="End"/> is <see cref="AgentStageEnd.Escalated"/>.
/// </summary>
public sealed record AgentStageResult(AgentStageEnd End, IReadOnlyList<AgentStageOutcome> Attempts, ApprovalGate? Gate)
{
    public AgentStageOutcome Last => Attempts[^1];

    /// <summary>The findings of the last attempt, when it was invalid.</summary>
    public string? Findings => Last.Findings;

    /// <summary>For the job layer: a <see cref="AgentStageEnd.Failed"/> stage fails its job at once (no retry), with the
    /// extension's failure (FR-314; default <c>VALIDATION</c> and the findings).</summary>
    public void ThrowIfFailed()
    {
        if (End == AgentStageEnd.Failed && Last.Failure is { } failure)
        {
            throw new JobFailedException(failure.Message, failure.Transient, failure.ErrorCode);
        }

        if (End == AgentStageEnd.Failed)
        {
            throw new JobFailedException(
                $"The {Last.Stage.Id} output failed validation after {Attempts.Count} attempt(s): {Findings}",
                transient: false,
                EngineErrorCodes.Validation);
        }
    }
}

/// <summary>
/// FR-295: where an agent stage's prompt template comes from: by default the prompts the defining pack registered
/// (<see cref="Domain.Pipelines.IPackRegistry.AddPrompt"/>). A host may register its own source instead.
/// </summary>
public interface IPromptSource
{
    /// <summary>The template text of prompt <paramref name="name"/> for pipeline <paramref name="pipelineId"/> (defined by
    /// pack <paramref name="packId"/>), or <c>null</c>.</summary>
    string? Find(string packId, string pipelineId, string name);
}

/// <summary>The prompts the packs registered (<see cref="PackRegistration.Prompts"/>).</summary>
public sealed class PackPromptSource(PipelineRegistry registry) : IPromptSource
{
    public string? Find(string packId, string pipelineId, string name) =>
        registry.Packs.FirstOrDefault(p => p.PackId == packId)?.Prompts.GetValueOrDefault(name);
}
