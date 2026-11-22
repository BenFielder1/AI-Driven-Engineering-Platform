using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>D-0.7.0-42, FR-275, FR-276, §4.49: the loader and validator for a stage's secondary agents (<c>agents:</c>).</summary>
public class AgentBindingValidatorTests
{
    /// <summary>The summarise stage of <see cref="DefinitionYaml.Minimal"/>, before its <c>inputs:</c> line.</summary>
    private const string SummariseAnchor = "    status: working\n    inputs: [report]\n";

    /// <summary><see cref="DefinitionYaml.Minimal"/> with <paramref name="agents"/> (one YAML line per entry) on summarise.</summary>
    private static string WithAgents(params string[] agents) =>
        Edit(SummariseAnchor, "    status: working\n    agents:\n" + string.Concat(agents.Select(a => $"      - {a}\n")) + "    inputs: [report]\n");

    private const string Reviewer = "{ key: reviewer, job_type: stage.run }";

    private static StageDefinition Summarise(string yaml)
    {
        var parsed = PipelineDefinitionLoader.Parse(yaml);
        Assert.Empty(parsed.Errors);
        return parsed.Definition!.FindStage("summarise")!;
    }

    [Fact]
    public void D_0_7_0_42_a_binding_parses_with_its_inputs_defaulted_to_the_stages()
    {
        var stage = Summarise(WithAgents(Reviewer, "{ key: scribe, job_type: gate.timer, prompt: p.scribe, inputs: [draft], output: report, tools: [fixture.lookup] }"));

        Assert.Equal(
            [
                new AgentBindingDefinition("reviewer", EngineJobTypes.StageRun, null, ["report"], null, []),
                new AgentBindingDefinition("scribe", EngineJobTypes.GateTimer, "p.scribe", ["draft"], "report", [FixturePack.LookupTool]),
            ],
            stage.Agents,
            new BindingComparer());
        Assert.Null(stage.Agent);
        Assert.Equal(StageKind.Manual, stage.Kind);
    }

    [Fact]
    public void D_0_7_0_42_explicit_empty_inputs_are_kept()
    {
        var stage = Summarise(WithAgents("{ key: reviewer, job_type: stage.run, inputs: [] }"));

        Assert.Empty(stage.Agents[0].Inputs);
    }

    [Fact]
    public void D_0_7_0_42_a_valid_binding_has_no_errors() => Assert.Empty(Errors(WithAgents(Reviewer)));

