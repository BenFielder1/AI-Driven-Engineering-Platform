using Tevrin.Engine.Domain.Common;

namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-270, D-0.7.0-23: the SDLC's artifact keys, which replace the <c>ArtifactType</c> enum. Each key is the 0.6.1 wire and
/// stored value.
/// </summary>
public static class SdlcArtifacts
{
    public const string ProjectRequest = "project_request";

    public const string TechnicalSpecification = "technical_specification";

    public const string ImplementationPlan = "implementation_plan";

    public const string OptimizationPlan = "optimization_plan";

    /// <summary>FR-70: the Repository Analyst's summary of the codebase at the version's base commit.</summary>
    public const string CodebaseSummary = "codebase_summary";

    /// <summary>FR-170: the version's active tracker imports rendered to Markdown (§4.26); auto-locked, invalidates nothing.</summary>
    public const string SourceItems = "source_items";

    /// <summary>
    /// FR-180, §4.27: the Stack Analyst's findings for a version that infers its stack (layers, hosts, structure), edited,
    /// approved or rejected on the Analysis page. Approving it pins the composition.
    /// </summary>
    public const string StackProfile = "stack_profile";

    /// <summary>Every artifact key, in the order of the 0.6.1 enum.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ProjectRequest, TechnicalSpecification, ImplementationPlan, OptimizationPlan, CodebaseSummary, SourceItems, StackProfile];

    /// <summary>Whether <paramref name="value"/> is an artifact key (ordinal).</summary>
    public static bool IsKnown(string? value) => KeyNames.TryFind(All, value, out _);

    /// <summary>The artifact key of a wire value, or <c>false</c> for anything else (ordinal).</summary>
    public static bool TryParse(string? value, out string type) => KeyNames.TryFind(All, value, out type);

    /// <summary>The key of a wire value; throws <see cref="FormatException"/> for anything else (as parsing the 0.6.1
    /// enum did).</summary>
    public static string Parse(string value) =>
        TryParse(value, out var type) ? type : throw new FormatException($"'{value}' is not a valid ArtifactType.");

    /// <summary>The position of an artifact key in <see cref="All"/> (the 0.6.1 enum's numeric order), or -1.</summary>
    public static int Order(string type) => KeyNames.IndexOf(All, type);
}
