using System.Globalization;
using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Domain.Specs;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>What a review read from the project branch and the specification, and what the deterministic checks found.</summary>
internal sealed record ReviewMaterial(
    Artifact? Specification,
    Artifact? ImplementationPlan,
    Artifact? CurrentOptimizationPlan,
    string Code,
    IReadOnlyList<ReviewFinding> Findings,
    string FindingsText)
{
    /// <summary>§4.13: the specification's requirements (tagged in iterations), which scope the review.</summary>
    public IReadOnlyList<SpecRequirement> Requirements { get; init; } = [];

    /// <summary>FR-70: the version's codebase summary, an input of the review.</summary>
    public Artifact? CodebaseSummary { get; init; }
}

/// <summary>The succeeded run whose reply was accepted, and the reply as read.</summary>
internal sealed record ReviewAgentOutcome(AgentRunOutcome Run, CodeReviewResult Result);

/// <summary>
/// FR-38–FR-40: one review cycle (job <see cref="JobTypes.Review"/>, queued on entering review, by
/// <c>POST /review</c>, or by an escalation <c>continue</c> with no optimisation plan to run).
/// <list type="number">
/// <item>Start the cycle once per job: <c>projects.review_cycle</c> + 1, pending <c>pre_release</c> and escalation gates of the
/// previous decision cancelled (superseded), and <c>REVIEW_STARTED</c>, in one transaction. A failure after this still shows
/// as that cycle (<c>REVIEW_FAILED</c>, <see cref="ReviewJobEndedHook"/>).</item>
/// <item>Read the project branch as committed (bounded: <see cref="ReviewLimits"/>; changed files first) and run the
/// deterministic checks (§4.7): secret scan, the specification's dependency allowlist, its API endpoint paths.</item>
/// <item>Run the Code Review agent with the specification, plan(s), code (secrets the scan flagged redacted), findings and
/// previous cycle; an invalid reply — or a <c>review_required</c> one without optimisation steps — is re-run once with every
/// reason (as FR-28/FR-29); still unreadable fails the run (<c>INVALID_OUTPUT</c> or <c>INVALID_PLAN</c>), still without
/// steps escalates (<c>no_optimization_plan</c>).</item>
/// <item>Apply the penalties and the §4.5 decision (previous score = the latest earlier cycle's report in the current
/// review timeline, <see cref="ReviewTimeline"/>) and record, in one transaction guarded by the project's <c>xmin</c>: the compliance report, the optimisation plan
/// draft (unless release-eligible or empty), the <c>pre_release</c> gate (release-eligible) or the escalation gate, and
/// <c>REVIEW_COMPLETED</c>.</item>
/// </list>
/// An Owner rejecting the optimisation plan draft (FR-17) or its corruption (FR-20) queues this job to revise the plan for
/// the same cycle: the agent runs again with the feedback and only its plan is used (a new draft linked to the report).
/// §4.13: in iterations (a spec with tagged requirements) the reply is scoped by <see cref="IterationReviewScope"/> before
/// scoring: feature coverage is over <c>[new]</c>/<c>[changed]</c> requirements, and broken <c>[unchanged]</c> ones become
/// regressions (stored in <c>compliance_report.regressions</c>). The agent has the repo tools on the project branch (FR-71).
/// Redelivery is idempotent: run ids derive from the job, a succeeded run is reused, the cycle is read back from the job's
/// <c>REVIEW_STARTED</c>, and one report is recorded per job. A rollback (cycle reset) or another review in between makes
/// the job record nothing.
/// </summary>
public sealed partial class ReviewJobHandler(
    AideDbContext db,
    AgentRunner runner,
    AgentJobInputs inputs,
    IterationContextReader iterations,
    AgentDraftWriter drafts,
    WorkingCopyPreparer preparer,
    IWorkingCopies workingCopies,
    LifecycleWriter writer,
    ExternalChecksCollector externalChecks,
    TimeProvider time,
    ILogger<ReviewJobHandler> logger) : IJobHandler
{
    /// <summary>Cap on the changed files read from the branch comparison.</summary>
    public const int MaxComparedFiles = 5000;

    /// <summary>Cap on the files listed from the project branch.</summary>
    public const int MaxTreeEntries = 20_000;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = AgentJobInputs.Payload(job);
        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Review)
        {
            LogSkipped(logger, "the project is gone or not in review");
            return;
        }

        if (payload.Trigger is LifecycleJobPayload.ArtifactRejected or LifecycleJobPayload.ArtifactCorrupted)
        {
            await ReviseOptimizationPlanAsync(job, payload, project, cancellationToken);
            return;
        }

        if (await ReportExistsAsync(job.Id, cancellationToken))
        {
            LogSkipped(logger, "this job already recorded its report");
            return;
        }

        if (await BeginCycleAsync(job, payload, cancellationToken) is not { } cycle)
        {
            LogSkipped(logger, "the project left review before the cycle started");
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ReviewCycle"] = cycle });

        // FR-85: the PR head's checks are a deterministic input; pending ones are waited for (the job defers itself) first.
        var checks = await externalChecks.CollectAsync(project, payload, cancellationToken);
        var material = await GatherAsync(project, cancellationToken, checks is null ? [] : ExternalCheckRules.Findings(checks.Checks));

        // §4.5 row 4: the previous score is the latest earlier cycle's report in the current review timeline (§4.8).
        var previous = await ReviewTimeline.Current(db, project)
            .Where(r => r.ReviewCycle < cycle)
            .NewestFirst()
            .FirstOrDefaultAsync(cancellationToken);
        var previousText = previous is null
            ? null
            : CodeReviewOutput.PreviousReviewSummary(previous.ReviewCycle, previous.Score, previous.Decision, previous.Violations, previous.MissingFeatures, previous.DriftAreas);

        // A review_required reply must propose optimisation steps, or the cycle could never continue: it is re-run once
        // with that reason, and a reply still without steps escalates (RecordAsync).
        DomainError? MissingPlan(CodeReviewResult result)
        {
            var decision = Assess(result, material, project, cycle, previous?.Score).Assessment.Decision.Decision;
            return ReviewAssessment.LacksOptimizationPlan(decision, result.OptimizationPlan.Count) ? ReviewAssessment.MissingOptimizationPlanError() : null;
        }

        var outcome = await RunAgentAsync(job, project, cycle, material, previousText, payload.Feedback, MissingPlan, cancellationToken);
        await RecordAsync(job, cycle, material, outcome, previous?.Score, checks, cancellationToken);
    }

    private static IterationReviewResult Assess(CodeReviewResult result, ReviewMaterial material, Project project, int cycle, decimal? previousScore) =>
        IterationReviewScope.Assess(
            new ReviewAssessmentInput(
                result,
                material.Findings,
                project.ComplianceThreshold,
                cycle,
                project.MaxReviewCycles,
                project.ExtraReviewCycles,
                previousScore),
            material.Requirements);

    /// <summary>The cycle this job reviews: read back from its <c>REVIEW_STARTED</c>, or started now; <c>null</c> if the project left review.</summary>
    private async Task<int?> BeginCycleAsync(JobExecution job, LifecycleJobPayload payload, CancellationToken cancellationToken)
    {
        if (await ReviewEvents.StartedCycleAsync(db, payload.ProjectId, job.Id, cancellationToken) is { } started)
        {
            return started;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Review)
        {
            return null;
        }

        var now = time.GetUtcNow();
        project.ReviewCycle = ReviewCycleRules.NextCycle(project.ReviewCycle);
        project.UpdatedAt = now;

        // The decision gates of an earlier cycle no longer describe the code being reviewed: a new decision replaces them.
        var superseded = await db.ApprovalGates
            .Where(g => g.ProjectId == project.Id && g.Status == GateStatus.Pending && (g.Type == GateType.PreRelease || g.Type == GateType.Escalation))
            .ToListAsync(cancellationToken);
        foreach (var gate in superseded)
        {
            gate.Status = GateRules.Cancel(gate.Status);
            gate.DecidedAt = now;
            gate.Comment = $"Cancelled: review cycle {project.ReviewCycle.ToString(CultureInfo.InvariantCulture)} started.";
            db.AddEvent(EventTypes.GateCancelled, project.Id, new { gateId = gate.Id, type = gate.Type, cause = "review_started", cycle = project.ReviewCycle }, SystemActor.Name, now);
        }

        db.AddEvent(
            EventTypes.ReviewStarted,
            project.Id,
            new { jobId = job.Id, cycle = project.ReviewCycle, trigger = payload.Trigger, checkpoint = project.Checkpoint, releaseVersionId = project.CurrentReleaseVersionId },
            SystemActor.Name,
            now);
        await SaveAsync("starting the review cycle", cancellationToken);
        LogCycleStarted(logger, project.ReviewCycle, payload.Trigger);
        return project.ReviewCycle;
    }

    /// <param name="project">The project in review.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="externalFindings">FR-85: findings from failed required checks, added to the deterministic ones.</param>
    private async Task<ReviewMaterial> GatherAsync(Project project, CancellationToken cancellationToken, IReadOnlyList<ReviewFinding>? externalFindings = null)
    {
        var preparation = await preparer.TryPrepareAsync(project.Id, cancellationToken);
        if (preparation.Result.IsFailure)
        {
            var error = preparation.Result.Error!;
            throw new JobFailedException(error.Message, preparation.Transient, error.Code);
        }

        var working = preparation.Result.Value;
        var limits = ReviewLimits.Default;
        var tree = await Git(() => workingCopies.ListFilesAsync(project.Id, working.Branch, MaxTreeEntries, cancellationToken)) ?? [];
        BranchComparison? comparison = null;
        try
        {
            comparison = await workingCopies.CompareAsync(
                project.Id, working.Provider.ComparisonBaseRef(working.Connection), working.Branch, 1, MaxComparedFiles, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            // Without the comparison every file is reviewed in path order.
            LogCompareFailed(logger, ex.GetType().Name);
        }

        var changed = (comparison?.Files ?? []).GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
        var selected = ReviewCode.SelectForReading(tree, changed.Keys, limits);
        var contents = await Git(() => workingCopies.ReadCommittedAsync(project.Id, working.Branch, selected, cancellationToken)) ?? [];

        var spec = await inputs.CurrentAsync(project.Id, ArtifactType.TechnicalSpecification, cancellationToken);
        var implementation = await inputs.CurrentAsync(project.Id, ArtifactType.ImplementationPlan, cancellationToken);
        var currentPlanId = await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken);
        var optimization = currentPlanId is { } planId && planId != implementation?.Id
            ? await inputs.CurrentAsync(project.Id, ArtifactType.OptimizationPlan, cancellationToken)
            : null;

        var specText = spec?.Content ?? string.Empty;
        var allowlist = SpecReading.AllowedDependencies(specText);
        var endpoints = SpecReading.EndpointPaths(specText);
        var findings = DeterministicChecks.SecretScan(contents)
            .Concat(allowlist is null ? [] : DeterministicChecks.DependencyAllowlist(allowlist, DependencyManifests.Used(contents)))
            .Concat(DeterministicChecks.EndpointCoverage(endpoints, contents))
            .Concat(externalFindings ?? [])
            .ToList();
        LogGathered(logger, tree.Count, contents.Count, findings.Count);
        var iteration = await iterations.ReadAsync(project.Id, cancellationToken);
        return new ReviewMaterial(
            spec,
            implementation,
            optimization,
            // NFR-SEC: secrets the scan flagged never reach the provider; the markers keep the line numbers.
            ReviewCode.RenderForPrompt(tree, changed, ReviewCode.RedactSecrets(contents), limits),
            findings,
            ReviewCode.RenderFindings(findings, allowlist is not null, endpoints.Count))
        {
            Requirements = RequirementParser.Parse(specText).Requirements,
            CodebaseSummary = iteration.CodebaseSummary,
        };
    }

    /// <summary>
    /// Runs the Code Review agent. A reply that can't be read, or that fails <paramref name="requirement"/> (which returns the
    /// reason), is re-run once with the reasons as feedback; an unreadable second reply fails the job, a readable one is used.
    /// </summary>
    private async Task<ReviewAgentOutcome> RunAgentAsync(
        JobExecution job,
        Project project,
        int cycle,
        ReviewMaterial material,
        string? previousReview,
        string? feedback,
        Func<CodeReviewResult, DomainError?> requirement,
        CancellationToken cancellationToken)
    {
        var maxFiles = project.MaxFilesPerStep;
        var plans = material.CurrentOptimizationPlan is { } optimization
            ? $"{material.ImplementationPlan?.Content}\n\n---\n\n# Optimization plan v{optimization.Version.ToString(CultureInfo.InvariantCulture)} (the latest cycle's steps)\n\n{optimization.Content}"
            : material.ImplementationPlan?.Content;
        AgentRunRequest Request(string? runFeedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.CodeReview,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["REVIEW_CYCLE"] = cycle.ToString(CultureInfo.InvariantCulture),
                ["TECHNICAL_SPECIFICATION"] = material.Specification?.Content,
                ["IMPLEMENTATION_PLAN"] = plans,
                ["CODEBASE_SUMMARY"] = material.CodebaseSummary?.Content,
                ["DETERMINISTIC_FINDINGS"] = material.FindingsText,
                ["PREVIOUS_REVIEW"] = previousReview,
                ["CODE"] = material.Code,
                ["MAX_FILES_PER_STEP"] = maxFiles.ToString(CultureInfo.InvariantCulture),
            },
            InputArtifacts = [.. new[] { material.Specification, material.ImplementationPlan, material.CurrentOptimizationPlan, material.CodebaseSummary }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = runFeedback,
            Repo = RepoScope.WorkingTree,
        };

        var first = await runner.RunAsync(Request(feedback) with { RunKey = null, Check = null }, cancellationToken);
        var parsed = CodeReviewOutput.Parse(first.Text, maxFiles);
        var problem = parsed.IsSuccess ? requirement(parsed.Value) : parsed.Error!;
        if (problem is null)
        {
            return new ReviewAgentOutcome(first, parsed.Value);
        }

        LogRejected(logger, problem.Code, problem.Message);
        var second = await runner.RunAsync(
            Request(ArtifactAgentJob.JoinFeedback(feedback, CodeReviewOutput.CorrectiveFeedback(problem))) with
            {
                RunKey = AgentOutput.CorrectiveRunKey,
                Check = text => CodeReviewOutput.Parse(text, maxFiles) is { IsFailure: true } failed ? Result.Failure(failed.Error!) : Result.Success(),
            },
            cancellationToken);
        var reparsed = CodeReviewOutput.Parse(second.Text, maxFiles);
        return reparsed.IsSuccess
            ? new ReviewAgentOutcome(second, reparsed.Value)
            : throw new JobFailedException(reparsed.Error!.Message, transient: false, reparsed.Error.Code);
    }

    private async Task RecordAsync(
        JobExecution job, int cycle, ReviewMaterial material, ReviewAgentOutcome outcome, decimal? previousScore, CollectedChecks? checks, CancellationToken cancellationToken)
    {
        // The run's completion bumped the project row (tokens used): re-read it so its xmin is current.
        db.ChangeTracker.Clear();
        var utcNow = time.GetUtcNow();
        var now = utcNow.AddTicks(-(utcNow.Ticks % EventLog.Step.Ticks));
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == job.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Review || project.ReviewCycle != cycle)
        {
            // A rollback (which resets the cycle) or another review happened meanwhile: this result is stale.
            LogSkipped(logger, "the review was superseded while it ran");
            return;
        }

        if (await ReportExistsAsync(job.Id, cancellationToken))
        {
            LogSkipped(logger, "this job already recorded its report");
            return;
        }

        project.UpdatedAt = now;
        // Still no optimisation steps after the corrective re-run: a human decides (escalation no_optimization_plan).
        // §4.13: scored on the reply as scoped to the iteration; its regressions are stored with the report.
        var scoped = Assess(outcome.Result, material, project, cycle, previousScore);
        var assessment = ReviewAssessment.RequireOptimizationPlan(scoped.Assessment, outcome.Result.OptimizationPlan.Count);
        var decision = assessment.Decision;

        Artifact? draft = null;
        if (ReviewAssessment.DraftsOptimizationPlan(decision.Decision, outcome.Result.OptimizationPlan.Count))
        {
            draft = await drafts.AddAsync(project.Id, ArtifactType.OptimizationPlan, outcome.Result.OptimizationPlanMarkdown!, outcome.Run.RunId, now, cancellationToken);
        }

        var report = new ComplianceReport
        {
            ProjectId = project.Id,
            ReleaseVersionId = project.CurrentReleaseVersionId ?? Guid.Empty,
            ReviewCycle = cycle,
            Checkpoint = project.Checkpoint,
            Score = assessment.Overall,
            CategoryScores = assessment.Categories,
            Violations = assessment.Violations,
            MissingFeatures = scoped.ScopedAgent.MissingFeatures,
            DriftLevel = assessment.Drift,
            DriftAreas = scoped.ScopedAgent.DriftAreas,
            Regressions = AideJson.Serialize(scoped.Regressions),
            Decision = decision.Decision,
            EscalationReason = decision.Reason,
            AgentRunId = outcome.Run.RunId,
            Threshold = project.ComplianceThreshold,
            OptimizationPlanArtifactId = draft?.Id,
            ExternalChecks = checks?.ToJson(),
            CreatedAt = now,
        };
        db.ComplianceReports.Add(report);

        var score = assessment.Overall.ToString("0.000", CultureInfo.InvariantCulture);
        var threshold = project.ComplianceThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        switch (decision.Decision)
        {
            case ReviewDecision.ReleaseEligible:
                // FR-39: release still needs the pre_release gate, in every approval mode (§4.2).
                writer.AddGate(project, GateType.PreRelease, null, SystemActor.Name, now, $"Review cycle {cycle.ToString(CultureInfo.InvariantCulture)}: score {score} meets the threshold {threshold}");
                break;
            case ReviewDecision.EscalationRequired:
                writer.AddGate(
                    project,
                    GateType.Escalation,
                    null,
                    SystemActor.Name,
                    now,
                    $"Review cycle {cycle.ToString(CultureInfo.InvariantCulture)}: score {score}, drift {assessment.Drift.ToWire()} ({decision.Reason?.ToWire()})",
                    escalationReason: decision.Reason);
                break;
            default:
                break;
        }

        db.AddEvent(
            EventTypes.ReviewCompleted,
            project.Id,
            new
            {
                jobId = job.Id,
                reportId = report.Id,
                cycle,
                checkpoint = project.Checkpoint,
                releaseVersionId = project.CurrentReleaseVersionId,
                overallScore = assessment.Overall,
                threshold = project.ComplianceThreshold,
                driftLevel = assessment.Drift,
                decision = decision.Decision,
                decisionReason = decision.Reason,
                optimizationPlanArtifactId = draft?.Id,
                runId = outcome.Run.RunId,
                regressionCount = scoped.Regressions.Count,
                checksTimedOut = checks?.TimedOut ?? false,
            },
            SystemActor.Name,
            now);
        await SaveAsync("recording the review", cancellationToken);
        LogRecorded(logger, cycle, assessment.Overall, decision.Decision, decision.Reason);
    }

    /// <summary>FR-17/FR-20 for the optimisation plan: revise the plan of the latest report's cycle with the feedback.</summary>
    private async Task ReviseOptimizationPlanAsync(JobExecution job, LifecycleJobPayload payload, Project project, CancellationToken cancellationToken)
    {
        var report = await ReviewTimeline.Current(db, project).NewestFirst().FirstOrDefaultAsync(cancellationToken);
        if (report is null || report.Decision == ReviewDecision.ReleaseEligible || report.ReviewCycle != project.ReviewCycle)
        {
            LogSkipped(logger, "there is no current review whose optimisation plan can be revised");
            return;
        }

        var material = await GatherAsync(project, cancellationToken);
        var summary = CodeReviewOutput.PreviousReviewSummary(report.ReviewCycle, report.Score, report.Decision, report.Violations, report.MissingFeatures, report.DriftAreas);
        var rejected = payload.ArtifactId is { } id
            ? await db.Artifacts.AsNoTracking().Where(a => a.Id == id && a.ProjectId == project.Id).Select(a => (int?)a.Version).FirstOrDefaultAsync(cancellationToken)
            : null;
        var feedback = ArtifactAgentJob.JoinFeedback(
            payload.Trigger == LifecycleJobPayload.ArtifactRejected
                ? $"The optimization plan{(rejected is { } v ? $" v{v.ToString(CultureInfo.InvariantCulture)}" : string.Empty)} of this cycle was rejected. Revise the optimization plan; the rest of the review stands."
                : "The optimization plan of this cycle was lost to an integrity failure. Produce it again.",
            payload.Feedback);
        var outcome = await RunAgentAsync(
            job,
            project,
            report.ReviewCycle,
            material,
            summary,
            feedback,
            result => result.OptimizationPlan.Count == 0 ? ReviewAssessment.MissingOptimizationPlanError() : null,
            cancellationToken);
        if (outcome.Result.OptimizationPlan.Count == 0)
        {
            LogSkipped(logger, "the revised review proposes no optimisation steps");
            return;
        }

        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % EventLog.Step.Ticks));
        var draft = await drafts.AddAsync(project.Id, ArtifactType.OptimizationPlan, outcome.Result.OptimizationPlanMarkdown!, outcome.Run.RunId, now, cancellationToken);
        if (draft is null)
        {
            LogSkipped(logger, "the project's phase no longer allows an optimisation plan draft");
            return;
        }

        var tracked = await db.ComplianceReports.FirstAsync(r => r.Id == report.Id, cancellationToken);
        tracked.OptimizationPlanArtifactId = draft.Id;
        await SaveAsync("saving the revised optimisation plan", cancellationToken);
    }

    private Task<bool> ReportExistsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var runIds = new[] { AgentRunRules.RunId(jobId), AgentRunRules.RunId(jobId, AgentOutput.CorrectiveRunKey) };
        return db.ComplianceReports.AnyAsync(r => runIds.Contains(r.AgentRunId), cancellationToken);
    }

    private async Task SaveAsync(string what, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A rollback, settings change or concurrent writer changed the project: retry, which re-checks everything.
            throw new JobFailedException($"{char.ToUpperInvariant(what[0])}{what[1..]} collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    /// <summary>Git and working-copy failures retry the job; an unsafe working copy fails it.</summary>
    private static async Task<T?> Git<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (GitCommandException ex)
        {
            throw new JobFailedException(ex.Message, transient: true);
        }
        catch (WorkingCopyException ex)
        {
            throw new JobFailedException(ex.Message, transient: false);
        }
        catch (IOException ex)
        {
            throw new JobFailedException($"Working copy I/O failed: {ex.GetType().Name}.", transient: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review cycle {Cycle} started ({Trigger})")]
    private static partial void LogCycleStarted(ILogger logger, int cycle, string trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review read {TreeCount} files ({ReadCount} with content); deterministic checks found {FindingCount}")]
    private static partial void LogGathered(ILogger logger, int treeCount, int readCount, int findingCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code_review reply was rejected with {ErrorCode}; re-running once with the reasons as feedback: {Reason}")]
    private static partial void LogRejected(ILogger logger, string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review cycle {Cycle} recorded: score {Score}, decision {Decision} ({Reason})")]
    private static partial void LogRecorded(ILogger logger, int cycle, decimal score, ReviewDecision decision, EscalationReason? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comparing the project branch with the default branch failed ({ErrorType}); reviewing without it")]
    private static partial void LogCompareFailed(ILogger logger, string errorType);
}

/// <summary>Reads of the review events.</summary>
public static class ReviewEvents
{
    /// <summary>The cycle recorded by the <c>REVIEW_STARTED</c> event of <paramref name="jobId"/>, or <c>null</c>.</summary>
    public static async Task<int?> StartedCycleAsync(AideDbContext db, Guid projectId, Guid jobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var match = JsonSerializer.Serialize(new { jobId });
        var payload = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.ReviewStarted && EF.Functions.JsonContains(e.Payload, match))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("cycle", out var cycle) && cycle.TryGetInt32(out var value) ? value : null;
    }
}

/// <summary>
/// A review job that ended without succeeding (failed for good, or dead-lettered) records <c>REVIEW_FAILED</c> with the
/// cycle it started (else the project's current cycle) and its redacted last error, so the failure shows in the activity
/// feed and the review status (FR-38).
/// </summary>
public sealed class ReviewJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.Review || job.ProjectId is not { } projectId)
        {
            return;
        }

        var cycle = await ReviewEvents.StartedCycleAsync(db, projectId, job.JobId, cancellationToken)
            ?? await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => (int?)p.ReviewCycle).FirstOrDefaultAsync(cancellationToken);
        if (cycle is null)
        {
            return; // the project is gone
        }

        var reason = string.IsNullOrWhiteSpace(job.Error) ? "The review job failed." : SecretRedaction.SanitizeProviderError(job.Error);
        db.AddEvent(
            EventTypes.ReviewFailed,
            projectId,
            new { jobId = job.JobId, cycle, reason, deadLettered = job.DeadLettered },
            SystemActor.Name,
            time.GetUtcNow());
    }
}
