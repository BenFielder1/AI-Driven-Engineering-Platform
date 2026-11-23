using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Execution;
using Tevrin.Infrastructure.Persistence;

namespace Tevrin.Api.Features.BuildChecks;

/// <summary>A source a build check read (FR-255, FR-259): the preview rollout or a required CI check, its conclusion
/// (<c>success</c>, <c>failure</c>, <c>timed_out</c>, <c>unavailable</c>, …; <c>null</c> while pending) and a link to it.
/// D-0.6.1-23: <c>status</c> is where the source stands (a preview's deployment state <c>queued</c>, <c>building</c>,
/// <c>ready</c>, <c>error</c>, <c>canceled</c>, or a check's <c>queued</c>, <c>in_progress</c>, <c>completed</c>; <c>null</c>
/// when not started or not known), <c>message</c> explains an <c>unavailable</c> or informational source, and
/// <c>informational</c> is <c>true</c> for a source shown for information only (branch rules unreadable): never waited for,
/// never a failure.</summary>
public sealed record BuildCheckSourceDto(string Name, BuildCheckSourceKind Kind, string? Conclusion, string? Url, string? Status, string? Message, bool Informational);

/// <summary>A failure a build check found (FR-256, FR-259): the failure id (as in review findings), its source
/// (<c>preview</c> or <c>check</c>), the message and the redacted log excerpt (§4.38; <c>null</c> when there is none).</summary>
public sealed record BuildCheckFailureDto(string FailureId, string Source, string Message, string? Excerpt);

/// <summary>
/// One build check of a release version (FR-254–FR-259, §4.47): its review cycle and attempt (0 checks the plan's commit,
/// k the commit after the k-th fix), the head commit, the status (<c>waiting</c>, <c>passed</c>, <c>failed</c>,
/// <c>skipped</c>, <c>exhausted</c>, <c>cancelled</c>), the sources read with their results, the failures, the step numbers
/// of the build-fix steps planned from it, and when it started and completed. D-0.6.1-23: <c>endReason</c> says why an
/// exhausted or cancelled check ended (<c>null</c> otherwise), and <c>fixPlanning</c> where its fix stands (<c>none</c>,
/// <c>planning</c>, <c>planned</c>, <c>failed</c>).
/// </summary>
public sealed record BuildCheckDto(
    Guid Id,
    Guid ReleaseVersionId,
    int ReviewCycle,
    int Attempt,
    string HeadSha,
    BuildCheckStatus Status,
    IReadOnlyList<BuildCheckSourceDto> Sources,
    IReadOnlyList<BuildCheckFailureDto> Failures,
    IReadOnlyList<int> FixStepNumbers,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    BuildCheckEndReason? EndReason,
    BuildCheckFixPlanning FixPlanning);

/// <summary>
/// FR-259: the live <c>buildCheckUpdated</c> message, pushed on the project's live channel (the execution event stream)
/// when a build check starts, changes state, has fix steps planned, or (D-0.6.1-23) while waiting its sources change or its
/// fix planning starts: the project, and the check as the list endpoint
/// returns it. Not an HTTP response: the schema is added to the OpenAPI components for the generated client.
/// </summary>
public sealed record BuildCheckUpdatedMessage(Guid ProjectId, BuildCheckDto Check);

/// <summary>Reads build checks as <see cref="BuildCheckDto"/> (FR-259).</summary>
public static class BuildCheckReader
{
    /// <summary>The version's build checks, newest first (by start; checks superseded by a rollback stay as history).</summary>
    public static async Task<IReadOnlyList<BuildCheckDto>> ListAsync(TevrinDbContext db, Guid projectId, Guid releaseVersionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var checks = await db.BuildChecks.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.ReleaseVersionId == releaseVersionId)
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Attempt)
            .ToListAsync(cancellationToken);
        if (checks.Count == 0)
        {
            return [];
        }

        var ids = checks.Select(c => c.Id).ToList();
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.BuildCheckId != null && ids.Contains(s.BuildCheckId.Value) && s.Status != StepStatus.Invalidated)
            .Select(s => new { s.BuildCheckId, s.StepNumber })
            .ToListAsync(cancellationToken);
        var byCheck = steps.GroupBy(s => s.BuildCheckId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(s => s.StepNumber).Order()]);
        return [.. checks.Select(c => ToDto(c, byCheck.GetValueOrDefault(c.Id) ?? []))];
    }

    /// <summary>One check with its fix step numbers.</summary>
    public static async Task<BuildCheckDto> ReadAsync(TevrinDbContext db, BuildCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(check);
        var numbers = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.BuildCheckId == check.Id && s.Status != StepStatus.Invalidated)
            .Select(s => s.StepNumber)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);
        var pending = db.ChangeTracker.Entries<ExecutionStep>()
            .Where(e => e.State == EntityState.Added && e.Entity.BuildCheckId == check.Id)
            .Select(e => e.Entity.StepNumber);
        return ToDto(check, [.. numbers.Concat(pending).Distinct().Order()]);
    }

    public static BuildCheckDto ToDto(BuildCheck check, IReadOnlyList<int> fixStepNumbers)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new BuildCheckDto(
            check.Id,
            check.ReleaseVersionId,
            check.ReviewCycle,
            check.Attempt,
            check.HeadSha,
            check.Status,
            [.. check.Sources.Select(s => new BuildCheckSourceDto(s.Name, s.Kind, s.Conclusion, s.Url, s.Status, s.Message, s.Informational))],
            [.. check.Failures.Select(f => new BuildCheckFailureDto(f.FailureId, f.Source, f.Message, f.Excerpt))],
            fixStepNumbers,
            check.StartedAt,
            check.CompletedAt,
            check.EndReason,
            BuildCheckRules.FixPlanningOf(check.Status, check.EndReason, fixStepNumbers.Count > 0, check.Failures.Count > 0));
    }
}
