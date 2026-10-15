using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// FR-16, §4.3: adds an agent-authored draft version (<c>agent_run_id</c> set, no user) to the current unit of work:
/// version = latest + 1 with the latest as parent, any open draft of the type superseded, and <c>ARTIFACT_CREATED</c>
/// (author <c>agent</c>). Nothing is saved here: the caller saves it after the run's completion was committed.
/// <para>
/// The project row is loaded tracked and its <c>updated_at</c> bumped, so the save carries the project's <c>xmin</c>: a
/// rollback, phase fallback or any other project change committed between the <see cref="ArtifactRules.CanCreateDraft"/>
/// check and the save makes it fail with a concurrency conflict instead of leaving a draft in a phase that no longer
/// allows it. Concurrent writers of the same type collide on the same <c>xmin</c>, the unique version or the superseded
/// draft's <c>xmin</c>. The job retries transiently; the retry re-uses the succeeded run and re-checks the phase.
/// </para>
/// </summary>
public sealed class AgentDraftWriter(AideDbContext db, TimeProvider time)
{
    /// <summary>
    /// The new draft, the one this run already created (idempotent), or <c>null</c> when the type can't be drafted in the
    /// project's current phase any more (<see cref="ArtifactRules.CanCreateDraft"/>) or the project is gone.
    /// </summary>
    public async Task<Artifact?> AddAsync(Guid projectId, ArtifactType type, string content, Guid agentRunId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var existingForRun = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.AgentRunId == agentRunId && a.Type == type, cancellationToken);
        if (existingForRun is not null)
        {
            return existingForRun;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || ArtifactRules.CanCreateDraft(type, project.CurrentPhase, project.Status).IsFailure)
        {
            return null;
        }

        project.UpdatedAt = time.GetUtcNow(); // xmin: the phase checked above must still hold when the draft commits

        var existing = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);
        var plan = ArtifactRules.NewDraft(existing);
        if (plan.SupersededVersions.Count > 0)
        {
            // §4.10, D-V2-3: only open drafts of the current release version are superseded; a closed version's drafts are
            // history. Version numbers stay project-wide.
            var drafts = await db.Artifacts
                .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == project.CurrentReleaseVersionId && a.Type == type && plan.SupersededVersions.Contains(a.Version))
                .ToListAsync(cancellationToken);
            foreach (var draft in drafts.Where(d => ArtifactRules.CanTransition(d.Status, ArtifactStatus.Superseded)))
            {
                draft.Status = ArtifactStatus.Superseded;
                db.AddEvent(
                    EventTypes.ArtifactSuperseded,
                    projectId,
                    new { artifactId = draft.Id, type, version = draft.Version, supersededByVersion = plan.Version },
                    SystemActor.Name,
                    now);
            }
        }

        var artifact = new Artifact
        {
            ProjectId = projectId,
            Type = type,
            Version = plan.Version,
            ParentVersion = plan.ParentVersion,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Draft,
            CheckpointAtCreation = project.Checkpoint,
            ReleaseVersionId = project.CurrentReleaseVersionId ?? Guid.Empty,
            AgentRunId = agentRunId,
            CreatedAt = now,
        };
        db.Artifacts.Add(artifact);
        db.AddEvent(
            EventTypes.ArtifactCreated,
            projectId,
            new { artifactId = artifact.Id, type, version = artifact.Version, parentVersion = artifact.ParentVersion, author = "agent", agentRunId },
            SystemActor.Name,
            now);
        return artifact;
    }
}
