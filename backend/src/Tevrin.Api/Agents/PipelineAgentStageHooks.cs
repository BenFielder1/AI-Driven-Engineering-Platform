using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Access;
using Tevrin.Domain.Common;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Agents;

/// <summary>
/// FR-341, D-0.8.0-7: the host's <see cref="IAgentStageHooks"/> for every pipeline (scoped). SDLC runs go to
/// <see cref="SdlcAgentStageHooks"/> unchanged (its tokens are committed by <see cref="SdlcAgentExtension"/>, 0.6.1's
/// order, and its artifact events by the SDLC draft writer). Runs of every other pipeline get the same FR-26 project
/// budget (<see cref="TokenBudget.CheckCanStart"/>), their tokens added to the project's <c>tokens_used</c>, and audit
/// events (budget refusal, artifact version, gate). Event payloads carry ids, keys and counts only, never artifact
/// content or prompts (redaction).
/// </summary>
public sealed class PipelineAgentStageHooks(SdlcAgentStageHooks sdlc, IServiceProvider services, TimeProvider time) : IAgentStageHooks
{
    private static bool IsSdlc(PipelineRun run) => run.PipelineId == SdlcPack.PipelineId;

    // Resolved only for non-SDLC runs, so the SDLC path (and its offline unit harness) never touches the database here.
    private TevrinDbContext Db => services.GetRequiredService<TevrinDbContext>();

    public async Task<string?> CheckBudgetAsync(PipelineRun run, string agentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsSdlc(run))
        {
            return await sdlc.CheckBudgetAsync(run, agentKey, cancellationToken);
        }

        var projectId = run.ProjectId;
        var project = await Db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.TokenBudget, p.TokensUsed })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return null;
        }

        var budget = TokenBudget.CheckCanStart(project.TokenBudget, project.TokensUsed);
        if (!budget.IsFailure)
        {
            return null;
        }

        Db.AddEvent(
            EventTypes.BudgetExceeded,
            projectId,
            new { pipelineId = run.PipelineId, pipelineRunId = run.Id, stage = run.CurrentStage, agent = agentKey, tokenBudget = project.TokenBudget, tokensUsed = project.TokensUsed },
            SystemActor.Name,
            time.GetUtcNow());
        return budget.Error!.Message;
    }

    public async Task OnTokensUsedAsync(PipelineRun run, AgentRun agentRun, long tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsSdlc(run))
        {
            await sdlc.OnTokensUsedAsync(run, agentRun, tokens, cancellationToken);
            return;
        }

        if (tokens <= 0)
        {
            return;
        }

        // The provider was paid for: recorded even if the job is being cancelled (as the SDLC does).
        var projectId = run.ProjectId;
        await Db.Projects
            .Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokensUsed, p => p.TokensUsed + tokens), CancellationToken.None);
    }

    public Task OnArtifactCreatedAsync(PipelineRun run, Artifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(artifact);
        if (IsSdlc(run))
        {
            return sdlc.OnArtifactCreatedAsync(run, artifact, cancellationToken);
        }

        Db.AddEvent(
            EventTypes.ArtifactCreated,
            run.ProjectId,
            new { pipelineId = run.PipelineId, pipelineRunId = run.Id, artifactId = artifact.Id, type = artifact.Type, version = artifact.Version, status = artifact.Status.ToString() },
            SystemActor.Name,
            time.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task OnArtifactSupersededAsync(PipelineRun run, Artifact superseded, int byVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return IsSdlc(run) ? sdlc.OnArtifactSupersededAsync(run, superseded, byVersion, cancellationToken) : Task.CompletedTask;
    }

    public Task OnGateCreatedAsync(PipelineRun run, ApprovalGate gate, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(gate);
        if (IsSdlc(run))
        {
            return sdlc.OnGateCreatedAsync(run, gate, reason, cancellationToken);
        }

        Db.AddEvent(
            EventTypes.GateCreated,
            run.ProjectId,
            new { pipelineId = run.PipelineId, pipelineRunId = run.Id, gateId = gate.Id, type = gate.Type, stage = run.CurrentStage },
            SystemActor.Name,
            time.GetUtcNow());
        return Task.CompletedTask;
    }
}
