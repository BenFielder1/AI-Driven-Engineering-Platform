using Aide.Api.Features.Artifacts;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Entities;

namespace Aide.Api.Agents;

/// <summary>
/// FR-311–FR-316: the database work of <see cref="SdlcAgentExtension"/> (a seam, so its rules are tested without a
/// database), done as 0.6.1's runner did it.
/// </summary>
public interface ISdlcAgentRunData
{
    /// <summary>The project, without tracking, or <c>null</c>.</summary>
    Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Adds an event by the system actor (saved with the next save).</summary>
    void AddEvent(string type, Guid projectId, object payload, DateTimeOffset at);

    /// <summary>
    /// FR-26, FR-315: in one transaction, adds <paramref name="tokens"/> to the project's <c>tokens_used</c> and runs
    /// <paramref name="save"/> (the run and its events).
    /// </summary>
    Task CommitWithTokensAsync(Guid projectId, long tokens, Func<CancellationToken, Task> save);

    /// <summary>FR-78: marks the run's bound connection invalid, with <c>CONNECTION_INVALID</c> (saved with the run).</summary>
    Task InvalidateCredentialAsync(AgentRun run, Guid credentialId);

    /// <summary>
    /// FR-27–FR-29: adds the agent draft through 0.6.1's <see cref="Features.Artifacts.AgentDraftWriter"/> (the phase check, the
    /// <c>xmin</c> touch, superseding, the events; the draft this run already created): <c>null</c> when the phase no longer
    /// allows it. <c>Added</c>: the draft is new in this unit of work. Saved with the next save.
    /// </summary>
    Task<(Artifact? Draft, bool Added)> AddDraftAsync(Guid projectId, string type, string content, Guid agentRunId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary><see cref="ISdlcAgentRunData"/> on the SDLC database (scoped: the engine store's unit of work).</summary>
public sealed class SdlcAgentRunData(AideDbContext db, AgentDraftWriter drafts, TimeProvider time, ILogger<SdlcAgentRunData> logger) : ISdlcAgentRunData
{
    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

    public void AddEvent(string type, Guid projectId, object payload, DateTimeOffset at) =>
        db.AddEvent(type, projectId, payload, SystemActor.Name, at);

    public async Task CommitWithTokensAsync(Guid projectId, long tokens, Func<CancellationToken, Task> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        // The provider was paid for: recorded even if the job is being cancelled (as 0.6.1).
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await db.Projects
            .Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokensUsed, p => p.TokensUsed + tokens), CancellationToken.None);

        await save(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    public Task InvalidateCredentialAsync(AgentRun run, Guid credentialId) =>
        SdlcAgentRuns.InvalidateCredentialAsync(db, time, logger, run, credentialId);

    public async Task<(Artifact? Draft, bool Added)> AddDraftAsync(Guid projectId, string type, string content, Guid agentRunId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var draft = await drafts.AddAsync(projectId, type, content, agentRunId, now, cancellationToken);
        return (draft, draft is not null && db.Entry(draft).State == EntityState.Added);
    }
}
