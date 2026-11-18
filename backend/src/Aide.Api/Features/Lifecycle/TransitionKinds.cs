using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Entities;

namespace Aide.Api.Features.Lifecycle;

/// <summary>
/// §4.51, FR-294: the generic kind stored in <c>state_transitions.kind</c> for each SDLC transition kind (D-0.7.0-28). A new
/// version's first transition is <see cref="PipelineTransitionKinds.Start"/>, like a new project's creation record: it is
/// the first transition of the version's new run, not a move within one.
/// </summary>
public static class TransitionKinds
{
    public static string Of(TransitionKind kind) => kind switch
    {
        TransitionKind.Advance => PipelineTransitionKinds.Advance,
        TransitionKind.ReviewCycle => PipelineTransitionKinds.Loop,
        TransitionKind.Rollback => PipelineTransitionKinds.Rollback,
        TransitionKind.InvalidationFallback => PipelineTransitionKinds.Fallback,
        TransitionKind.ReleaseNoop => PipelineTransitionKinds.Noop,
        TransitionKind.ReleaseCompleted or TransitionKind.ReleaseAwaitingMerge or TransitionKind.ReleaseMergeAbandoned => PipelineTransitionKinds.StatusChange,
        TransitionKind.NewVersion => PipelineTransitionKinds.Start,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transition kind."),
    };
}
