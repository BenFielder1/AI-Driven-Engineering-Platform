using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-351: the agent feedback store's human side. People list and add entries for an agent key of a pipeline in a
/// project, and soft-delete them (a deleted entry keeps its row and is never injected again, §4.75).
/// </summary>
public sealed class AgentFeedbackEntries(IEngineDbContext db, PipelineRegistry registry, TimeProvider time)
{
    /// <summary>The most entries a list returns.</summary>
    public const int MaxListed = 200;

    /// <summary>The project's not-deleted entries for (pipeline, agent key), newest first; <c>NOT_FOUND</c> for an unknown
    /// pipeline or an agent key the pipeline doesn't declare.</summary>
    public async Task<Result<IReadOnlyList<AgentFeedback>>> ListAsync(Guid projectId, string pipelineId, string agentKey, CancellationToken cancellationToken)
    {
        if (Known(pipelineId, agentKey) is { } missing)
        {
            return Result.Failure<IReadOnlyList<AgentFeedback>>(missing);
        }

        var rows = await db.AgentFeedback.AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.PipelineId == pipelineId && f.AgentKey == agentKey && f.DeletedAt == null)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AgentFeedback>>(rows);
    }

    /// <summary>A <c>human</c> entry by <paramref name="userId"/>; <c>VALIDATION</c> for blank or over-long text.</summary>
    public async Task<Result<AgentFeedback>> CreateAsync(
        Guid projectId, string pipelineId, string agentKey, string? text, Guid? userId, CancellationToken cancellationToken)
    {
        if (Known(pipelineId, agentKey) is { } missing)
        {
            return Result.Failure<AgentFeedback>(missing);
        }

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > AgentFeedback.TextMaxLength)
        {
            return Result.Failure<AgentFeedback>(new DomainError(
                EngineErrorCodes.Validation, $"text is required and at most {AgentFeedback.TextMaxLength} characters.")
            { Field = "text" });
        }

        var entry = new AgentFeedback
        {
            PipelineId = pipelineId,
            AgentKey = agentKey,
            ProjectId = projectId,
            Source = AgentFeedbackSource.Human,
            Text = trimmed,
            CreatedBy = userId,
            CreatedAt = time.GetUtcNow(),
        };
        db.AgentFeedback.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(entry);
    }

    /// <summary>The project's not-deleted entry <paramref name="feedbackId"/>, tracked, or <c>null</c>.</summary>
    public Task<AgentFeedback?> FindAsync(Guid projectId, Guid feedbackId, CancellationToken cancellationToken) =>
        db.AgentFeedback.FirstOrDefaultAsync(f => f.Id == feedbackId && f.ProjectId == projectId && f.DeletedAt == null, cancellationToken);

    /// <summary>Soft-deletes <paramref name="entry"/> (<see cref="AgentFeedback.DeletedAt"/>).</summary>
    public async Task DeleteAsync(AgentFeedback entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private DomainError? Known(string pipelineId, string agentKey) =>
        registry.TryGet(pipelineId, out var loaded) && loaded.Definition is { } definition && FeedbackInjectionRules.DeclaresAgent(definition, agentKey)
            ? null
            : new DomainError(EngineErrorCodes.NotFound, $"Pipeline '{pipelineId}' has no agent '{agentKey}'.");
}
