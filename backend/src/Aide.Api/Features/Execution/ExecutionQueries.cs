using Aide.Api.Features.Projects;
using Aide.Domain.Artifacts;
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
    /// review cycle), as the lifecycle snapshot defines it.
    /// </summary>
    public static Task<Guid?> CurrentPlanIdAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Status == ArtifactStatus.Locked
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
        if (provider != GitProvider.Github || string.IsNullOrEmpty(sha) || !ProjectSettingsRules.TryParseGitHubRepo(repoUrl, out var repo))
        {
            return null;
        }

        return $"https://github.com/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/commit/{Uri.EscapeDataString(sha)}";
    }
}
