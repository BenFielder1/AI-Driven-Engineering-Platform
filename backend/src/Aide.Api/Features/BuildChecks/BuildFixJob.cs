using System.Globalization;
using Aide.Api.Agents;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Stack;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.BuildChecks;

/// <summary>
/// FR-256, §4.48: the build-fix agent (<c>build_fix.md</c> v1, the <c>code_review</c> model settings, D-0.6.1-17) plans 1–5
/// fix steps from a failed build check.
/// <list type="number">
/// <item>Inputs: the check's failures with repeat marks and the previous fix's step text (from this version's earlier checks,
/// any cycle, the current one excluded), the error summary, the stack guidance (required deployment files and the targets'
/// build commands, <see cref="DeploymentBuildRules"/>), the current plan's step titles and the branch's file list; the agent
/// has the read-only repo tools on the working tree (at most 25 calls).</item>
/// <item><see cref="BuildFixPlanning.Process"/> validates the reply, attaches the failure evidence blocks (FR-239) and adds
/// missing deployment files (FR-243). An invalid reply is re-run once with every reason; still invalid ends the check
/// <c>exhausted</c> and the pull request job advances to Review with the failures.</item>
/// <item>The steps are stored as execution steps of the current plan with origin <c>build_fix</c> and the check's id,
/// numbered after the plan's highest step, with <c>BUILD_FIX_PLANNED</c>; execution is queued (unless paused). Build-fix
/// steps need no approval gate in any mode (D-0.6.1-17). When they complete, the pull request job runs again and the next
/// check starts.</item>
/// </list>
/// Redelivery is safe: run ids derive from the job, and a check that already has fix steps is left alone.
/// </summary>
public sealed partial class BuildFixJobHandler(
    AideDbContext db,
    AgentRunner runner,
    WorkingCopyPreparer preparer,
    IWorkingCopies workingCopies,
    VersionStackLoader stacks,
    BuildCheckService buildChecks,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<BuildFixJobHandler> logger) : IJobHandler
{
    /// <summary>The execution job's trigger after fix steps were planned.</summary>
    public const string ExecutionTrigger = "build_fix";

    /// <summary>Cap on the files listed from the project branch.</summary>
    public const int MaxTreeEntries = 20_000;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = BuildCheckService.Payload<BuildFixJobPayload>(job, "build fix");
        var check = await db.BuildChecks.FirstOrDefaultAsync(c => c.Id == payload.BuildCheckId, cancellationToken);
        if (check is null || check.Status != BuildCheckStatus.Failed
            || await db.ExecutionSteps.AnyAsync(s => s.BuildCheckId == check.Id, cancellationToken))
        {
            LogSkipped(logger, "the check is gone, not failed, or its fix is already planned");
            return;
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == check.ProjectId, cancellationToken);
        var planId = project is null ? null : await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Execution || planId is null
            || (project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken)) != check.ReleaseVersionId)
        {
            buildChecks.Complete(check, BuildCheckStatus.Cancelled, [], [], time.GetUtcNow());
            await SaveAsync(cancellationToken);
            return;
        }

        var version = (await stacks.CurrentAsync(project.Id, cancellationToken)).ThrowIfUnresolvable();
        var preparation = await preparer.TryPrepareAsync(project.Id, cancellationToken);
        if (preparation.Result.IsFailure)
        {
            var error = preparation.Result.Error!;
            throw new JobFailedException(error.Message, preparation.Transient, error.Code);
        }

        var working = preparation.Result.Value;
        IReadOnlyList<Domain.Review.RepositoryFile>? tree;
        try
        {
            tree = await workingCopies.ListFilesAsync(project.Id, working.Branch, MaxTreeEntries, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            tree = null;
        }

        IReadOnlySet<string>? repoFiles = tree is { Count: > 0 and < MaxTreeEntries } ? tree.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var planSteps = await db.ExecutionSteps.AsNoTracking().Where(s => s.PlanArtifactId == planId).ToListAsync(cancellationToken);
        var highest = planSteps.Count == 0 ? 0 : planSteps.Max(s => s.StepNumber);
        var repeats = BuildFixPlanning.Repeats(check.Failures, await HistoryAsync(check, cancellationToken));
        var stackGuidance = BuildFixPlanning.RenderStackGuidance(
            version.Stack is { } stack ? StackRequiredFiles.For(stack) : [],
            await BuildCommandsAsync(project.Id, version.Stack?.Composition, cancellationToken));
        var planCheck = new BuildFixPlanCheck(
            highest,
            project.MaxFilesPerStep,
            version.PlanAgents(ArtifactType.OptimizationPlan),
            check.Failures,
            repoFiles,
            version.Stack is { } composed ? new DeployFileCheck(StackRequiredFiles.For(composed), repoFiles, Optimisation: true) : null);

        AgentRunRequest Request(int attempt, string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.BuildFix,
            Job = job,
            RunKey = string.Create(CultureInfo.InvariantCulture, $"build-fix-{check.Id:N}-{attempt}"),
            Stack = version.Stack,
            Inputs = new Dictionary<string, string?>
            {
                ["FAILURES"] = BuildFixPlanning.RenderFailures(check.Failures, repeats),
                ["ERROR_SUMMARY"] = BuildFixPlanning.RenderErrorSummary(check.Failures),
                ["PLAN_STEPS"] = BuildFixPlanning.RenderPlanSteps(planSteps.Where(s => s.Status != StepStatus.Invalidated).Select(s => new BuildFixStepText(s.StepNumber, s.Title, s.Task))),
                ["MAX_FILES_PER_STEP"] = project.MaxFilesPerStep.ToString(CultureInfo.InvariantCulture),
                ["STACK_GUIDANCE"] = stackGuidance,
                ["REPO_FILES"] = BuildFixPlanning.RenderRepoFiles(tree?.Select(f => f.Path)),
            },
            UserFeedback = feedback,

            // §4.48: the read-only repo tools on the project branch's working tree (the per-run limit is 25 calls).
            Repo = RepoScope.WorkingTree,
        };

        var first = await runner.RunAsync(Request(1, null), cancellationToken);
        var processed = BuildFixPlanning.Process(first.Text, planCheck);
        if (processed.IsFailure && BuildCheckRules.RetryFixPlanning(0))
        {
            LogRetry(logger, check.Id, processed.Error!.Code);
            var second = await runner.RunAsync(Request(2, BuildFixPlanning.CorrectiveFeedback(processed.Error!)), cancellationToken);
            processed = BuildFixPlanning.Process(second.Text, planCheck);
        }

        var now = time.GetUtcNow();
        if (processed.IsFailure)
        {
            // §4.48 step 4: invalid after one retry: the check is exhausted and Review starts with the failures.
            LogInvalid(logger, check.Id, processed.Error!.Message);
            await buildChecks.ExhaustAsync(check, planId, SystemActor.Name, now, cancellationToken);
            await SaveAsync(cancellationToken);
            return;
        }

        var plan = processed.Value;
        foreach (var dropped in plan.DroppedEvidence)
        {
            LogEvidenceDropped(logger, check.Id, dropped);
        }

        foreach (var step in plan.Steps)
        {
            db.ExecutionSteps.Add(new ExecutionStep
            {
                ProjectId = project.Id,
                ReleaseVersionId = check.ReleaseVersionId,
                PlanArtifactId = planId.Value,
                StepNumber = step.Number,
                Title = step.Title,
                Task = step.Task,
                DeclaredFiles = step.Files,
                Dependencies = step.Dependencies,
                UserInstructions = step.UserInstructions,
                AgentKey = step.Agent,
                Covers = step.Covers,
                Status = StepStatus.Pending,
                Attempt = 0,
                Origin = StepOrigin.BuildFix,
                BuildCheckId = check.Id,
            });
        }

        buildChecks.FixPlanned(check, [.. plan.Steps.Select(s => s.Number)], now);
        if (!project.ExecutionPaused)
        {
            await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(project.Id, ExecutionTrigger), project.Id, cancellationToken, now);
        }

        await SaveAsync(cancellationToken);
    }

    /// <summary>§4.48: this version's earlier checks (any cycle; the current one excluded) with the fix steps planned from them.</summary>
    private async Task<IReadOnlyList<BuildCheckHistory>> HistoryAsync(BuildCheck current, CancellationToken cancellationToken)
    {
        var earlier = await db.BuildChecks.AsNoTracking()
            .Where(c => c.ReleaseVersionId == current.ReleaseVersionId && c.Id != current.Id)
            .ToListAsync(cancellationToken);
        if (earlier.Count == 0)
        {
            return [];
        }

        var ids = earlier.Select(c => c.Id).ToList();
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.BuildCheckId != null && ids.Contains(s.BuildCheckId.Value))
            .Select(s => new { CheckId = s.BuildCheckId!.Value, s.StepNumber, s.Title, s.Task })
            .ToListAsync(cancellationToken);
        return [.. earlier.Select(c => new BuildCheckHistory(
            c.ReviewCycle,
            c.Attempt,
            c.Status,
            c.Failures,
            [.. steps.Where(s => s.CheckId == c.Id).Select(s => new BuildFixStepText(s.StepNumber, s.Title, s.Task))]))];
    }

    /// <summary>§4.48 <c>STACK_GUIDANCE</c>: the build command of each deployment target, derived from the composition.</summary>
    private async Task<IReadOnlyList<BuildCommandHint>> BuildCommandsAsync(Guid projectId, StackComposition? composition, CancellationToken cancellationToken)
    {
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Component, t.Provider })
            .ToListAsync(cancellationToken);
        var hints = new List<BuildCommandHint>();
        foreach (var target in targets.OrderBy(t => t.Component))
        {
            var plan = DeploymentBuildRules.Derive(composition, target.Component, target.Provider, null);
            if (plan.Config.BuildCommand is { Length: > 0 } command)
            {
                var root = plan.RootDirectory is "" or "." ? string.Empty : $", in {plan.RootDirectory}";
                hints.Add(new BuildCommandHint($"{target.Component.ToWire()} ({DeploymentPlatforms.DisplayName(target.Provider)}{root})", command));
            }
        }

        return hints;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new JobFailedException($"The build fix collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Build fix job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fix plan for build check {BuildCheckId} was invalid ({ErrorCode}); retrying once with the reasons")]
    private static partial void LogRetry(ILogger logger, Guid buildCheckId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fix plan for build check {BuildCheckId} was still invalid after a retry; the check is exhausted: {Reason}")]
    private static partial void LogInvalid(ILogger logger, Guid buildCheckId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Build check {BuildCheckId}: the evidence of {FailureId} could not be attached to a fix step")]
    private static partial void LogEvidenceDropped(ILogger logger, Guid buildCheckId, string failureId);
}
