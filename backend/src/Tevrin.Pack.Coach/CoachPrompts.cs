namespace Tevrin.Pack.Coach;

/// <summary>FR-339: the prompt template names the definition's agent stages use (one per agent; embedded as
/// <c>Prompts/&lt;name&gt;.md</c>).</summary>
public static class CoachPrompts
{
    public const string PerformanceAnalyst = "coach.performance-analyst";

    public const string ProgrammePlanner = "coach.programme-planner";

    public const string CoachVoice = "coach.coach-voice";

    public static IReadOnlyList<string> All { get; } = [PerformanceAnalyst, ProgrammePlanner, CoachVoice];
}
