using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-351, §4.75 (M69): the agent feedback store's definition syntax (<c>feedback: { inject, limit }</c> on an agent stage,
/// <c>writes_feedback: { agent, from }</c> on a stage), validated and left out of the canonical JSON when unused, the
/// <c>{{AGENT_FEEDBACK}}</c> placeholder, and how a stage's output items become entries.
/// </summary>
public class AgentFeedbackDefinitionTests
{
    private static string Yaml => FlowFixturePack.Yaml(FlowFixturePack.Postmortem);

    private static PipelineDefinition Load(string yaml) => PipelineDefinitionLoader.Parse(yaml).Definition!;

    private static string Hash(string yaml) => DefinitionHash.Compute(Load(yaml));

    private static List<(string Code, string Path)> PostmortemErrors(string find, string replace) =>
        Errors(Edit(find, replace, Yaml), FlowFixturePack.Catalog);

    [Fact]
    public void FR351_the_fixture_loads_feedback_and_writes_feedback()
    {
        var definition = Load(Yaml);

        Assert.Equal(new FeedbackInjectionDefinition(true, 10), definition.FindStage("work")!.Feedback);
        Assert.Null(definition.FindStage("work")!.WritesFeedback);
        Assert.Equal(new WritesFeedbackDefinition("worker", "lessons"), definition.FindStage("review")!.WritesFeedback);
        Assert.Null(definition.FindStage("review")!.Feedback);
    }

    [Fact]
    public void FR351_unused_feedback_settings_leave_every_existing_hash_unchanged()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));
        var json = DefinitionHash.CanonicalJson(Load(Minimal));
        Assert.DoesNotContain("\"feedback\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"writes_feedback\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FR351_feedback_and_writes_feedback_are_hashed_and_the_default_limit_is_written_out()
    {
        Assert.Equal(Hash(Yaml), Hash(Edit("feedback: { inject: true, limit: 10 }", "feedback: { inject: true }", Yaml)));
        Assert.NotEqual(Hash(Yaml), Hash(Edit("feedback: { inject: true, limit: 10 }", "feedback: { inject: true, limit: 5 }", Yaml)));
        Assert.NotEqual(Hash(Yaml), Hash(Edit("    feedback: { inject: true, limit: 10 }\n", string.Empty, Yaml)));
        Assert.NotEqual(Hash(Yaml), Hash(Edit("from: lessons", "from: notes", Yaml)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void FR351_a_feedback_limit_outside_1_to_50_is_rejected(int limit) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[0].feedback.limit")],
            PostmortemErrors("limit: 10", $"limit: {limit}"));

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void FR351_a_feedback_limit_of_1_to_50_is_valid(int limit) =>
        Assert.Empty(PostmortemErrors("limit: 10", $"limit: {limit}"));

    [Fact]
    public void FR351_writes_feedback_for_an_agent_the_pipeline_does_not_declare_is_rejected() =>
        Assert.Equal([(DefinitionErrorCodes.FanOutAgent, "stages[1].writes_feedback.agent")],
            PostmortemErrors("{ agent: worker, from: lessons }", "{ agent: planner, from: lessons }"));

    [Fact]
    public void FR351_writes_feedback_needs_a_from_field() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].writes_feedback.from")],
            PostmortemErrors("{ agent: worker, from: lessons }", "{ agent: worker }"));

    [Fact]
    public void FR351_an_unknown_feedback_setting_is_a_parse_error() =>
        Assert.Contains(PostmortemErrors("{ inject: true, limit: 10 }", "{ inject: true, limit: 10, decay: 2 }"),
            e => e.Code == DefinitionErrorCodes.Parse);

    [Fact]
    public void FR351_an_injecting_stage_may_use_the_optional_AGENT_FEEDBACK_placeholder()
    {
        var work = Load(Yaml).FindStage("work")!;
        const string Optional = "---\nversion: w-1\n---\nYou work.\n<!-- user -->\nTask: {{TASK}}\n{{AGENT_FEEDBACK?}}\n";
        const string Required = "---\nversion: w-1\n---\nYou work.\n<!-- user -->\nTask: {{TASK}}\n{{AGENT_FEEDBACK}}\n";

        Assert.Null(AgentPromptPlaceholders.Problem(work, Optional, []));
        Assert.Contains("{{AGENT_FEEDBACK}} must be optional", AgentPromptPlaceholders.Problem(work, Required, []), StringComparison.Ordinal);
    }

    [Fact]
    public void FR351_the_pipeline_declares_its_stage_agents_only()
    {
        var definition = Load(Yaml);

        Assert.True(FeedbackInjectionRules.DeclaresAgent(definition, "worker"));
        Assert.True(FeedbackInjectionRules.DeclaresAgent(definition, "reviewer"));
        Assert.False(FeedbackInjectionRules.DeclaresAgent(definition, "planner"));
    }

    [Fact]
    public void FR351_output_items_are_strings_or_objects_with_text()
    {
        const string Output = """{ "lessons": ["Cite sources.", { "text": "Keep it short." }, { "note": "x" }, 3, null] }""";

        Assert.Equal(["Cite sources.", "Keep it short.", null, null, null], EngineRunDriver.FeedbackItems(Output, "lessons"));
        Assert.Equal(["One lesson."], EngineRunDriver.FeedbackItems("""{ "lessons": "One lesson." }""", "lessons"));
        Assert.Empty(EngineRunDriver.FeedbackItems("""{ "notes": ["x"] }""", "lessons"));
        Assert.Empty(EngineRunDriver.FeedbackItems("Not JSON.", "lessons"));
        Assert.Empty(EngineRunDriver.FeedbackItems(null, "lessons"));
    }
}
