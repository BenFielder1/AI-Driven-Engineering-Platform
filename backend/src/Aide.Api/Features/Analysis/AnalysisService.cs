using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Refinement;
using Aide.Api.Features.Stack;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Analysis;

/// <summary>
/// FR-180–FR-185: the Analysis page (<c>GET /analysis</c>) and re-running the Stack Analyst (<c>POST /analysis/run</c>). The
/// rules are <see cref="StackProfile"/>'s; this service reads the rows and queues the job.
/// </summary>
public sealed partial class AnalysisService(
    AideDbContext db,
    RefinementService refinement,
    ProjectStackService stacks,
    VersionStackReader compositions,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<AnalysisService> logger)
{
    /// <summary>
    /// The Analysis page of <paramref name="releaseVersionId"/>, or of the project's current release version when it's
    /// <c>null</c> (FR-182: a past version's page shows that version's summary, run and profile). <c>NOT_FOUND</c> for an
    /// unknown project, a project without versions, or a version that isn't the project's. A version other than the current
    /// one is read-only: it can't be approved or continued.
    /// </summary>
    public async Task<Result<AnalysisDto>> GetAsync(Guid projectId, CancellationToken cancellationToken, Guid? releaseVersionId = null)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<AnalysisDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var currentId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var versionId = releaseVersionId ?? currentId;
        var version = versionId is { } wanted
            ? await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == wanted && v.ProjectId == projectId, cancellationToken)
            : null;
        if (version is null)
        {
            return Result.Failure<AnalysisDto>(new DomainError(
                ErrorCodes.NotFound,
                releaseVersionId is null ? "The project was not found." : "The release version was not found in this project."));
        }

        var isCurrent = version.Id == currentId;

        var summary = await refinement.CodebaseSummaryAsync(projectId, version.Id, cancellationToken);
        var pinned = await compositions.ForVersionAsync(version, cancellationToken);
        var previous = await PreviousCompositionAsync(version, cancellationToken);
        var hasCode = !string.IsNullOrWhiteSpace(version.BaseCommitSha);
        if (version.StackSource != StackSource.Infer)
        {
            // D-V5-2: a manual (or no-stack) version shows only the summary; nothing to approve.
            return Result.Success(new AnalysisDto(
                version.Id, version.Version, project.CurrentPhase, version.StackSource, hasCode, version.BaseCommitSha, summary,
                null, null, [], [], false, "This version's stack wasn't inferred.", false, null, null,
                pinned.Composition is { } c ? StackMapping.ToDto(c) : null, previous, Deployment(null, pinned.Composition)));
        }

        var latest = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == version.Id && a.Type == ArtifactType.StackProfile)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var lockedExists = latest is not null && (latest.Status == ArtifactStatus.Locked || await db.Artifacts.AsNoTracking().AnyAsync(
            a => a.ProjectId == projectId && a.ReleaseVersionId == version.Id && a.Type == ArtifactType.StackProfile && a.Status == ArtifactStatus.Locked,
            cancellationToken));
        var parsed = latest is null ? null : StackProfile.Parse(latest.Content);
        var document = parsed is { IsSuccess: true } ok ? ok.Value.Document : null;
        var profile = latest is null
            ? null
            : new StackProfileViewDto(
                latest.Id,
                latest.Version,
                latest.Status,
                ArtifactRules.IntegrityOf(latest.Status, latest.Content, latest.ContentHash),
                ArtifactRules.IsAuthoredByAgent(latest.CreatedByUserId),
                latest.AgentRunId,
                latest.CreatedAt,
                parsed is { IsSuccess: true } p ? p.Value.Narrative : string.Empty,
                document is null ? null : StackProfileMapping.ToDto(document),
                parsed is { IsFailure: true } failed ? failed.Error!.Details : []);

        var blanks = document is null
            ? []
            : StackProfile.NeedsInput(document).Select(layer =>
            {
                var value = document.Layers.Get(layer);
                return new StackProfileBlankDto(
                    layer,
                    value.Status,
                    document.Questions.FirstOrDefault(q => q.Layer == layer)?.Text,
                    value.Technology,
                    [.. value.Candidates.Select(StackProfileMapping.ToDto)]);
            }).ToList();
        var unresolved = document is null ? [] : StackProfile.Unresolved(document);
        var (canApprove, blocked) = isCurrent
            ? ApproveState(project, latest, document, unresolved)
            : (false, $"This is version {version.Version}, not the project's current version; its profile is read-only here.");

        var repoHost = await stacks.RepoHostAsync(projectId, cancellationToken);
        var selection = document is null ? null : WithRepoHost(StackProfileMapping.ToRequest(document), repoHost);
        var suggested = await SuggestedAsync(projectId, version, cancellationToken);
        return Result.Success(new AnalysisDto(
            version.Id,
            version.Version,
            project.CurrentPhase,
            version.StackSource,
            hasCode,
            version.BaseCommitSha,
            summary,
            await RunAsync(projectId, version, cancellationToken),
            profile,
            blanks,
            unresolved,
            canApprove,
            blocked,
            isCurrent && project.CurrentPhase == Phase.Analysis && lockedExists && (pinned.Composition is not null || LockedGeneralist(latest, document)),
            suggested is null ? null : StackProfileMapping.ToDto(suggested),
            selection,
            pinned.Composition is { } composition ? StackMapping.ToDto(composition) : null,
            previous,
            Deployment(latest, pinned.Composition)));
    }

    /// <summary>
    /// <c>POST /analysis/run</c> (FR-180 Retry, re-run): queues the Stack Analyst. <c>WRONG_PHASE</c> unless the project is in
    /// <c>analysis</c> on a version that infers its stack; <c>BUDGET_EXCEEDED</c> when the token budget is used up;
    /// <c>INVALID_STATE</c> while a Stack Analyst job is queued or running. <c>CONFLICT</c> on a concurrent start.
    /// </summary>
    public async Task<Result<StackAnalysisQueuedDto>> RunAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<StackAnalysisQueuedDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var version = project.CurrentReleaseVersionId is { } versionId
            ? await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        if (project.CurrentPhase != Phase.Analysis || version is not { StackSource: StackSource.Infer } || !ReleaseVersionRules.IsDevelopment(version.Status))
        {
            return Result.Failure<StackAnalysisQueuedDto>(new DomainError(
                ErrorCodes.WrongPhase,
                $"The Stack Analyst runs only in the analysis phase of a version that infers its stack; the project is in {project.CurrentPhase.ToWire()}."));
        }

        var now = time.GetUtcNow();
        var budget = TokenBudget.CheckCanStart(project.TokenBudget, project.TokensUsed);
        if (budget.IsFailure)
        {
            // FR-26: refused up front; the event lets Owners be notified like a refused run.
            db.ChangeTracker.Clear();
            db.AddEvent(
                EventTypes.BudgetExceeded,
                projectId,
                new { agent = AgentKind.StackAnalyst.ToWire(), tokenBudget = project.TokenBudget, tokensUsed = project.TokensUsed },
                actor,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure<StackAnalysisQueuedDto>(budget.Error!);
        }

        if (await db.Jobs.AnyAsync(
                j => j.ProjectId == projectId && j.Type == JobTypes.StackAnalysis && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                cancellationToken))
        {
            return Result.Failure<StackAnalysisQueuedDto>(new DomainError(ErrorCodes.InvalidState, "The Stack Analyst is already queued or running. Wait for it to finish."));
        }

        var job = jobs.Enqueue(JobTypes.StackAnalysis, new LifecycleJobPayload(projectId, project.Checkpoint, LifecycleJobPayload.UserRequested), projectId, now);
        project.UpdatedAt = now; // xmin: concurrent starts conflict instead of queueing two runs
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<StackAnalysisQueuedDto>(saved.Error!);
        }

        LogQueued(logger, projectId, job.Id);
        return Result.Success(new StackAnalysisQueuedDto(job.Id, LifecycleJobPayload.UserRequested, job.CreatedAt));
    }

    /// <summary>FR-181, FR-183: whether an Owner could approve now, and the reason as text when not.</summary>
    private static (bool CanApprove, string? Reason) ApproveState(Project project, Artifact? latest, StackProfileDocument? document, IReadOnlyList<string> unresolved)
    {
        if (latest is null)
        {
            return (false, "There is no stack profile yet.");
        }

        if (latest.Status != ArtifactStatus.Draft)
        {
            return (false, $"The latest profile (v{latest.Version}) is {latest.Status.ToWire()}, not a draft.");
        }

        if (project.CurrentPhase != Phase.Analysis)
        {
            return (false, $"The project is in {project.CurrentPhase.ToWire()}; the profile is decided during analysis.");
        }

        if (document is null)
        {
            return (false, "The profile can't be read. Edit it or run the analysis again.");
        }

        return unresolved.Count > 0 ? (false, $"Answer these before approving: {string.Join(", ", unresolved)}.") : (true, null);
    }

    private static bool LockedGeneralist(Artifact? latest, StackProfileDocument? document) =>
        latest is { Status: ArtifactStatus.Locked } && document is { Generalist: true };

    /// <summary>FR-184: deployment setup is offered once a locked profile pinned a composition with a host.</summary>
    private static AnalysisDeploymentDto Deployment(Artifact? latest, StackComposition? composition)
    {
        if (composition is null)
        {
            return new AnalysisDeploymentDto(false, latest is { Status: ArtifactStatus.Locked } ? "No stack was chosen, so there's nothing to deploy." : "Approve the stack profile first.");
        }

        var deploy = composition.Deploy;
        return deploy.FrontendHost is null && deploy.BackendHost is null && deploy.DatabaseHost is null
            ? new AnalysisDeploymentDto(false, "The stack has no hosts. Choose them in Settings → Stack.")
            : new AnalysisDeploymentDto(true, null);
    }

    private static StackCompositionRequest WithRepoHost(StackCompositionRequest request, StackVcsPick? repoHost) =>
        repoHost is null ? request : request with { Vcs = new StackVcsSelection { Pack = repoHost.Pack, InstanceId = repoHost.InstanceId, SelfManaged = repoHost.SelfManaged } };

    /// <summary>FR-186: the composition of the released version this one builds on (same commit), as the comparison.</summary>
    private async Task<StackCompositionDto?> PreviousCompositionAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return null;
        }

        var json = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.ProjectId == version.ProjectId && v.Id != version.Id && v.MergeCommitSha == version.BaseCommitSha)
            .Where(v => v.Status == ReleaseVersionStatus.Released || v.Status == ReleaseVersionStatus.Imported)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.StackComposition)
            .FirstOrDefaultAsync(cancellationToken);
        return StackCompositionJson.Deserialize(json) is { } previous ? StackMapping.ToDto(previous) : null;
    }

    /// <summary>The latest Stack Analyst job of the version and its run.</summary>
    private async Task<StackAnalysisRunDto?> RunAsync(Guid projectId, ReleaseVersion version, CancellationToken cancellationToken)
    {
        // Jobs carry no version: a version's are those queued from its creation until the project's next version.
        var next = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Id != version.Id && v.CreatedAt > version.CreatedAt)
            .OrderBy(v => v.CreatedAt)
            .Select(v => (DateTimeOffset?)v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.StackAnalysis && j.CreatedAt >= version.CreatedAt)
            .Where(j => next == null || j.CreatedAt < next)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        var main = AgentRunRules.RunId(job.Id);
        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.Id == main || r.Id == corrective).ToListAsync(cancellationToken);
        var run = runs.FirstOrDefault(r => r.Id == corrective) ?? runs.FirstOrDefault(r => r.Id == main);
        var draft = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == ArtifactType.StackProfile && a.AgentRunId != null && (a.AgentRunId == main || a.AgentRunId == corrective))
            .Select(a => new { a.Id, a.Version })
            .FirstOrDefaultAsync(cancellationToken);
        var status = job.Status switch
        {
            JobStatus.Queued or JobStatus.Retried => StackAnalysisRunState.Queued,
            JobStatus.Running => StackAnalysisRunState.Running,
            JobStatus.Succeeded => StackAnalysisRunState.Succeeded,
            _ => StackAnalysisRunState.Failed,
        };
        if (run is { Status: AgentRunStatus.Failed or AgentRunStatus.Cancelled })
        {
            status = StackAnalysisRunState.Failed;
        }

        var error = status == StackAnalysisRunState.Failed ? run?.Error ?? job.LastError : null;
        var code = error?.Split(':', 2)[0] is { Length: > 0 } prefix && prefix.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? prefix : null;
        var trigger = ReadTrigger(job.Payload);
        return new StackAnalysisRunDto(
            job.Id,
            run?.Id,
            status,
            trigger,
            code,
            error,
            AgentRunner.ReadOutput(run?.Output)?.Warnings ?? [],
            draft?.Id,
            draft?.Version,
            job.CreatedAt,
            run?.CompletedAt ?? job.CompletedAt);
    }

    private static string ReadTrigger(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.String ? trigger.GetString()! : LifecycleJobPayload.UserRequested;
        }
        catch (JsonException)
        {
            return LifecycleJobPayload.UserRequested;
        }
    }

    /// <summary>
    /// FR-180 "Fill in manually": the detector's profile of the version's base commit, rebuilt from the latest
    /// <c>STACK_DETECTED</c> event the Stack Analyst recorded for it, or <c>null</c> before one exists.
    /// </summary>
    private async Task<StackProfileDocument?> SuggestedAsync(Guid projectId, ReleaseVersion version, CancellationToken cancellationToken)
    {
        var id = version.Id.ToString();
        var payload = await db.Events
            .FromSql($"SELECT * FROM events WHERE project_id = {projectId} AND type = {EventTypes.StackDetected} AND payload ->> 'releaseVersionId' = {id}")
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        var repoVcs = await stacks.RepoVcsAsync(projectId, cancellationToken);
        return payload is null
            ? StackProfile.FromDetection(null, repoVcs, version.BaseCommitSha)
            : StackProfile.FromDetection(ReadDetection(payload), repoVcs, version.BaseCommitSha);
    }

    private static StackDetection? ReadDetection(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            static IReadOnlyList<string> Strings(JsonElement e, string name) =>
                e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : [];
            static string? Text(JsonElement e, string name) =>
                e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var suggestions = root.TryGetProperty("suggestions", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray()
                    .Where(x => Text(x, "choice") is not null && Text(x, "option") is not null)
                    .Select(x => new StackDetectedChoice(Text(x, "choice")!, Text(x, "option")!, Text(x, "path"), Text(x, "mode"), Strings(x, "evidence")))
                    .ToList()
                : [];
            var conflicts = root.TryGetProperty("conflictCandidates", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray()
                    .Where(x => Text(x, "choice") is not null && x.TryGetProperty("candidates", out _))
                    .Select(x => new StackDetectionConflict(
                        Text(x, "choice")!,
                        [.. x.GetProperty("candidates").EnumerateArray()
                            .Where(y => Text(y, "option") is not null)
                            .Select(y => new StackDetectionCandidate(Text(y, "option")!, Text(y, "path"), Strings(y, "evidence")))]))
                    .ToList()
                : [];
            return new StackDetection(suggestions, conflicts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: Stack Analyst queued (job {JobId})")]
    private static partial void LogQueued(ILogger logger, Guid projectId, Guid jobId);
}
