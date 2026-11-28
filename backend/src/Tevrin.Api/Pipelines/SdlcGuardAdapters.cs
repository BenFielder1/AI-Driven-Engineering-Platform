using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Pipelines;

/// <summary>
/// FR-290, D-0.7.0-23: an SDLC guard as an engine <see cref="IGuard"/>. It reads <see cref="SdlcLifecycleFacts"/> from the
/// context's fact bag and returns <see cref="SdlcGuards"/>' reasons. Each guard name is bound to its own type
/// (<c>AddPipelineGuard&lt;T&gt;(name)</c> maps a name to a type). A context without the fact is a programming error.
/// </summary>
public abstract class SdlcGuardAdapter(string name) : IGuard
{
    /// <summary>The guard name (<see cref="SdlcGuardNames"/>).</summary>
    public string Name { get; } = name;

    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var facts = context.Facts.Get<SdlcLifecycleFacts>()
            ?? throw new InvalidOperationException($"Guard '{Name}' needs {nameof(SdlcLifecycleFacts)} in the guard context's facts.");
        var reasons = SdlcGuards.Evaluate(Name, facts.State, facts.Snapshot);
        return Task.FromResult(reasons.Count == 0 ? GuardResult.Pass : new GuardResult(false, reasons));
    }
}

/// <summary><see cref="SdlcGuardNames.StartOnly"/>.</summary>
public sealed class StartOnlyGuard() : SdlcGuardAdapter(SdlcGuardNames.StartOnly);

/// <summary><see cref="SdlcGuardNames.LeavingAnalysis"/>.</summary>
public sealed class LeavingAnalysisGuard() : SdlcGuardAdapter(SdlcGuardNames.LeavingAnalysis);

/// <summary><see cref="SdlcGuardNames.PlanHasSteps"/>.</summary>
public sealed class PlanHasStepsGuard() : SdlcGuardAdapter(SdlcGuardNames.PlanHasSteps);

/// <summary><see cref="SdlcGuardNames.WorkingCopyPrepared"/>.</summary>
public sealed class WorkingCopyPreparedGuard() : SdlcGuardAdapter(SdlcGuardNames.WorkingCopyPrepared);

/// <summary><see cref="SdlcGuardNames.PlanStepsCompleted"/>.</summary>
public sealed class PlanStepsCompletedGuard() : SdlcGuardAdapter(SdlcGuardNames.PlanStepsCompleted);

/// <summary><see cref="SdlcGuardNames.ReleaseEligible"/>.</summary>
public sealed class ReleaseEligibleGuard() : SdlcGuardAdapter(SdlcGuardNames.ReleaseEligible);

/// <summary><see cref="SdlcGuardNames.ReviewCycleReady"/>.</summary>
public sealed class ReviewCycleReadyGuard() : SdlcGuardAdapter(SdlcGuardNames.ReviewCycleReady);
