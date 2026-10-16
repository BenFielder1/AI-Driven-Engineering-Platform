using System.Globalization;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>A plan version that produced execution steps (FR-57): its id, type, version, status and creation time.</summary>
public sealed record RunPlan(Guid Id, ArtifactType Type, int Version, ArtifactStatus Status, DateTimeOffset CreatedAt);

/// <summary>
/// A compliance report as far as runs care: its id, cycle, creation time and the optimisation plan it drafted. Cycle
/// numbers restart after a rollback, so a release version can hold several reports of one cycle; runs link by report.
/// </summary>
public sealed record RunReport(Guid Id, int Cycle, DateTimeOffset CreatedAt, Guid? OptimizationPlanId);

/// <summary>Why a plan was invalidated (the <c>ARTIFACT_INVALIDATED</c> event): <c>cascade</c> with the approved artifact, or <c>rollback</c> with its target.</summary>
public sealed record PlanInvalidation(string? Cause, ArtifactType? ApprovedType, int? ApprovedVersion, int? TargetCheckpoint);

/// <summary>
/// FR-57: execution runs. A run is the steps of one plan version: the development run (an implementation plan) and one
/// review-fix run per review cycle that re-entered execution (that cycle's optimisation plan).
/// </summary>
public static class ExecutionRunRules
{
    /// <summary>
    /// Run order: development runs (implementation plans) first by plan version, then review-fix runs in the order their
    /// reports were written (cycle numbers alone repeat after a rollback), then plan version.
    /// </summary>
    public static IReadOnlyList<RunPlan> Order(IEnumerable<RunPlan> plans, Func<RunPlan, RunReport?> reportOf)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(reportOf);
        return
        [
            .. plans
                .OrderBy(p => p.Type == ArtifactType.ImplementationPlan ? 0 : 1)
                .ThenBy(p => p.Type == ArtifactType.ImplementationPlan ? DateTimeOffset.MinValue : reportOf(p)?.CreatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(p => p.Version),
        ];
    }

    /// <summary>
    /// The review report a plan belongs to: <c>null</c> for an implementation plan; for an optimisation plan, the report
    /// that drafted it, else (an edited later version of that draft) the latest report written at or before the plan was
    /// created; <c>null</c> when no report precedes it.
    /// </summary>
    public static RunReport? ReportOf(RunPlan plan, IReadOnlyCollection<RunReport> reports)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reports);
        if (plan.Type != ArtifactType.OptimizationPlan)
        {
            return null;
        }

        return reports.Where(r => r.OptimizationPlanId == plan.Id).OrderByDescending(r => r.CreatedAt).FirstOrDefault()
            ?? reports
                .Where(r => r.CreatedAt <= plan.CreatedAt)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .FirstOrDefault();
    }

    /// <summary>The review cycle a plan belongs to (that of <see cref="ReportOf"/>), or <c>null</c>.</summary>
    public static int? CycleOf(RunPlan plan, IReadOnlyCollection<RunReport> reports) => ReportOf(plan, reports)?.Cycle;

    /// <summary>
    /// FR-56: the review-fix run report <paramref name="reportId"/> produced: the latest version among the plans with steps
    /// that belong to that report (<see cref="ReportOf"/>), or <c>null</c>.
    /// </summary>
    public static RunPlan? RunOfReport(Guid reportId, IEnumerable<RunPlan> plansWithSteps, IReadOnlyCollection<RunReport> reports)
    {
        ArgumentNullException.ThrowIfNull(plansWithSteps);
        return plansWithSteps
            .Where(p => ReportOf(p, reports)?.Id == reportId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefault();
    }

    /// <summary>
    /// A run's status. <c>superseded</c> when its plan was invalidated (or every step was reset); otherwise over the steps
    /// still in force: <c>completed</c> when all are completed, <c>blocked</c> when one awaits an escalation decision,
    /// <c>pending</c> when none has started (and no job is about to start one), <c>paused</c> when the current run is paused
    /// or has stopped part-way with nothing queued, and <c>running</c> while the current run has a step in progress or
    /// an execution job queued or running. Only the current plan's run can be running or paused.
    /// </summary>
    public static PlanRunStatus Status(ArtifactStatus planStatus, IReadOnlyCollection<StepStatus> steps, bool isCurrentPlan, bool paused, bool jobActive)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var live = steps.Where(s => s != StepStatus.Invalidated).ToList();
        if (planStatus == ArtifactStatus.Invalidated || live.Count == 0)
        {
            return PlanRunStatus.Superseded;
        }

        if (ExecutionRules.AllCompleted(live))
        {
            return PlanRunStatus.Completed;
        }

        if (live.Contains(StepStatus.Blocked))
        {
            return PlanRunStatus.Blocked;
        }

        var active = isCurrentPlan && (jobActive || live.Contains(StepStatus.InProgress));
        if (live.All(s => s == StepStatus.Pending))
        {
            return active && !paused ? PlanRunStatus.Running : PlanRunStatus.Pending;
        }

        if (isCurrentPlan && paused)
        {
            return PlanRunStatus.Paused;
        }

        return active ? PlanRunStatus.Running : PlanRunStatus.Paused;
    }

    /// <summary>The "Superseded" reason shown for a run whose plan was invalidated.</summary>
    public static string SupersededReason(ArtifactType planType, int planVersion, PlanInvalidation? invalidation)
    {
        var plan = $"{planType.ToWire()} v{planVersion.ToString(CultureInfo.InvariantCulture)}";
        return invalidation switch
        {
            { Cause: "rollback", TargetCheckpoint: { } target } =>
                $"{plan} was invalidated by a rollback to checkpoint {target.ToString(CultureInfo.InvariantCulture)}.",
            { Cause: "rollback" } => $"{plan} was invalidated by a rollback.",
            { Cause: "cascade", ApprovedType: { } type, ApprovedVersion: { } version } =>
                $"{plan} was invalidated when {type.ToWire()} v{version.ToString(CultureInfo.InvariantCulture)} was approved.",
            _ => $"{plan} was invalidated.",
        };
    }
}

/// <summary>An attempt already recorded for a step (D-V2-14): its number and the agent run it used, if any.</summary>
public sealed record RecordedAttempt(int Attempt, Guid? AgentRunId);

/// <summary>Where a step attempt's outcome goes (D-V2-14): a new row with <see cref="Attempt"/>, or the existing row of that number.</summary>
public sealed record AttemptSlot(int Attempt, bool Existing);

/// <summary>D-V2-14: execution attempt history (<c>execution_step_attempt</c>).</summary>
public static class StepAttemptRules
{
    /// <summary>
    /// The history row an attempt's outcome is recorded in. Each attempt has its own agent run (the run id derives from the
    /// job and the attempt), so a row already holding <paramref name="agentRunId"/> is this same attempt redelivered (D17)
    /// and is updated rather than duplicated. Otherwise the attempt is new and numbered highest recorded + 1: numbers
    /// grow across the step's whole history, because an escalation <c>continue</c> resets the step's retry counter, never
    /// its history.
    /// </summary>
    public static AttemptSlot Slot(IReadOnlyCollection<RecordedAttempt> recorded, Guid? agentRunId)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        var same = agentRunId is { } runId ? recorded.FirstOrDefault(r => r.AgentRunId == runId) : null;
        return same is not null
            ? new AttemptSlot(same.Attempt, Existing: true)
            : new AttemptSlot((recorded.Count == 0 ? 0 : recorded.Max(r => r.Attempt)) + 1, Existing: false);
    }
}
