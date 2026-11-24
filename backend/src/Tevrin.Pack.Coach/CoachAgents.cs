namespace Tevrin.Pack.Coach;

/// <summary>FR-339: the agent keys of <c>impulse-coach.pipeline.yaml</c>.</summary>
public static class CoachAgents
{
    public const string PerformanceAnalyst = "performance-analyst";

    public const string ProgrammePlanner = "programme-planner";

    public const string CoachVoice = "coach-voice";

    public static IReadOnlyList<string> All { get; } = [PerformanceAnalyst, ProgrammePlanner, CoachVoice];
}
