using Tevrin.Engine.Domain.Common;

namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-269, D-0.7.0-23: the SDLC's agent keys, which replace the <c>AgentKind</c> enum: the cognitive agents (§1, FR-23),
/// plus the Repository Analyst (FR-70), the Stack Analyst (FR-180) and the build fixer (FR-256). Each key is the 0.6.1 wire
/// and stored value, and names the agent's prompt (<c>Prompts/&lt;key&gt;.md</c>).
/// </summary>
public static class SdlcAgents
{
    public const string IdeaRefinement = "idea_refinement";

    public const string SpecGenerator = "spec_generator";

    public const string PlanGenerator = "plan_generator";

    public const string CodeGenerator = "code_generator";

    public const string CodeReview = "code_review";

    /// <summary>FR-70: summarises an existing codebase at a release version's base commit (<c>codebase_summary</c>).</summary>
    public const string RepositoryAnalyst = "repository_analyst";

    /// <summary>FR-180: infers a version's stack from its repository into a <c>stack_profile</c> draft (D-V5-7).</summary>
    public const string StackAnalyst = "stack_analyst";

    /// <summary>
    /// FR-256, §4.48: plans 1–5 fix steps after a failed build check (<c>build_fix</c>). It uses the model and credentials
    /// configured for <see cref="CodeReview"/> (D-0.6.1-17).
    /// </summary>
    public const string BuildFix = "build_fix";

    /// <summary>Every agent key, in the order of the 0.6.1 enum.</summary>
    public static IReadOnlyList<string> All { get; } =
        [IdeaRefinement, SpecGenerator, PlanGenerator, CodeGenerator, CodeReview, RepositoryAnalyst, StackAnalyst, BuildFix];

    /// <summary>Whether <paramref name="value"/> is an agent key (ordinal).</summary>
    public static bool IsKnown(string? value) => KeyNames.TryFind(All, value, out _);

    /// <summary>The agent key of a wire value, or <c>false</c> for anything else (ordinal).</summary>
    public static bool TryParse(string? value, out string agent) => KeyNames.TryFind(All, value, out agent);

    /// <summary>FR-274: the agent key of a configuration name: the key (<c>code_review</c>) or the 0.6.1 enum member name
    /// in any case (<c>CodeReview</c>), as <c>Llm:Providers:&lt;P&gt;:Models:&lt;agent&gt;</c> accepted.</summary>
    public static bool TryParseConfigName(string? value, out string agent) => KeyNames.TryParseName(All, value, out agent);

    /// <summary>The key of a wire value; throws <see cref="FormatException"/> for anything else (as parsing the 0.6.1
    /// enum did).</summary>
    public static string Parse(string value) =>
        TryParse(value, out var agent) ? agent : throw new FormatException($"'{value}' is not a valid AgentKind.");

    /// <summary>The position of an agent key in <see cref="All"/> (the 0.6.1 enum's numeric order), or -1.</summary>
    public static int Order(string agent) => KeyNames.IndexOf(All, agent);
}
