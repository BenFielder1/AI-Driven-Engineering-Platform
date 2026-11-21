using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>FR-317: what the SDLC context providers read (a seam, so their rules are tested without a database).</summary>
public interface ISdlcContextSource
{
    /// <summary>The project, or <c>null</c>.</summary>
    Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>The <see cref="IterationContext"/> of pipeline run <paramref name="runId"/>'s release version (its subject);
    /// <see cref="IterationContext.None"/> when the run has none.</summary>
    Task<IterationContext> IterationAsync(Guid runId, Guid projectId, CancellationToken cancellationToken);
}

/// <summary>FR-317: <see cref="ISdlcContextSource"/> on the database, through the SDLC job readers.</summary>
public sealed class SdlcContextSource(AideDbContext db, AgentJobInputs inputs, IterationContextReader iterations) : ISdlcContextSource
{
    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) => inputs.ProjectAsync(projectId, cancellationToken);

    public async Task<IterationContext> IterationAsync(Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        // The run's subject is the release version it works in (AgentStageScope.ReleaseVersionOf), as a 0.6.1 job's payload named it.
        var versionId = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.ProjectId == projectId)
            .Select(r => r.SubjectId)
            .FirstOrDefaultAsync(cancellationToken);
        return await iterations.ReadAsync(projectId, versionId, cancellationToken);
    }
}
