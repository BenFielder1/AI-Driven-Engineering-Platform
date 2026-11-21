using Aide.Api.Agents;
using Aide.Infrastructure.Agents;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-317: each SDLC agent's prompt rendered through the engine's prompt path (<see cref="AgentStagePrompt.Values"/>: the
/// stage's input artifacts, then the SDLC context sections of <see cref="SdlcAgentContext"/>, then the host's prompt
/// values) equals its golden file. Every placeholder has a source; where two sources meet (<see cref="ExpectedOverlaps"/>)
/// the render shows which wins. <see cref="HostValues"/> pins the ones the
/// engine path can't fill yet, which the SDLC extension passes as <see cref="AgentStageRequest.PromptValues"/> when the
/// agents move (M62c, M62d).
/// </summary>
public class SdlcContextPromptTests
{
    /// <summary>
    /// Placeholders filled by the host (job values: the payload, the stack guidance, step and review material, the feedback
    /// as <c>USER_FEEDBACK</c>). <c>IMPLEMENTATION_PLAN</c> of review and code generation is not the artifact itself (the
    /// plan plus the optimisation plan, the plan with step statuses), so it is a host value too.
    /// </summary>
    private static readonly Dictionary<string, string[]> HostValues = new(StringComparer.Ordinal)
    {
        [SdlcAgents.IdeaRefinement] = ["STACK_GUIDANCE", "CURRENT_DRAFT", "ANSWERS", "STACK_DETECTION", "USER_FEEDBACK"],
        [SdlcAgents.SpecGenerator] = ["STACK_GUIDANCE", "PREVIOUS_SPECIFICATION", "USER_FEEDBACK"],
        [SdlcAgents.PlanGenerator] = ["STACK_GUIDANCE", "PREVIOUS_PLAN", "USER_FEEDBACK"],
        [SdlcAgents.CodeGenerator] =
        [
            "STEP_NUMBER", "PROJECT_REQUEST", "TECHNICAL_SPECIFICATION", "IMPLEMENTATION_PLAN", "PROJECT_RULES", "STEP_TITLE",
            "STEP_TASK", "DECLARED_FILES", "STEP_USER_INSTRUCTIONS", "EXISTING_CODE", "USER_FEEDBACK",
        ],
        [SdlcAgents.CodeReview] =
        [
            "STACK_GUIDANCE", "REVIEW_CYCLE", "IMPLEMENTATION_PLAN", "DETERMINISTIC_FINDINGS", "PREVIOUS_REVIEW", "CODE", "USER_FEEDBACK",
        ],
        [SdlcAgents.RepositoryAnalyst] = ["STACK_GUIDANCE", "STACK_DETECTION", "USER_FEEDBACK"],
        [SdlcAgents.StackAnalyst] = ["CATALOG_OPTIONS", "STACK_DETECTION", "DETECTOR_PROFILE", "PREVIOUS_PROFILE", "USER_FEEDBACK"],
        [SdlcAgents.BuildFix] = ["STACK_GUIDANCE", "FAILURES", "ERROR_SUMMARY", "PLAN_STEPS", "REPO_FILES", "USER_FEEDBACK"],
    };

    public static TheoryData<string> Agents() => [.. SdlcAgents.All];

    [Theory]
    [MemberData(nameof(Agents))]
    public void FR317_prompt_through_the_engine_path_equals_the_golden_file(string agent)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Agents", "Golden", $"{agent}.txt");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        var actual = GoldenPromptTests.Render(agent, (template, seeded) => EngineValues(agent, template, seeded));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Placeholders with two sources: a stage input artifact and the SDLC context (<c>CODEBASE_SUMMARY</c>: the context
    /// wins, <see cref="SdlcAgentContext.OverridingInputs"/>) or the host (review's <c>IMPLEMENTATION_PLAN</c>: the host's
    /// prompt value wins, as values after the inputs).
    /// </summary>
    private static readonly Dictionary<string, string[]> ExpectedOverlaps = new(StringComparer.Ordinal)
    {
        [SdlcAgents.IdeaRefinement] = ["CODEBASE_SUMMARY"],
        [SdlcAgents.SpecGenerator] = ["CODEBASE_SUMMARY"],
        [SdlcAgents.PlanGenerator] = ["CODEBASE_SUMMARY"],
        [SdlcAgents.CodeReview] = ["CODEBASE_SUMMARY", "IMPLEMENTATION_PLAN"],
        [SdlcAgents.StackAnalyst] = ["CODEBASE_SUMMARY"],
        [SdlcAgents.CodeGenerator] = [],
        [SdlcAgents.RepositoryAnalyst] = [],
        [SdlcAgents.BuildFix] = [],
    };

    [Theory]
    [MemberData(nameof(Agents))]
    public void FR317_every_placeholder_has_a_source_and_the_overlaps_are_the_known_ones(string agent)
    {
        var template = new EmbeddedPromptLibrary().Template(agent);
        var names = template.Placeholders.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var (inputs, context, host) = Sources(agent, template);

        Assert.Equal(names.Order(StringComparer.Ordinal), inputs.Union(context).Union(host).Order(StringComparer.Ordinal));
        Assert.Empty(context.Intersect(host));
        Assert.Equal(ExpectedOverlaps[agent].Order(StringComparer.Ordinal), inputs.Intersect(context.Union(host)).Order(StringComparer.Ordinal));
        Assert.Subset(
            SdlcAgentContext.OverridingInputs.Select(AgentStagePrompt.PlaceholderOf).ToHashSet(StringComparer.Ordinal),
            inputs.Intersect(context).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void FR317_the_SDLC_context_fills_the_codebase_summary_of_every_agent_whose_prompt_has_it()
    {
        foreach (var agent in SdlcAgents.All)
        {
            var template = new EmbeddedPromptLibrary().Template(agent);
            var hasSummary = template.Placeholders.Any(p => p.Name == "CODEBASE_SUMMARY");
            Assert.Equal(hasSummary, SdlcAgentContext.NamesFor(agent).Contains(SdlcContextNames.CodebaseSummary));
        }
    }

    [Theory]
    [InlineData(SdlcAgents.IdeaRefinement)]
    [InlineData(SdlcAgents.SpecGenerator)]
    [InlineData(SdlcAgents.PlanGenerator)]
    [InlineData(SdlcAgents.CodeReview)]
    [InlineData(SdlcAgents.StackAnalyst)]
    public void FR317_without_a_summary_the_stage_input_never_fills_the_placeholder(string agent)
    {
        // 0.6.1: no summary for the version (or no version: IterationContext.None) renders {{CODEBASE_SUMMARY?}} empty.
        var template = new EmbeddedPromptLibrary().Template(agent);
        var required = template.RequiredInputs.ToDictionary(n => n, n => (string?)$"value of {n}", StringComparer.Ordinal);
        var sections = SdlcAgentContext.Sections(agent, SdlcContextValues.FromIteration(IterationContext.None));
        var stale = new Artifact { Type = SdlcArtifacts.CodebaseSummary, Content = "A stale summary" };

        var values = AgentStagePrompt.Values([stale], sections, feedback: null);
        foreach (var (name, value) in required.Where(r => r.Key != "CODEBASE_SUMMARY" && r.Key != "SPEC_MODE"))
        {
            values[name] = value;
        }

        var expected = new Dictionary<string, string?>(values, StringComparer.Ordinal);
        expected.Remove("CODEBASE_SUMMARY");
        var rendered = template.Render(values);
        Assert.True(rendered.IsSuccess, rendered.Error?.Message);
        Assert.Equal(template.Render(expected).Value, rendered.Value);
        Assert.DoesNotContain("A stale summary", rendered.Value.User, StringComparison.Ordinal);
        Assert.Equal(string.Empty, sections.Single(s => s.Name == SdlcContextNames.CodebaseSummary).Content);
    }

    /// <summary>
    /// The seeded values rebuilt as the engine would: every stage input artifact (one that another source must override
    /// exists with stale content, so a wrong precedence changes the render), then the context sections (every SDLC context
    /// value, absent ones <c>null</c>, as the providers read them), then the host's prompt values (all of its keys).
    /// </summary>
    private static Dictionary<string, string?> EngineValues(string agent, PromptTemplate template, Dictionary<string, string?> seeded)
    {
        var (_, context, host) = Sources(agent, template);
        var names = template.Placeholders.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var artifacts = new List<Artifact>();
        foreach (var key in StageInputs(agent))
        {
            var placeholder = AgentStagePrompt.PlaceholderOf(key);
            if (names.Contains(placeholder) && (context.Contains(placeholder) || host.Contains(placeholder)))
            {
                artifacts.Add(new Artifact { Type = key, Content = $"Stale {key} artifact" });
            }
            else if (seeded.TryGetValue(placeholder, out var value))
            {
                artifacts.Add(new Artifact { Type = key, Content = value! });
            }
        }

        var contextValues = SdlcContextNames.All.ToDictionary(
            name => name,
            name => seeded.GetValueOrDefault(AgentStagePrompt.PlaceholderOf(name)),
            StringComparer.Ordinal);
        var sections = SdlcAgentContext.Sections(agent, contextValues);

        var values = AgentStagePrompt.Values(artifacts, sections, feedback: null);
        foreach (var name in host)
        {
            values[name] = seeded.GetValueOrDefault(name);
        }

        return values;
    }

    /// <summary>The placeholders filled by the stage's raw inputs, by SDLC context, and by the host (they may overlap).</summary>
    private static (HashSet<string> Inputs, HashSet<string> Context, HashSet<string> Host) Sources(string agent, PromptTemplate template)
    {
        var names = template.Placeholders.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var inputs = StageInputs(agent).Select(AgentStagePrompt.PlaceholderOf).Where(names.Contains).ToHashSet(StringComparer.Ordinal);
        var context = SdlcAgentContext.NamesFor(agent).Select(AgentStagePrompt.PlaceholderOf).Where(names.Contains).ToHashSet(StringComparer.Ordinal);
        var host = HostValues[agent].ToHashSet(StringComparer.Ordinal);
        return (inputs, context, host);
    }

    /// <summary>The input artifact keys of the agent's stage in <c>sdlc.pipeline.yaml</c> (none: the agent has no stage yet).</summary>
    private static IReadOnlyList<string> StageInputs(string agent) =>
        Definition.Stages.FirstOrDefault(s => s.Agent?.Key == agent)?.Inputs ?? [];
}
