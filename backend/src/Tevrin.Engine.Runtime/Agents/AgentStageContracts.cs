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
public sealed record AgentStageRequest(Guid RunId, string StageId, string Trigger, string? Feedback = null, int Attempt = 1);

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
    StageDefinition Stage);

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

    /// <summary>For the job layer: a <see cref="AgentStageEnd.Failed"/> stage fails its job at once (no retry), with
    /// <c>VALIDATION</c> and the findings.</summary>
    public void ThrowIfFailed()
    {
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
