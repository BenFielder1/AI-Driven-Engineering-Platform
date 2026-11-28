using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Pack.Coach;

/// <summary>FR-339: the coach's gate keys: the plan approval, and the engine's escalation (after the Velocity rules
/// still fail on the last attempt).</summary>
public static class CoachGates
{
    public const string PlanApproval = "plan_approval";

    public static IReadOnlyList<string> All { get; } = [PlanApproval, EngineGates.Escalation];
}
