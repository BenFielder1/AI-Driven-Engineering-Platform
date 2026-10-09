using Aide.Api.Agents;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Refinement;

/// <summary>
/// FR-27: refinement rounds. Starting a round queues an <c>idea_refinement</c> job (the agent never runs in the request,
/// CLAUDE.md rule 11) carrying the answers; the job's handler (<see cref="IdeaRefinementJobHandler"/>) writes the run and
/// the new project request draft. A round's run id is its job id (the corrective re-run's is derived from it), so the
/// rounds, their runs, answers and drafts are read back from the jobs, runs and artifacts tables — no extra schema.
/// </summary>
public sealed partial class RefinementService(
    AideDbContext db,
    JobEnqueuer jobs,
    ArtifactReader artifacts,
    TimeProvider time,
    ILogger<RefinementService> logger)
{
    /// <summary>The error shown on a round that ended without a draft because the project no longer allowed one.</summary>
    public const string NoDraftReason = "No draft was created: the project no longer allowed a project request draft when the round ran (e.g. a release was in progress).";

    /// <summary>Rounds considered when reading the refinement state (newest first).</summary>
    public const int RoundsRead = 50;

    /// <summary>
    /// Queues a round: <c>WRONG_PHASE</c> outside refinement, <c>BUDGET_EXCEEDED</c> when the token budget is used up,
    /// <c>REFINEMENT_IN_PROGRESS</c> while a round is queued or running, <c>VALIDATION</c> for bad answers (an unknown
    /// question id, a blank answer, …). The job and the project's <c>xmin</c> commit together, so two concurrent starts
    /// give one round and one <c>CONFLICT</c>.
    /// </summary>
    public async Task<Result<RefinementRoundDto>> StartAsync(Guid projectId, IReadOnlyList<RefinementAnswerInput>? answers, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<RefinementRoundDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        if (project.CurrentPhase != Phase.Refinement)
        {
            return Result.Failure<RefinementRoundDto>(new DomainError(
                ErrorCodes.WrongPhase,
                $"Refinement rounds run only in the refinement phase; the project is in {project.CurrentPhase.ToWire()}."));
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
                new { agent = AgentKind.IdeaRefinement.ToWire(), tokenBudget = project.TokenBudget, tokensUsed = project.TokensUsed },
                actor,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Failure<RefinementRoundDto>(budget.Error!);
        }

        if (await db.Jobs.AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.IdeaRefinement && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken))
        {
            return Result.Failure<RefinementRoundDto>(new DomainError(
                ErrorCodes.RefinementInProgress,
                "A refinement round is already queued or running. Wait for it to finish, then answer its questions."));
        }

        var rounds = await LoadRoundsAsync(projectId, cancellationToken);
        var latestQuestions = LatestCompleted(rounds)?.Questions ?? [];
        var resolved = RefinementRules.ResolveAnswers(answers, latestQuestions);
        if (resolved.IsFailure)
        {
            return Result.Failure<RefinementRoundDto>(resolved.Error!);
        }

        var job = await jobs.EnqueueAsync(
            JobTypes.IdeaRefinement,
            new LifecycleJobPayload(projectId, project.ProjectVersion, LifecycleJobPayload.UserRequested, Answers: resolved.Value),
            projectId,
            cancellationToken,
            now);
        project.UpdatedAt = now; // xmin: concurrent starts conflict instead of queueing two rounds

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<RefinementRoundDto>(saved.Error!);
        }

        LogStarted(logger, projectId, job.Id, resolved.Value.Count);
        return Result.Success(new RefinementRoundDto(
            AgentRunRules.RunId(job.Id),
            job.Id,
            AgentJobTrigger.UserRequested,
            AgentRunStatus.Queued,
            null,
            [.. resolved.Value.Select(a => new RefinementAnswerDto(a.QuestionId, a.Question, a.Answer))],
            null,
            null,
            null,
            job.CreatedAt,
            null));
    }

    /// <summary>The refinement page state (FR-27): latest draft, latest round and the latest completed round's questions.</summary>
    public async Task<RefinementDto> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var latestDraft = await artifacts.LatestSummaryAsync(projectId, ArtifactType.ProjectRequest, cancellationToken);
        var rounds = await LoadRoundsAsync(projectId, cancellationToken);
        var latest = rounds.Count == 0 ? null : rounds[0];
        var completed = LatestCompleted(rounds);

        // Answers already given to the completed round's questions: by a later round (queued, running or failed).
        var later = latest is not null && completed is not null && latest.Dto.JobId != completed.Dto.JobId ? latest : null;
        var given = (later?.Dto.Answers ?? [])
            .Where(a => a.QuestionId is not null)
            .GroupBy(a => a.QuestionId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Answer, StringComparer.Ordinal);
        var questions = (completed?.Questions ?? [])
            .Select(q => new RefinementQuestionDto(q.Id, q.Question, q.Reason, given.GetValueOrDefault(q.Id)))
            .ToList();

        return new RefinementDto(latestDraft, latest?.Dto, completed?.Dto.RunId, questions);
    }

    private static Round? LatestCompleted(IReadOnlyList<Round> rounds) =>
        rounds.FirstOrDefault(r => r.Dto.Status == AgentRunStatus.Succeeded && r.Questions is not null);

    /// <summary>The project's latest refinement rounds, newest first, with their runs, answers and drafts.</summary>
    private async Task<IReadOnlyList<Round>> LoadRoundsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var roundJobs = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.IdeaRefinement)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Take(RoundsRead)
            .ToListAsync(cancellationToken);
        if (roundJobs.Count == 0)
        {
            return [];
        }

        var runIds = roundJobs.SelectMany(j => new[] { AgentRunRules.RunId(j.Id), AgentRunRules.RunId(j.Id, AgentOutput.CorrectiveRunKey) }).ToList();
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        var drafts = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == ArtifactType.ProjectRequest && a.AgentRunId != null && runIds.Contains(a.AgentRunId.Value))
            .Select(a => new { a.Id, a.Version, RunId = a.AgentRunId!.Value })
            .ToListAsync(cancellationToken);

        var result = new List<Round>(roundJobs.Count);
        foreach (var job in roundJobs)
        {
            var payload = ReadPayload(job.Payload);
            var main = runs.GetValueOrDefault(AgentRunRules.RunId(job.Id));
            var final = runs.GetValueOrDefault(AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey)) ?? main;
            var draft = drafts.FirstOrDefault(d => d.RunId == final?.Id) ?? drafts.FirstOrDefault(d => d.RunId == main?.Id);
            var status = RoundStatus(job, final, draft is not null);

            IReadOnlyList<RefinementQuestion>? questions = null;
            if (status == AgentRunStatus.Succeeded && final is not null
                && AgentRunner.ReadOutput(final.Output) is { } output
                && RefinementRules.Parse(output.Text) is { IsSuccess: true } parsed)
            {
                questions = parsed.Value.Questions;
            }

            var error = status is AgentRunStatus.Failed or AgentRunStatus.Cancelled
                ? final?.Error ?? job.LastError ?? (job.Status == JobStatus.Succeeded ? NoDraftReason : null)
                : null;
            result.Add(new Round(
                new RefinementRoundDto(
                    final?.Id ?? AgentRunRules.RunId(job.Id),
                    job.Id,
                    payload?.TriggerKind,
                    status,
                    error,
                    [.. (payload?.Answers ?? []).Select(a => new RefinementAnswerDto(a.QuestionId, a.Question, a.Answer))],
                    payload?.Feedback,
                    draft?.Id,
                    draft?.Version,
                    job.CreatedAt,
                    final?.CompletedAt ?? job.CompletedAt),
                questions));
        }

        return result;
    }

    /// <summary>
    /// A round is <c>queued</c> until its job starts, <c>running</c> while the job is queued for a retry or running,
    /// then the status of its final run (or of the job when it ended without one, e.g. refused by the budget). A job that
    /// finished without drafting — skipped because the project no longer allowed the draft, or its run succeeded but it was no
    /// longer allowed — is <c>cancelled</c>, never a <c>succeeded</c> round with nothing to show.
    /// </summary>
    private static AgentRunStatus RoundStatus(Job job, AgentRun? final, bool hasDraft) => job.Status switch
    {
        JobStatus.Queued when final is null => AgentRunStatus.Queued,
        JobStatus.Queued or JobStatus.Running => AgentRunStatus.Running,
        JobStatus.Cancelled => AgentRunStatus.Cancelled,
        JobStatus.Succeeded when final is null || (final.Status == AgentRunStatus.Succeeded && !hasDraft) => AgentRunStatus.Cancelled,
        JobStatus.Succeeded => final?.Status ?? AgentRunStatus.Cancelled,
        _ => final?.Status is AgentRunStatus.Succeeded or null ? AgentRunStatus.Failed : final.Status,
    };

    private static LifecycleJobPayload? ReadPayload(string json)
    {
        try
        {
            return AideJson.Deserialize<LifecycleJobPayload>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record Round(RefinementRoundDto Dto, IReadOnlyList<RefinementQuestion>? Questions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: refinement round queued as job {JobId} with {AnswerCount} answers")]
    private static partial void LogStarted(ILogger logger, Guid projectId, Guid jobId, int answerCount);
}
