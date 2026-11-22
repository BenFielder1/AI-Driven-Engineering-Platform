using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-317: which context sections (<see cref="SdlcContextNames"/>) each SDLC agent's prompt gets, exactly as 0.6.1's job
/// handlers fill them. Pure: the host's <see cref="IAgentContextProvider"/>s read the values and call
/// <see cref="Sections"/>, so an agent never gets a value its handler didn't give it (e.g. refinement's
/// <c>{{PREVIOUS_SPECIFICATION}}</c> is the baseline spec, the spec generator's is a job value).
/// </summary>
public static class SdlcAgentContext
{
    private static readonly Dictionary<string, IReadOnlyList<string>> ByAgent = new(StringComparer.Ordinal)
    {
        [SdlcAgents.IdeaRefinement] =
        [
            SdlcContextNames.ProjectName, SdlcContextNames.Idea, SdlcContextNames.CodebaseSummary,
            SdlcContextNames.PreviousProjectRequest, SdlcContextNames.PreviousSpecification,
        ],
        [SdlcAgents.SpecGenerator] = [SdlcContextNames.SpecMode, SdlcContextNames.BaselineSpecification, SdlcContextNames.CodebaseSummary],
        [SdlcAgents.PlanGenerator] = [SdlcContextNames.MaxFilesPerStep, SdlcContextNames.CodebaseSummary],
        [SdlcAgents.CodeGenerator] = [SdlcContextNames.MaxFilesPerStep, SdlcContextNames.CodebaseSummary],
        [SdlcAgents.CodeReview] = [SdlcContextNames.MaxFilesPerStep, SdlcContextNames.CodebaseSummary],
        [SdlcAgents.RepositoryAnalyst] = [SdlcContextNames.ProjectName, SdlcContextNames.VersionRequest, SdlcContextNames.Base],
        [SdlcAgents.StackAnalyst] =
        [
            SdlcContextNames.ProjectName, SdlcContextNames.VersionRequest, SdlcContextNames.Base, SdlcContextNames.CodebaseSummary,
        ],
        [SdlcAgents.BuildFix] = [SdlcContextNames.MaxFilesPerStep],
    };

    /// <summary>The context names <paramref name="agent"/>'s prompt gets (empty for an unknown agent).</summary>
    public static IReadOnlyList<string> NamesFor(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return ByAgent.GetValueOrDefault(agent) ?? [];
    }

    /// <summary>
    /// Context names that are also stage input artifact keys, where the context wins: <c>codebase_summary</c> is the
    /// run version's summary as 0.6.1's handlers read it (locked, intact), and when that is none the
    /// placeholder stays empty (a known <c>null</c> gives an empty section, so the stage's input artifact never fills it).
    /// </summary>
    public static IReadOnlyList<string> OverridingInputs { get; } = [SdlcContextNames.CodebaseSummary];

    /// <summary>
    /// The sections of <paramref name="agent"/> among <paramref name="values"/> (context name → value), in
    /// <see cref="NamesFor"/> order. An absent name gives no section (another provider may supply it). A <c>null</c>
    /// value gives no section (its optional placeholder renders empty, a required one fails with <c>MISSING_INPUT</c>, as
    /// before), except for <see cref="OverridingInputs"/>, which give an empty one.
    /// </summary>
    public static IReadOnlyList<AgentContextSection> Sections(string agent, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sections = new List<AgentContextSection>();
        foreach (var name in NamesFor(agent))
        {
            if (!values.TryGetValue(name, out var value))
            {
                continue;
            }

            if (value is not null || OverridingInputs.Contains(name))
            {
                sections.Add(new AgentContextSection(name, value ?? string.Empty));
            }
        }

        return sections;
    }
}
