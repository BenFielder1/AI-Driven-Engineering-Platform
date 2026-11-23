using Tevrin.Domain.Artifacts;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.UnitTests.Lifecycle;

/// <summary>
/// FR-292, FR-293 (rule 8): the SDLC lifecycle as the app runs it, the engine with the shipped SDLC definition
/// (<see cref="SdlcMachine"/>, <see cref="SdlcStages"/>), for the transition tests. The review loop sits below its cap, as
/// the review decision rules keep it (D-0.7.0-30).
/// </summary>
internal static class Sdlc
{
    public static ReviewLoopState Loop { get; } = new(1, 3);

    public static PipelineDefinition Definition => SdlcPipelineFixture.Definition;

    public static IReadOnlyList<Phase> All => SdlcStages.All(Definition);

    public static ProjectStatus StatusFor(Phase phase) => SdlcStages.StatusFor(Definition, phase);

    public static Phase? Next(Phase phase) => SdlcStages.Next(Definition, phase);

    public static bool IsBefore(Phase phase, Phase other) => SdlcStages.IsBefore(Definition, phase, other);

    public static string? RequiredGate(Tevrin.Engine.Domain.Gates.ApprovalMode mode, Phase entering) => SdlcStages.RequiredGate(Definition, mode, entering);

    public static Result<PhaseTransition> Apply(LifecycleState state, Transition move, LifecycleSnapshot snapshot) =>
        SdlcMachine.Apply(state, move, snapshot, Loop, Definition);

    public static Result<AdvanceOutcome> RequestAdvance(LifecycleState state, LifecycleSnapshot snapshot, string? reason = null) =>
        SdlcMachine.RequestAdvance(state, snapshot, Loop, Definition, reason);

    public static IReadOnlyList<string> UnmetEntryConditions(LifecycleState state, Phase entering, LifecycleSnapshot snapshot, bool includeGate = true) =>
        SdlcMachine.UnmetEntryConditions(state, entering, snapshot, Loop, Definition, includeGate);

    public static Result<LifecycleState> Start(bool infersStack, bool baseHasCode) => SdlcMachine.Start(infersStack, baseHasCode, Definition);

    /// <summary>A new release version (a new run) entering <paramref name="entering"/>.</summary>
    public static Result<PhaseTransition> NewVersion(LifecycleState state, LifecycleSnapshot snapshot, Phase entering = Phase.Refinement, string? reason = null) =>
        SdlcMachine.NewVersion(state, entering, snapshot, Definition, reason);

    public static InvalidationEffect Cascade(string approved, Phase currentPhase) => SdlcMachine.CascadeEffect(approved, currentPhase, Definition);
}
