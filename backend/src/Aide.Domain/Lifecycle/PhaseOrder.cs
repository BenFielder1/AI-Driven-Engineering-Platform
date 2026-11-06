namespace Aide.Domain.Lifecycle;

/// <summary>Phase ordering and the status-follows-phase table (§4.1; 0.5.0 §4.27 puts <c>analysis</c> first).</summary>
public static class PhaseOrder
{
    /// <summary>Every phase in order. §4.27: <c>analysis</c> comes first and is only entered by a version that infers its stack.</summary>
    public static IReadOnlyList<Phase> All { get; } =
        [Phase.Analysis, Phase.Refinement, Phase.Specification, Phase.Planning, Phase.Execution, Phase.Review, Phase.Release];

    /// <summary>The next phase, or <c>null</c> for <see cref="Phase.Release"/>. Nothing advances into <c>analysis</c>: it is first.</summary>
    public static Phase? Next(Phase phase) => phase == Phase.Release ? null : phase + 1;

    /// <summary>§4.27: <c>analysis</c> sits before <c>refinement</c> in every ordering comparison.</summary>
    public static bool IsBefore(this Phase phase, Phase other) => phase < other;

    /// <summary>Status for a phase entered by a forward move: analysis/refinement/specification/planning → active, etc.</summary>
    public static ProjectStatus StatusFor(Phase phase) => phase switch
    {
        Phase.Analysis or Phase.Refinement or Phase.Specification or Phase.Planning => ProjectStatus.Active,
        Phase.Execution => ProjectStatus.Building,
        Phase.Review => ProjectStatus.Reviewing,
        Phase.Release => ProjectStatus.Releasing,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
