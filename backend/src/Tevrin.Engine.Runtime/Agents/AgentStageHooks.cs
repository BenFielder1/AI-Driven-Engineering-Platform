using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-295, FR-296: host behaviour around an agent stage that the engine doesn't own: audit events, and the FR-26 token
/// budget and <c>tokens_used</c> accounting (the SDLC keeps them on its project). Every method runs inside the runner's
/// unit of work: changes a hook makes to the shared context are saved with the run's own changes. The default does
/// nothing (<see cref="NoAgentStageHooks"/>); a host registers its own before <c>AddAgentStages</c>.
/// </summary>
public interface IAgentStageHooks
{
    /// <summary>
    /// FR-26: before the model is called. Return a message to refuse the run (it fails the job with
    /// <c>BUDGET_EXCEEDED</c>, without retry), or <c>null</c> to go ahead.
    /// </summary>
    Task<string?> CheckBudgetAsync(PipelineRun run, string agentKey, CancellationToken cancellationToken);

    /// <summary>FR-26: an agent run used <paramref name="tokens"/> tokens (called for successful and failed calls alike).</summary>
    Task OnTokensUsedAsync(PipelineRun run, AgentRun agentRun, long tokens, CancellationToken cancellationToken);

    /// <summary>A new artifact version was stored (draft or auto-locked).</summary>
    Task OnArtifactCreatedAsync(PipelineRun run, Artifact artifact, CancellationToken cancellationToken);

    /// <summary>An open draft was superseded by version <paramref name="byVersion"/>.</summary>
    Task OnArtifactSupersededAsync(PipelineRun run, Artifact superseded, int byVersion, CancellationToken cancellationToken);

    /// <summary>A gate was raised on the run, with why (for an escalation, the validation findings).</summary>
    Task OnGateCreatedAsync(PipelineRun run, ApprovalGate gate, string reason, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IAgentStageHooks"/>: no budget, no audit events.</summary>
public sealed class NoAgentStageHooks : IAgentStageHooks
{
    public Task<string?> CheckBudgetAsync(PipelineRun run, string agentKey, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task OnTokensUsedAsync(PipelineRun run, AgentRun agentRun, long tokens, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnArtifactCreatedAsync(PipelineRun run, Artifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnArtifactSupersededAsync(PipelineRun run, Artifact superseded, int byVersion, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnGateCreatedAsync(PipelineRun run, ApprovalGate gate, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
