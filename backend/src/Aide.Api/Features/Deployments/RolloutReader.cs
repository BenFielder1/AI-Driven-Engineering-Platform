using Aide.Api.Features.Databases;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>What <c>PREVIEW_ROLLOUT_COMPLETED</c> records about a finished preview rollout.</summary>
public sealed record RolloutCompletedFacts(Guid RolloutId, string State, string? ErrorCode, string? Message);

/// <summary>
/// FR-147, FR-149: rollouts as the Review and Release pages show them, built from the rollout job, its deployment rows
/// (<c>rollout_id</c>), its migration run and (preview) its <c>PREVIEW_ROLLOUT_COMPLETED</c> event.
/// </summary>
public sealed class RolloutReader(AideDbContext db, MigrationRunService migrations, RolloutScheduler rollouts, IWorkingCopies workingCopies)
{
    public const string PreviewUsesProductionNote = "Preview uses the production database without this version's migrations.";

    /// <summary>FR-147, FR-148: the preview environment: the newest preview rollout, notes, and whether the preview database diverged.</summary>
    public async Task<PreviewEnvironmentDto> PreviewAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var job = await rollouts.LatestPreviewAsync(projectId, cancellationToken);
        var current = job is null ? null : await BuildAsync(job, null, cancellationToken);
        List<string> notes = [];
        var databases = await db.ManagedDatabases.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Status != ManagedDatabaseStatus.Removed)
            .Select(d => d.Environment)
            .ToListAsync(cancellationToken);
        var backendHost = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Component == DeploymentComponent.Backend)
            .Select(t => (DeploymentProvider?)t.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        if (databases.Contains(DeploymentEnvironment.Production) && !databases.Contains(DeploymentEnvironment.Preview)
            && backendHost is not (DeploymentProvider.Render or DeploymentProvider.Railway))
        {
            notes.Add(PreviewUsesProductionNote);
        }

        notes.AddRange(current?.Steps.Where(s => s.Note is not null).Select(s => s.Note!) ?? []);
        var missing = await DivergedAsync(projectId, cancellationToken);
        return new PreviewEnvironmentDto(current, notes, missing.Count > 0, missing.Count > 0 ? PreviewDatabaseRules.Describe(missing) : null);
    }

    /// <summary>FR-148: the preview database's divergence from the version branch head (empty when none or unknown).</summary>
    public async Task<IReadOnlyList<string>> DivergedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var version = await db.CurrentReleaseVersionAsync(projectId, cancellationToken);
        if (version?.BranchName is not { } branch)
        {
            return [];
        }

        string? head;
        try
        {
            head = await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            head = null;
        }

        return head is null ? [] : await rollouts.DivergedAsync(projectId, head, cancellationToken);
    }

    /// <summary>FR-149: each release's production rollout (<c>null</c> when none was queued).</summary>
    public async Task<IReadOnlyDictionary<Guid, RolloutDto>> ForReleasesAsync(Guid projectId, IReadOnlyList<Domain.Entities.Release> releases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var result = new Dictionary<Guid, RolloutDto>();
        if (releases.Count == 0)
        {
            return result;
        }

        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.ProductionRollout)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var release in releases)
        {
            if (jobs.FirstOrDefault(j => RolloutScheduler.TryPayload(j.Payload)?.ReleaseId == release.Id) is { } job)
            {
                result[release.Id] = await BuildAsync(job, release, cancellationToken);
            }
        }

        return result;
    }

    /// <summary>The rollout of <paramref name="job"/> (a <c>preview_rollout</c> or <c>production_rollout</c> job).</summary>
    public Task<RolloutDto> ForJobAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return BuildAsync(job, null, cancellationToken);
    }

    /// <summary>FR-149: where the rollout is live: its ready deployment steps (backend first).</summary>
    public static IReadOnlyList<LiveTargetDto> LiveOn(RolloutDto? rollout) =>
        rollout is null
            ? []
            : [.. rollout.Steps
                .Where(s => s.Step != RolloutStepKind.Migrations && s.Status == DeploymentStatus.Ready && s.Provider is not null)
                .Select(s => new LiveTargetDto(RolloutRules.ComponentOf(s.Step), s.Provider!.Value, DeploymentPlatforms.DisplayName(s.Provider.Value), s.Url))];

    private async Task<RolloutDto> BuildAsync(Job job, Domain.Entities.Release? release, CancellationToken cancellationToken)
    {
        var payload = RolloutScheduler.TryPayload(job.Payload)
            ?? throw new InvalidOperationException("The rollout job payload can't be read.");
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == payload.ProjectId).ToListAsync(cancellationToken);
        var plan = RolloutRules.Plan(
            payload.Environment,
            await migrations.HasMigrationStepAsync(payload.ProjectId, payload.Environment, cancellationToken) || release?.MigrationRunId is not null,
            [.. targets.Select(t => new RolloutTarget(t.Component, t.Provider, t.ResourceId is not null && (t.ConnectionId is not null || t.Provider == DeploymentProvider.GithubPages)))]);
        var rows = await db.Deployments.AsNoTracking()
            .Where(d => d.RolloutId == job.Id || (payload.ReleaseId != null && d.ReleaseId == payload.ReleaseId && d.Environment == DeploymentEnvironment.Production
                && d.Kind == DeploymentKind.Deploy))
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
        // The rollout's own run (production: the post-merge run on the merge commit, not the release's pre-merge run).
        var run = await db.DatabaseMigrationRuns.AsNoTracking()
            .Where(r => r.ProjectId == payload.ProjectId && r.Environment == payload.Environment && r.ReleaseId == payload.ReleaseId
                && r.GitSha == payload.GitSha && r.StartedAt >= payload.RequestedAt)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var steps = plan.Select(step => Step(step, rows, run, targets)).ToList();
        var (state, code, message) = await StateAsync(job, payload, steps, cancellationToken);
        return new RolloutDto(job.Id, payload.Environment, state, payload.ReleaseVersionId, payload.ReleaseId, payload.GitRef, payload.GitSha, code, message, steps, payload.RequestedAt);
    }

    private static RolloutStepDto Step(RolloutStepPlan step, IReadOnlyList<Deployment> rows, DatabaseMigrationRun? run, IReadOnlyList<DeploymentTarget> targets)
    {
        if (step.Kind == RolloutStepKind.Migrations)
        {
            var state = run is null ? RolloutStepState.Pending : RolloutRules.StateOf(run.Status);
            return new RolloutStepDto(step.Kind, state, null, null, null, null, null, null, run?.ErrorCode, run?.ErrorMessage, run?.Id, null, false);
        }

        var component = RolloutRules.ComponentOf(step.Kind);
        var host = targets.FirstOrDefault(t => t.Component == component)?.Provider;
        if (step.Skipped)
        {
            return new RolloutStepDto(step.Kind, RolloutStepState.Skipped, host, null, null, null, null, null, null, null, null, step.Note, false);
        }

        var row = rows.FirstOrDefault(r => r.Component == component);
        return row is null
            ? new RolloutStepDto(step.Kind, RolloutStepState.Pending, host, null, null, null, null, null, null, null, null, null, false)
            : new RolloutStepDto(
                step.Kind, RolloutRules.StateOf(row.Status), host, row.Id, row.Status, row.Url, row.InspectorUrl, row.BuildLogTail, row.ErrorCode, row.ErrorMessage, null, null,
                DeploymentStates.IsTerminal(row.Status));
    }

    private async Task<(RolloutState State, string? Code, string? Message)> StateAsync(Job job, RolloutJobPayload payload, IReadOnlyList<RolloutStepDto> steps, CancellationToken cancellationToken)
    {
        if (payload.Environment == DeploymentEnvironment.Preview && await CompletedAsync(payload.ProjectId, job.Id, cancellationToken) is { } facts
            && EnumStrings.TryParse<RolloutState>(facts.State, out var recorded))
        {
            return (recorded, facts.ErrorCode, facts.Message);
        }

        if (job.Status == JobStatus.Cancelled)
        {
            return (RolloutState.Canceled, null, null);
        }

        var failed = steps.FirstOrDefault(s => s.State == RolloutStepState.Failed);
        if (failed is not null)
        {
            return (RolloutState.Failed, failed.ErrorCode, failed.ErrorMessage);
        }

        if (job.Status is JobStatus.Queued or JobStatus.Running)
        {
            return (RolloutState.Running, null, null);
        }

        return steps.All(s => s.State is RolloutStepState.Succeeded or RolloutStepState.Skipped)
            ? (RolloutState.Succeeded, null, null)
            : (job.Status == JobStatus.Succeeded && payload.Environment == DeploymentEnvironment.Preview ? RolloutState.Canceled : RolloutState.Failed, job.Status == JobStatus.Succeeded ? null : ErrorCodes.PlatformError, job.LastError);
    }

    private async Task<RolloutCompletedFacts?> CompletedAsync(Guid projectId, Guid rolloutId, CancellationToken cancellationToken)
    {
        var payloads = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.PreviewRolloutCompleted)
            .OrderByDescending(e => e.CreatedAt)
            .Take(20)
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        foreach (var payload in payloads)
        {
            try
            {
                if (AideJson.Deserialize<RolloutCompletedFacts>(payload) is { } facts && facts.RolloutId == rolloutId)
                {
                    return facts;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // An unreadable event says nothing about this rollout.
            }
        }

        return null;
    }
}
