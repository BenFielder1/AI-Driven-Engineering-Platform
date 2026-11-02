using System.Text;
using Aide.Api.Features.Analysis;
using Aide.Api.Features.Versions;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// FR-180: the Stack Analyst. Runs for the release version in development when it infers its stack (<c>stack_source = infer</c>)
/// and the project is in <c>analysis</c>: queued when the version's Repository Analyst ends (<see cref="StackAnalysisTrigger"/>),
/// by <c>POST /analysis/run</c>, and on a rejected (FR-183) or corrupted (FR-20) profile. Its inputs are the locked
/// <c>codebase_summary</c> (missing: the run carries <c>summary_unavailable</c>), the detector's evidence at the base commit
/// (§4.18, also recorded as <c>STACK_DETECTED</c>), the profile the detector alone gives, the catalog's options, the version's
/// request, a rejected draft and its comment, and the read-only repo tools at the base commit (FR-71), through the
/// <see cref="AgentRunner"/> (rule 10).
/// <para>
/// The reply must be a valid profile naming only catalog options and citing files that exist at the base commit
/// (<see cref="StackProfile.CheckReply"/>); otherwise it is re-run once with the reasons and the allowed options, then the
/// run fails with <c>STACK_PROFILE_INVALID</c>. An accepted profile goes through the deterministic rules
/// (<see cref="StackProfile.Reconcile"/>: a disagreement with the detector is a <c>conflict</c>, the VCS is fixed) and is
/// stored as a <c>stack_profile</c> draft with <c>STACK_PROFILE_CREATED</c>. Redelivery is idempotent (<see cref="ArtifactAgentJob"/>).
/// </para>
/// </summary>
public sealed partial class StackAnalysisJobHandler(
    ArtifactAgentJob agentJob,
    AgentJobInputs inputs,
    AideDbContext db,
    StackProfileBaseReader bases,
    IStackCatalog catalogs,
    TimeProvider time,
    ILogger<StackAnalysisJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = AgentJobInputs.Payload(job);
        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        if (project is null || !AgentJobInputs.ShouldRun(payload, ArtifactType.StackProfile, project))
        {
            AgentJobLog.Skipped(logger, job.Type, payload.Trigger, project?.CurrentPhase);
            return;
        }

        var version = project.CurrentReleaseVersionId is { } versionId
            ? await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        if (version is null || version.StackSource != StackSource.Infer || !ReleaseVersionRules.IsDevelopment(version.Status))
        {
            LogSkipped(logger, "the version in development doesn't infer its stack");
            return;
        }

        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            throw new JobFailedException("This repository has no code to analyse. Choose a stack instead.", transient: false, ErrorCodes.StackInferNoCode);
        }

        if (catalogs.Catalog is not { } catalog)
        {
            // A server-side failure (the stack-catalog readiness check reports it): retried, never blamed on the input.
            throw new JobFailedException("The stack catalog failed to load on this server.", transient: true);
        }

        var runIds = new[] { AgentRunRules.RunId(job.Id), AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey) };
        if (payload.Trigger == LifecycleJobPayload.PhaseEntered && await db.Artifacts.AsNoTracking().AnyAsync(
                a => a.ProjectId == project.Id && a.ReleaseVersionId == version.Id && a.Type == ArtifactType.StackProfile
                    && (a.AgentRunId == null || !runIds.Contains(a.AgentRunId.Value)),
                cancellationToken))
        {
            LogSkipped(logger, "the version already has a stack profile");
            return;
        }

        var summary = await inputs.CurrentAsync(project.Id, ArtifactType.CodebaseSummary, cancellationToken);
        var previous = await inputs.PreviousAsync(payload, ArtifactType.StackProfile, cancellationToken);
        var repoBase = await bases.ReadAsync(version, detect: true, cancellationToken);
        await RecordDetectionAsync(project.Id, version, repoBase.Detection, job.Id, cancellationToken);

        var context = new StackProfileContext(catalog, repoBase.RepoVcs, repoBase.Files);
        var detectorProfile = StackProfile.FromDetection(repoBase.Detection, repoBase.RepoVcs, version.BaseCommitSha);
        var report = repoBase.Detection is { } d ? new StackDetectionReport(d.Suggestions, d.Conflicts, []) : null;
        var baseText = version.BaseTag is { } tag ? $"{tag} ({version.BaseCommitSha})" : $"commit {version.BaseCommitSha}";
        var options = CatalogOptions(catalog);

        AgentRunRequest Request(string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.StackAnalyst,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = project.Name,
                ["VERSION_REQUEST"] = version.Request,
                ["BASE"] = baseText,
                ["CODEBASE_SUMMARY"] = summary?.Content,
                ["STACK_DETECTION"] = StackGuidance.DetectionInput(report),
                ["DETECTOR_PROFILE"] = StackProfile.ToJson(detectorProfile),
                ["CATALOG_OPTIONS"] = options,
                ["PREVIOUS_PROFILE"] = previous?.Content,
                [AgentRunner.FeedbackInput] = feedback,
            },
            InputArtifacts = [.. new[] { summary, previous }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,

            // FR-180: the Repository Analyst failed or produced nothing: the Stack Analyst still runs, with the FR-70 warning.
            Warnings = summary is null ? [AgentRunWarnings.SummaryUnavailable] : [],
        };

        var spec = new ArtifactAgentSpec(
            ArtifactType.StackProfile,
            text =>
            {
                var reply = StackProfile.CheckReply(text, context);
                if (reply.IsFailure)
                {
                    return Result.Failure<string>(reply.Error!);
                }

                var reconciled = StackProfile.Reconcile(reply.Value.Document, repoBase.Detection, repoBase.RepoVcs, version.BaseCommitSha);
                return Result.Success(StackProfile.Render(reply.Value.Narrative, reconciled));
            },
            (_, error) => Request(ArtifactAgentJob.JoinFeedback(payload.Feedback, StackProfile.CorrectiveFeedback(error, catalog))))
        {
            OnAdded = (draft, run) =>
            {
                var unresolved = StackProfile.Parse(draft.Content) is { IsSuccess: true } parsed ? StackProfile.Unresolved(parsed.Value.Document) : [];
                db.AddEvent(
                    EventTypes.StackProfileCreated,
                    project.Id,
                    new { artifactId = draft.Id, version = draft.Version, releaseVersionId = version.Id, agentRunId = run.RunId, unresolvedLayers = unresolved },
                    SystemActor.Name,
                    run.CompletedAt);
            },
        };

        await agentJob.RunAsync(Request(payload.Feedback), spec, cancellationToken);
    }

    /// <summary>
    /// FR-155, FR-180: the detector's result at the base commit, recorded once per job as <c>STACK_DETECTED</c> (with evidence,
    /// so the Analysis page can offer "Fill in manually" from it). Nothing is recorded when the commit couldn't be read.
    /// </summary>
    private async Task RecordDetectionAsync(Guid projectId, ReleaseVersion version, StackDetection? detection, Guid jobId, CancellationToken cancellationToken)
    {
        if (detection is null)
        {
            return;
        }

        var job = jobId.ToString();
        var recorded = await db.Events
            .FromSql($"SELECT * FROM events WHERE project_id = {projectId} AND type = {EventTypes.StackDetected} AND payload ->> 'jobId' = {job}")
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        if (recorded)
        {
            return;
        }

        db.AddEvent(
            EventTypes.StackDetected,
            projectId,
            new
            {
                releaseVersionId = version.Id,
                version = version.Version,
                baseCommitSha = version.BaseCommitSha,
                suggestions = detection.Suggestions.Select(s => new { choice = s.Choice, option = s.Option, path = s.Path, mode = s.Mode, evidence = s.Evidence }),
                conflicts = detection.Conflicts.Select(c => c.Choice),
                conflictCandidates = detection.Conflicts.Select(c => new { choice = c.Choice, candidates = c.Candidates.Select(x => new { option = x.Option, path = x.Path, evidence = x.Evidence }) }),
                mismatches = Array.Empty<object>(),
                jobId,
            },
            SystemActor.Name,
            time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The catalog's options per choice, as platform data for the prompt (<c>- frontend: fe-vue (Vue + Vite), …</c>).</summary>
    internal static string CatalogOptions(StackCatalog catalog)
    {
        var sb = new StringBuilder();
        foreach (var id in StackChoices.Order)
        {
            var choice = catalog.Choice(id);
            sb.Append("- ").Append(id).Append(": ")
                .Append(string.Join(", ", choice.Options.Where(o => o.Id != StackIds.None).Select(o => o.Mode is { } mode ? $"{o.Id} ({o.Name}; mode {mode})" : $"{o.Id} ({o.Name})")))
                .Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stack analysis skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);
}

/// <summary>
/// FR-180: when a version's Repository Analyst ends — succeeded, skipped or failed — and that version infers its stack and is
/// still in <c>analysis</c>, the Stack Analyst is queued (<c>stack_analysis</c>, trigger <c>phase_entered</c>), in the same
/// transaction as the job's final status. Idempotent: nothing is queued when a stack analysis was already queued for the version.
/// </summary>
public sealed class StackAnalysisTrigger(TimeProvider time) : IJobSucceededHook, IJobEndedHook
{
    public Task OnJobSucceededAsync(AideDbContext db, JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Type == JobTypes.RepositoryAnalysis ? QueueAsync(db, job.Payload, cancellationToken) : Task.CompletedTask;
    }

    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.RepositoryAnalysis)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is not null)
        {
            await QueueAsync(db, payload, cancellationToken);
        }
    }

    private async Task QueueAsync(AideDbContext db, string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        RepositoryAnalysisJobPayload payload;
        try
        {
            payload = AideJson.Deserialize<RepositoryAnalysisJobPayload>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == payload.ProjectId)
            .Select(p => new { p.Id, p.CurrentPhase, p.CurrentReleaseVersionId, p.Checkpoint })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null || project.CurrentReleaseVersionId != payload.ReleaseVersionId || project.CurrentPhase != Phase.Analysis)
        {
            return;
        }

        var version = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == payload.ReleaseVersionId)
            .Select(v => new { v.StackSource, v.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (version is not { StackSource: StackSource.Infer })
        {
            return;
        }

        var queued = db.ChangeTracker.Entries<Job>().Any(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.StackAnalysis && e.Entity.ProjectId == project.Id)
            || await db.Jobs.AsNoTracking().AnyAsync(j => j.ProjectId == project.Id && j.Type == JobTypes.StackAnalysis && j.CreatedAt >= version.CreatedAt, cancellationToken);
        if (queued)
        {
            return;
        }

        var now = time.GetUtcNow();
        db.Jobs.Add(new Job
        {
            Type = JobTypes.StackAnalysis,
            Payload = AideJson.Serialize(new LifecycleJobPayload(project.Id, project.Checkpoint, LifecycleJobPayload.PhaseEntered)),
            ProjectId = project.Id,
            Status = JobStatus.Queued,
            Attempts = 0,
            RunAfter = now,
            CreatedAt = now,
        });
    }
}

public static class StackAnalysisServiceCollectionExtensions
{
    /// <summary>FR-180–FR-185: the Stack Analyst job, its trigger after the Repository Analyst, and the Analysis page's services.</summary>
    public static IServiceCollection AddStackAnalysis(this IServiceCollection services)
    {
        services.AddJobHandler<StackAnalysisJobHandler>(JobTypes.StackAnalysis);
        services.AddScoped<StackAnalysisTrigger>();
        services.AddScoped<IJobSucceededHook>(sp => sp.GetRequiredService<StackAnalysisTrigger>());
        services.AddScoped<IJobEndedHook>(sp => sp.GetRequiredService<StackAnalysisTrigger>());
        services.AddScoped<StackProfileBaseReader>();
        services.AddScoped<AnalysisService>();
        return services;
    }
}
