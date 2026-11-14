using Aide.Api.Features.Projects;
using Aide.Domain.Artifacts;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Domain.Projects;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>Reads shared by the execution endpoints, the execution job and its continuation (FR-31, FR-37).</summary>
public static class ExecutionQueries
{
    /// <summary>
    /// The current plan: the most recently created locked plan (the implementation plan, or the optimisation plan of a
    /// review cycle) of the project's current release version (§4.10), as the lifecycle snapshot defines it.
    /// </summary>
    public static async Task<Guid?> CurrentPlanIdAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        return await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Status == ArtifactStatus.Locked
                && (a.Type == ArtifactType.ImplementationPlan || a.Type == ArtifactType.OptimizationPlan))
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The current plan's steps in step order (invalidated ones excluded); tracked when <paramref name="track"/>.</summary>
    public static async Task<List<ExecutionStep>> CurrentStepsAsync(AideDbContext db, Guid planId, bool track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.ExecutionSteps.Where(s => s.PlanArtifactId == planId && s.Status != StepStatus.Invalidated);
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(s => s.StepNumber).ToListAsync(cancellationToken);
    }

    /// <summary>FR-259: the attempt of each build check the given build-fix steps were planned from, by check id.</summary>
    public static async Task<IReadOnlyDictionary<Guid, int>> BuildCheckAttemptsAsync(AideDbContext db, IEnumerable<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(steps);
        var ids = steps.Where(s => s.BuildCheckId is not null).Select(s => s.BuildCheckId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.BuildChecks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Attempt, cancellationToken);
    }

    /// <summary>An execution job of the project is queued or running (§4.9: at most one).</summary>
    public static Task<bool> JobActiveAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Jobs.AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.Execution && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken);
    }

    public static StepState ToState(ExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new StepState(step.StepNumber, step.Status, step.Dependencies, [.. step.DeclaredFiles.Select(f => f.Path)]);
    }

    /// <summary>The runner's state for <paramref name="project"/> (FR-31, FR-37), over the current plan's steps.</summary>
    public static async Task<ExecutionStateDto> StateAsync(AideDbContext db, Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var planId = await CurrentPlanIdAsync(db, project.Id, cancellationToken);
        var steps = planId is { } id ? await CurrentStepsAsync(db, id, track: false, cancellationToken) : [];
        var jobActive = await JobActiveAsync(db, project.Id, cancellationToken);
        var statuses = steps.Select(s => s.Status).ToList();
        int Count(StepStatus status) => statuses.Count(s => s == status);
        return new ExecutionStateDto(
            ExecutionRules.Status(statuses, project.ExecutionPaused, jobActive),
            project.ExecutionPaused,
            steps.FirstOrDefault(s => s.Status == StepStatus.InProgress)?.StepNumber,
            new ExecutionCountsDto(statuses.Count, Count(StepStatus.Pending), Count(StepStatus.InProgress), Count(StepStatus.Completed), Count(StepStatus.Rejected), Count(StepStatus.Blocked)));
    }

    /// <summary>
    /// A step commit's web link: for GitHub, <c>https://github.com/owner/repo/commit/&lt;sha&gt;</c>; for Local (no
    /// remote) <c>null</c>. Built server-side because the repository URL is Owner-only settings.
    /// </summary>
    public static string? CommitUrl(GitProvider provider, string? repoUrl, string? sha)
    {
        if (!ProjectSettingsRules.IsGitHub(provider) || string.IsNullOrEmpty(sha) || !ProjectSettingsRules.TryParseGitHubRepo(repoUrl, out var repo))
        {
            return null;
        }

        return $"https://github.com/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/commit/{Uri.EscapeDataString(sha)}";
    }

    /// <summary>
    /// FR-232, §4.35 rule 2: the branch of the project's most recent execution step with a recorded branch (by completion,
    /// else start time, steps with neither last; then id), or <c>null</c>.
    /// </summary>
    public static Task<string?> LatestStepBranchAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.BranchName != null)
            .OrderBy(s => (s.CompletedAt ?? s.StartedAt) == null)
            .ThenByDescending(s => s.CompletedAt ?? s.StartedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.BranchName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// FR-233, §4.36: the branch prefixes the project's branch control must block: <c>tevrin/</c>, plus <c>aide/</c> when any
    /// recorded release-version or execution-step branch of the project starts with <c>aide/</c>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> BranchControlPrefixesAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var legacy = BranchNaming.LegacyPrefix;
        var usesLegacy = await db.ReleaseVersions.AsNoTracking().AnyAsync(v => v.ProjectId == projectId && v.BranchName != null && v.BranchName.StartsWith(legacy), cancellationToken)
            || await db.ExecutionSteps.AsNoTracking().AnyAsync(s => s.ProjectId == projectId && s.BranchName != null && s.BranchName.StartsWith(legacy), cancellationToken);
        return BranchControlRules.RequiredPrefixes(usesLegacy);
    }
}
