using System.Linq.Expressions;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// FR-297: reads of <c>agent_runs</c> that don't need the recorded input (up to 1 MB per row). Use with
/// <c>Select</c> on an untracked query; to update the row, attach the result (only the properties then changed are written,
/// so the stored input is kept).
/// </summary>
public static class AgentRunProjections
{
    /// <summary>Every column except <see cref="AgentRun.Input"/> (left null).</summary>
    public static Expression<Func<AgentRun, AgentRun>> WithoutInput { get; } = r => new AgentRun
    {
        Id = r.Id,
        ProjectId = r.ProjectId,
        ReleaseVersionId = r.ReleaseVersionId,
        PipelineRunId = r.PipelineRunId,
        Agent = r.Agent,
        AgentKey = r.AgentKey,
        PresetId = r.PresetId,
        PresetVersion = r.PresetVersion,
        Packs = r.Packs,
        CatalogVersion = r.CatalogVersion,
        CredentialId = r.CredentialId,
        Stage = r.Stage,
        StepId = r.StepId,
        Provider = r.Provider,
        Model = r.Model,
        PromptVersion = r.PromptVersion,
        InputArtifacts = r.InputArtifacts,
        UserFeedback = r.UserFeedback,
        Output = r.Output,
        ToolCalls = r.ToolCalls,
        Status = r.Status,
        Attempts = r.Attempts,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CostEstimate = r.CostEstimate,
        DurationMs = r.DurationMs,
        InputTruncated = r.InputTruncated,
        LatencyMs = r.LatencyMs,
        Error = r.Error,
        CreatedAt = r.CreatedAt,
        CompletedAt = r.CompletedAt,
    };

    /// <summary>The tracked instance of run <paramref name="loaded"/> if the unit of work already has one, else
    /// <paramref name="loaded"/> attached unchanged.</summary>
    public static AgentRun Track(IEngineDbContext db, AgentRun loaded)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(loaded);
        if (db.AgentRuns.Local.FirstOrDefault(r => r.Id == loaded.Id) is { } tracked)
        {
            return tracked;
        }

        db.AgentRuns.Attach(loaded);
        return loaded;
    }
}
