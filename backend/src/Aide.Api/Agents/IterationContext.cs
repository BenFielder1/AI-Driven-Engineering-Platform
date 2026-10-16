using Aide.Api.Features.Versions;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Releases;
using Aide.Domain.Specs;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// What an agent job knows about the release version it works in (§4.10, §4.13, FR-70, FR-72): the version, whether it
/// has existing code (a base commit: a new version, an import, a scratch project on a repository with code), whether it
/// is an <b>iteration</b> (existing code or an earlier released/imported version), its <c>codebase_summary</c>, and the
/// previous released version's locked spec and project request (the <b>baseline inputs</b>, read-only).
/// </summary>
public sealed record IterationContext(
    ReleaseVersion? Version,
    bool HasExistingCode,
    bool IsIteration,
    Artifact? CodebaseSummary,
    Artifact? BaselineSpecification,
    Artifact? BaselineProjectRequest)
{
    public static IterationContext None { get; } = new(null, false, false, null, null, null);

    /// <summary>§4.13: the spec validation context of this version (preset sections arrive with FR-74).</summary>
    public SpecValidationContext SpecContext => SpecValidationContext.ForVersion(IsIteration, BaselineSpecification?.Content);

    /// <summary>The input artifacts these contribute to a run (summary and baselines that exist).</summary>
    public IEnumerable<Artifact> Artifacts => new[] { CodebaseSummary, BaselineProjectRequest, BaselineSpecification }.OfType<Artifact>();
}

/// <summary>Reads the <see cref="IterationContext"/> of a project's current release version, and its analyst's state.</summary>
public sealed class IterationContextReader(AideDbContext db, AgentJobInputs inputs)
{
    public async Task<IterationContext> ReadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        if (versionId is not { } id)
        {
            return IterationContext.None;
        }

        var versions = await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        var version = versions.FirstOrDefault(v => v.Id == id);
        if (version is null)
        {
            return IterationContext.None;
        }

        // §4.10: the baseline is the highest released (or imported) version other than this one.
        var baseline = versions
            .Where(v => v.Id != id && ReleaseVersionRules.IsReleased(v.Status))
            .OrderByDescending(v => SemVer.TryParse(v.Version, out var parsed) ? parsed : null)
            .ThenByDescending(v => v.CreatedAt)
            .FirstOrDefault();
        var hasCode = !string.IsNullOrWhiteSpace(version.BaseCommitSha);
        var summary = await inputs.CurrentAsync(projectId, ArtifactType.CodebaseSummary, cancellationToken);
        var baselineSpec = baseline is null ? null : await LockedAsync(projectId, baseline.Id, ArtifactType.TechnicalSpecification, cancellationToken);
        var baselineRequest = baseline is null ? null : await LockedAsync(projectId, baseline.Id, ArtifactType.ProjectRequest, cancellationToken);
        return new IterationContext(version, hasCode, hasCode || baseline is not null || version.BaseTag is not null, summary, baselineSpec, baselineRequest);
    }

    /// <summary>FR-70: where the version's latest <c>repository_analysis</c> job is.</summary>
    public async Task<AnalysisJobState> AnalysisStateAsync(Guid projectId, Guid releaseVersionId, CancellationToken cancellationToken) =>
        (await AnalysisJobAsync(projectId, releaseVersionId, cancellationToken)).State;

    /// <summary>FR-70: the version's latest <c>repository_analysis</c> job (id, or <c>null</c> when none) and its state.</summary>
    public async Task<(Guid? JobId, AnalysisJobState State)> AnalysisJobAsync(Guid projectId, Guid releaseVersionId, CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.RepositoryAnalysis)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new { j.Id, j.Status, j.Payload })
            .Take(20)
            .ToListAsync(cancellationToken);
        var job = jobs.FirstOrDefault(j => ReadVersionId(j.Payload) == releaseVersionId);
        var state = job?.Status switch
        {
            null => AnalysisJobState.None,
            JobStatus.Queued => AnalysisJobState.Queued,
            JobStatus.Running => AnalysisJobState.Running,
            JobStatus.Succeeded => AnalysisJobState.Succeeded,
            _ => AnalysisJobState.Failed,
        };
        return (job?.Id, state);
    }

    private async Task<Artifact?> LockedAsync(Guid projectId, Guid releaseVersionId, ArtifactType type, CancellationToken cancellationToken)
    {
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == type && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return await inputs.IntactAsync(artifact, cancellationToken);
    }

    private static Guid? ReadVersionId(string payload)
    {
        try
        {
            return AideJson.Deserialize<RepositoryAnalysisJobPayload>(payload).ReleaseVersionId;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
