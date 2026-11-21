using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;

namespace Aide.Api.Agents;

/// <summary>
/// FR-26, FR-311: the SDLC's <see cref="IAgentStageHooks"/> (scoped, one agent stage at a time). The budget check is
/// 0.6.1's soft cap at a run's start (<see cref="TokenBudget.CheckCanStart"/>); the tokens a call used are held until
/// <see cref="SdlcAgentExtension"/> commits them with the run in one transaction (0.6.1's order). Artifact events are
/// written by the SDLC draft writer, so the artifact hooks do nothing.
/// </summary>
public sealed class SdlcAgentStageHooks(ISdlcAgentRunData data) : IAgentStageHooks
{
    /// <summary>The project's budget and use as the last start check read them (for its <c>BUDGET_EXCEEDED</c> event).</summary>
    public (long? TokenBudget, long TokensUsed) StartBudget { get; private set; }

    /// <summary>Tokens used by calls since the last commit (<see cref="TakePendingTokens"/>).</summary>
    public long PendingTokens { get; private set; }

    public async Task<string?> CheckBudgetAsync(PipelineRun run, string agentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (await data.ProjectAsync(run.ProjectId, cancellationToken) is not { } project)
        {
            return null;
        }

        StartBudget = (project.TokenBudget, project.TokensUsed);
        var budget = TokenBudget.CheckCanStart(project.TokenBudget, project.TokensUsed);
        return budget.IsFailure ? budget.Error!.Message : null;
    }

    public Task OnTokensUsedAsync(PipelineRun run, AgentRun agentRun, long tokens, CancellationToken cancellationToken)
    {
        PendingTokens += tokens;
        return Task.CompletedTask;
    }

    /// <summary>The pending tokens, reset to zero.</summary>
    public long TakePendingTokens()
    {
        var tokens = PendingTokens;
        PendingTokens = 0;
        return tokens;
    }

    public Task OnArtifactCreatedAsync(PipelineRun run, Artifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnArtifactSupersededAsync(PipelineRun run, Artifact superseded, int byVersion, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnGateCreatedAsync(PipelineRun run, ApprovalGate gate, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
