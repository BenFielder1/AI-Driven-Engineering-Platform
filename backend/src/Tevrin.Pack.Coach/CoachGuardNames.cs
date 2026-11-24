namespace Tevrin.Pack.Coach;

/// <summary>FR-340: the guard names the coach pack registers (namespaced by <see cref="CoachPack.PackId"/>).</summary>
public static class CoachGuardNames
{
    /// <summary>§4.63: the Velocity load rules R1–R5, checked on the programme planner's <c>plan_draft</c>.</summary>
    public const string VelocityLoadRules = "coach.velocity-load-rules";

    public static IReadOnlyList<string> All { get; } = [VelocityLoadRules];
}
