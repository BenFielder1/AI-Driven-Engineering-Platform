namespace Aide.Domain.Lifecycle;

/// <summary>Phase ordering and the status-follows-phase table (§4.1).</summary>
public static class PhaseOrder
{
    public static IReadOnlyList<Phase> All { get; } =
        [Phase.Refinement, Phase.Specification, Phase.Planning, Phase.Execution, Phase.Review, Phase.Release];

    /// <summary>The next phase, or <c>null</c> for <see cref="Phase.Release"/>.</summary>
    public static Phase? Next(Phase phase) => phase == Phase.Release ? null : phase + 1;

    public static bool IsBefore(this Phase phase, Phase other) => phase < other;

    /// <summary>Status for a phase entered by a forward move: refinement/specification/planning → active, etc.</summary>
    public static ProjectStatus StatusFor(Phase phase) => phase switch
    {
        Phase.Refinement or Phase.Specification or Phase.Planning => ProjectStatus.Active,
        Phase.Execution => ProjectStatus.Building,
        Phase.Review => ProjectStatus.Reviewing,
        Phase.Release => ProjectStatus.Releasing,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