    [Fact]
    public void D_0_7_0_42_DEF_PARSE_an_unknown_key_in_a_binding_and_a_binding_that_is_not_a_mapping() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "stages[2].agents[0].model"), (DefinitionErrorCodes.Parse, "stages[2].agents[1]")],
            Errors(WithAgents("{ key: reviewer, job_type: stage.run, model: m }", "reviewer")));

    [Fact]
    public void D_0_7_0_42_DEF_PARSE_key_and_job_type_are_required() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "stages[2].agents[0].key"), (DefinitionErrorCodes.Parse, "stages[2].agents[0].job_type")],
            Errors(WithAgents("{ inputs: [report] }")));

    [Fact]
    public void D_0_7_0_42_DEF_DUPLICATE_ID_a_binding_key_equal_to_a_primary_agent_key() =>
        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[2].agents[0].key")], Errors(WithAgents("{ key: drafter, job_type: stage.run }")));

    [Fact]
    public void D_0_7_0_42_DEF_DUPLICATE_ID_two_bindings_with_one_key_across_stages()
    {
        var yaml = Edit("    output: draft\n", "    output: draft\n    agents:\n      - { key: reviewer, job_type: gate.timer }\n", WithAgents(Reviewer));

        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[2].agents[0].key")], Errors(yaml));
    }

    [Fact]
    public void D_0_7_0_42_DEF_DUPLICATE_ID_a_job_type_another_agent_runs()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "stages[2].agents[0].job_type")],
            Errors(WithAgents($"{{ key: reviewer, job_type: {EngineJobTypes.AgentRun} }}")));
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "stages[2].agents[1].job_type")],
            Errors(WithAgents(Reviewer, "{ key: scribe, job_type: stage.run }")));
    }

    [Fact]
    public void D_0_7_0_42_DEF_UNKNOWN_JOB_TYPE_a_job_type_no_pack_registered() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownJobType, "stages[2].agents[0].job_type")], Errors(WithAgents("{ key: reviewer, job_type: fixture.nope }")));

    [Fact]
    public void D_0_7_0_42_DEF_UNKNOWN_ARTIFACT_inputs_and_output_must_be_declared() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownArtifact, "stages[2].agents[0].inputs[1]"), (DefinitionErrorCodes.UnknownArtifact, "stages[2].agents[0].output")],
            Errors(WithAgents("{ key: reviewer, job_type: stage.run, inputs: [draft, notes], output: verdict }")));

    [Fact]
    public void D_0_7_0_42_DEF_UNKNOWN_TOOL_a_tool_no_pack_registered() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownTool, "stages[2].agents[0].tools[1]")],
            Errors(WithAgents("{ key: reviewer, job_type: stage.run, tools: [fixture.lookup, fixture.nope] }")));

    [Fact]
    public void D_0_7_0_42_a_bindings_output_produces_its_artifact()
    {
        // summarise's output moves to a binding on summarise, and check reads it: the binding is its only producer.
        var yaml = Edit("    output: summary\n", "    agents:\n      - { key: scribe, job_type: stage.run, output: summary }\n");
        yaml = Edit("    inputs: [draft]\n", "    inputs: [draft, summary]\n", yaml);

        Assert.Empty(Errors(yaml));
        Assert.Equal(
            [(DefinitionErrorCodes.ArtifactNoProducer, "stages[1].inputs[1]")],
            Errors(yaml.Replace(", output: summary", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public void D_0_7_0_42_DEF_ARTIFACT_NO_PRODUCER_a_binding_input_nothing_produces() =>
        Assert.Equal(
            [(DefinitionErrorCodes.ArtifactNoProducer, "stages[2].agents[0].inputs[0]")],
            Errors(Edit("    output: summary\n", string.Empty, WithAgents("{ key: reviewer, job_type: stage.run, inputs: [summary] }"))));

    [Fact]
    public void D_0_7_0_42_DEF_UNKNOWN_PROMPT_the_binding_prompt_or_key_must_be_registered()
    {
        var catalog = Catalog with { Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { ["drafter"] = Template("Go."), ["checker"] = Template("{{DRAFT}}"), ["p.review"] = Template("{{REPORT}}") } };

        Assert.Empty(Errors(WithAgents("{ key: reviewer, job_type: stage.run, prompt: p.review }"), catalog));
        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[2].agents[0].key")], Errors(WithAgents(Reviewer), catalog));
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownPrompt, "stages[2].agents[0].prompt")],
            Errors(WithAgents("{ key: reviewer, job_type: stage.run, prompt: p.nope }"), catalog));
    }

    [Fact]
    public void D_0_7_0_42_DEF_UNKNOWN_PROMPT_a_required_placeholder_is_filled_from_the_bindings_inputs()
    {
        var catalog = Catalog with { Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { ["drafter"] = Template("Go."), ["checker"] = Template("{{DRAFT}}"), ["reviewer"] = Template("{{DRAFT}}") } };

        Assert.Empty(Errors(WithAgents("{ key: reviewer, job_type: stage.run, inputs: [draft] }"), catalog));
        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[2].agents[0].key")], Errors(WithAgents(Reviewer), catalog));
    }

    [Fact]
    public void D_0_7_0_42_ForAgent_resolves_the_primary_a_binding_or_nothing()
    {
        var definition = PipelineDefinitionLoader.Parse(Edit("    output: report\n", "    output: report\n    agents:\n      - { key: reviewer, job_type: stage.run, inputs: [], output: summary, tools: [fixture.lookup] }\n")).Definition!;
        var check = definition.FindStage("check")!;

        Assert.Same(check, check.ForAgent(null));
        Assert.Same(check, check.ForAgent("checker"));
        Assert.Null(check.ForAgent("nobody"));
        Assert.Null(definition.FindStage("summarise")!.ForAgent(null));

        var view = check.ForAgent("reviewer")!;
        Assert.Equal(("check", "reviewer", EngineJobTypes.StageRun, "summary"), (view.Id, view.Agent!.Key, view.Agent.JobType, view.Output));
        Assert.Empty(view.Inputs);
        Assert.Equal([FixturePack.LookupTool], view.Agent.Tools);
        Assert.Equal((0, (OnInvalidPolicy?)null, 0), (view.Validate.Count, view.OnInvalid, view.Agents.Count));
        Assert.Equal((check.Gate, check.Loop, check.OnReject, check.Status), (view.Gate, view.Loop, view.OnReject, view.Status));
    }

    [Fact]
    public void D_0_7_0_42_DEF_DUPLICATE_ID_two_primary_agents_with_one_key() =>
        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[1].agent.key")], Errors(Edit("{ key: checker }", "{ key: drafter }")));

    [Fact]
    public void D_0_7_0_42_DEF_DUPLICATE_ID_two_primary_agents_with_one_job_type_other_than_agent_run()
    {
        var yaml = Edit("{ key: checker }", "{ key: checker, job_type: stage.run }", Edit("{ key: drafter, ", "{ key: drafter, job_type: stage.run, "));

        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[1].agent.job_type")], Errors(yaml));
        Assert.Empty(Errors(Minimal)); // drafter and checker share the default agent.run
    }

    [Theory]
    [InlineData("fixture-review.pipeline.yaml")]
    [InlineData("sdlc-sketch.pipeline.yaml")]
    [InlineData("sdlc")]
    public void D_0_7_0_42_no_shipped_or_fixture_definition_repeats_an_agent_key_or_job_type(string name)
    {
        string yaml;
        if (name == "sdlc")
        {
            using var reader = new StreamReader(Tevrin.Pack.Sdlc.SdlcPack.OpenDefinition());
            yaml = reader.ReadToEnd();
        }
        else
        {
            yaml = Fixtures.Read(name);
        }

        Assert.DoesNotContain(Errors(yaml, PackCatalog.Empty), e => e.Code == DefinitionErrorCodes.DuplicateId);
    }

    private static string Template(string user) => $"---\nversion: t1\n---\nSystem.\n<!-- user -->\n{user}";

    /// <summary>Compares bindings by value (lists by their items).</summary>
    private sealed class BindingComparer : IEqualityComparer<AgentBindingDefinition>
    {
        public bool Equals(AgentBindingDefinition? x, AgentBindingDefinition? y) =>
            x is not null && y is not null && (x.Key, x.JobType, x.Prompt, x.Output) == (y.Key, y.JobType, y.Prompt, y.Output)
            && x.Inputs.SequenceEqual(y.Inputs) && x.Tools.SequenceEqual(y.Tools);

        public int GetHashCode(AgentBindingDefinition obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
}
