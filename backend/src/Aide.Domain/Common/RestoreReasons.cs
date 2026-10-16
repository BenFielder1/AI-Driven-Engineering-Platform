namespace Aide.Domain.Common;

/// <summary>FR-58: why an artifact version can't be restored (<see cref="ErrorCodes.RestoreNotAllowed"/>, ProblemDetails <c>reason</c>).</summary>
public static class RestoreReasons
{
    /// <summary>The project has left the phase that produces the type (§4.12).</summary>
    public const string StageCompleted = "stage_completed";

    /// <summary>The project hasn't reached the phase that produces the type yet (e.g. it was rolled back before it).</summary>
    public const string StageNotReached = "stage_not_reached";

    /// <summary>The version belongs to another release version, or the selected release version isn't in development.</summary>
    public const string OtherReleaseVersion = "other_release_version";

    /// <summary>The chosen version's content no longer matches its hash (FR-20).</summary>
    public const string Corrupted = "corrupted";

    /// <summary>The project is releasing.</summary>
    public const string Releasing = "releasing";

    public static IReadOnlyList<string> All { get; } = [StageCompleted, StageNotReached, OtherReleaseVersion, Corrupted, Releasing];
}
