namespace Tevrin.Pack.Coach;

/// <summary>FR-339: the artifact keys of <c>impulse-coach.pipeline.yaml</c>.</summary>
public static class CoachArtifacts
{
    /// <summary>The project-scope source of truth (FR-352), pinned at run start.</summary>
    public const string AthleteProfile = "athlete_profile";

    /// <summary>The run's input (the webhook trigger's payload).</summary>
    public const string SessionLog = "session_log";

    public const string Performance = "performance";

    public const string PlanDraft = "plan_draft";

    public const string CoachMessage = "coach_message";

    public static IReadOnlyList<string> All { get; } = [AthleteProfile, SessionLog, Performance, PlanDraft, CoachMessage];

    /// <summary>The JSON Schema file each artifact names.</summary>
    public static string SchemaOf(string artifactKey) => artifactKey + ".schema.json";
}
