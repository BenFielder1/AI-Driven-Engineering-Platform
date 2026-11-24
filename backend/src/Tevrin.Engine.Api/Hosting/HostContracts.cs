using Microsoft.AspNetCore.Http.HttpResults;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Api.Hosting;

/// <summary>
/// D-0.7.0-11, §2: projects and their permissions belong to the host. The engine endpoints check every call through
/// this (FR-299–FR-303).
/// </summary>
public interface IRunAccessPolicy
{
    /// <summary><c>null</c> when the caller may view <paramref name="projectId"/>; else the error to return
    /// (<c>NOT_FOUND</c> for a project they can't see, as the project endpoints do).</summary>
    Task<DomainError?> CheckViewAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>The ids of the projects the caller can see, as a query the engine composes into its own (filtered in
    /// SQL, §8). Empty for an anonymous caller.</summary>
    Task<IQueryable<Guid>> VisibleProjectIdsAsync(CancellationToken cancellationToken);

    /// <summary>Display names of <paramref name="projectIds"/> (for the gate inbox).</summary>
    Task<IReadOnlyDictionary<Guid, string>> ProjectNamesAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken);
}

/// <summary>An approve or reject request through the engine endpoints; the same fields as the project gate endpoints.</summary>
public sealed record PipelineGateDecision(string? Comment, EscalationDecision? Decision, int? TargetCheckpoint);

/// <summary>
/// FR-301: gate decisions go through the host's gate service, with its rules, errors, follow-ups and audit events,
/// exactly as <c>/api/projects/{id}/gates/{gateId}/…</c>.
/// </summary>
public interface IGateDecisionService
{
    /// <summary><c>null</c> when the caller may decide gates on <paramref name="projectId"/>, else the error
    /// (<c>FORBIDDEN</c>).</summary>
    Task<DomainError?> CheckDecideAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>FR-302, FR-307: which of <paramref name="projectIds"/> the caller may decide gates on, by the same rule as
    /// <see cref="CheckDecideAsync"/>, in one query.</summary>
    Task<IReadOnlySet<Guid>> DecidableProjectsAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken);

    Task<Result<ApprovalGate>> ApproveAsync(Guid projectId, Guid gateId, PipelineGateDecision decision, CancellationToken cancellationToken);

    Task<Result<ApprovalGate>> RejectAsync(Guid projectId, Guid gateId, PipelineGateDecision decision, CancellationToken cancellationToken);
}

/// <summary>Which transitions to read: those at exactly <see cref="At"/>, or the first <see cref="Take"/> after
/// <see cref="After"/> (all from the start when both are <c>null</c>), in (time, id) order.</summary>
public sealed record TimelineWindow(DateTimeOffset? At, DateTimeOffset? After, int? Take)
{
    public static TimelineWindow All { get; } = new(null, null, null);
}

/// <summary>A run transition (<c>state_transitions</c> is a host table; its stage columns hold stage ids).</summary>
public sealed record PipelineTransitionRow(
    Guid Id,
    DateTimeOffset At,
    string? FromStage,
    string ToStage,
    string? Kind,
    int Checkpoint,
    string? Reason);

/// <summary>FR-300: the run's transitions, read by the host.</summary>
public interface IPipelineTransitionSource
{
    Task<IReadOnlyList<PipelineTransitionRow>> ListAsync(Guid pipelineRunId, TimelineWindow window, CancellationToken cancellationToken);
}

/// <summary>The host's ProblemDetails mapping (status from its error table, <c>errorCode</c> extension; CLAUDE.md rule 4).</summary>
public interface IEngineProblems
{
    ProblemHttpResult FromError(DomainError problem);
}

/// <summary>
/// §4.57: which of the host's jobs make a run <c>running</c>. A queued or running job of the run's project counts unless
/// its type is in <see cref="IgnoredJobTypes"/> (e.g. the SDLC's notification jobs, which report what already happened).
/// </summary>
public interface IRunActivityPolicy
{
    IReadOnlyCollection<string> IgnoredJobTypes { get; }
}

/// <summary>The default: every job of the project counts.</summary>
internal sealed class AllJobsCount : IRunActivityPolicy
{
    public IReadOnlyCollection<string> IgnoredJobTypes { get; } = [];
}

/// <summary>FR-331, FR-333, FR-335: what a run-control call does, for the host's permission check.</summary>
public enum RunControlAction
{
    /// <summary>Start a run (Contributor).</summary>
    Start = 0,

    /// <summary>Retry a failed stage (Contributor).</summary>
    Retry = 1,

    /// <summary>Submit an input (Contributor).</summary>
    SubmitInput = 2,

    /// <summary>Cancel a run (Owner).</summary>
    Cancel = 3,

    /// <summary>Roll a run back (Owner).</summary>
    Rollback = 4,
}

/// <summary>
/// FR-331–FR-335: the host's side of run control: its permission matrix (404 for a project the caller can't see, 403
/// below the role), the caller and the project's approval mode.
/// </summary>
public interface IRunControlAccess
{
    /// <summary><c>null</c> when the caller may do <paramref name="action"/> on <paramref name="projectId"/>; else the error.</summary>
    Task<DomainError?> CheckAsync(Guid projectId, RunControlAction action, CancellationToken cancellationToken);

    /// <summary>The project's approval mode (a new run's default, FR-331).</summary>
    Task<ApprovalMode> ProjectApprovalModeAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>The signed-in caller's user id, or <c>null</c>.</summary>
    Guid? UserId { get; }
}
