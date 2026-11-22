using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// D-0.7.0-43, FR-280, §4.49, §4.50: a stage's secondary handlers (<c>handlers:</c>). They parse, are validated like
/// <c>handler:</c>, are unique across the pipeline, never change the stage's kind, and are omitted from the canonical JSON
/// when there are none, so every existing hash stays the same.
/// </summary>
public class StageHandlerBindingTests
{
    private const string SummariseAnchor = "    status: working\n    inputs: [report]\n";

    private static readonly PackCatalog WithHandlers = FixturePack.Catalog with { Handlers = ["fixture.run", "fixture.poll"] };

    /// <summary><see cref="DefinitionYaml.Minimal"/> with <paramref name="lines"/> (YAML, 4-space indented) on summarise.</summary>
    private static string OnSummarise(string lines) => Edit(SummariseAnchor, "    status: working\n" + lines + "    inputs: [report]\n");

    private static StageDefinition Summarise(string yaml)
    {
        var parsed = PipelineDefinitionLoader.Parse(yaml);
        Assert.Empty(parsed.Errors);
        return parsed.Definition!.FindStage("summarise")!;
    }

    [Fact]
    public void D_0_7_0_43_a_stage_parses_its_handler_and_secondary_handlers()
    {
        var stage = Summarise(OnSummarise("    handler: fixture.run\n    handlers: [fixture.poll]\n"));

        Assert.Equal("fixture.run", stage.Handler);
        Assert.Equal(["fixture.poll"], stage.Handlers);
        Assert.Equal(["fixture.run", "fixture.poll"], stage.AllHandlers());
        Assert.Equal(StageKind.Code, stage.Kind);
    }

    [Fact]
    public void D_0_7_0_43_secondary_handlers_alone_keep_the_stage_manual()
    {
        var stage = Summarise(OnSummarise("    handlers: [fixture.poll]\n"));

        Assert.Null(stage.Handler);
        Assert.Equal(StageKind.Manual, stage.Kind);
    }

    [Fact]
    public void D_0_7_0_43_a_handler_next_to_agent_bindings_is_valid() =>
        Assert.Empty(Errors(
            OnSummarise("    handler: fixture.run\n    handlers: [fixture.poll]\n    agents:\n      - { key: reviewer, job_type: stage.run }\n"),
            WithHandlers));

    [Fact]
    public void D_0_7_0_43_secondary_handlers_may_sit_on_an_agent_stage() =>
        Assert.Empty(Errors(Edit("    agent: { key: checker }\n", "    agent: { key: checker }\n    handlers: [fixture.poll]\n"), WithHandlers));

    [Fact]
    public void D_0_7_0_43_DEF_UNKNOWN_HANDLER_names_the_secondary_entry() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownHandler, "stages[2].handlers[1]")],
            Errors(OnSummarise("    handlers: [fixture.poll, fixture.missing]\n"), WithHandlers));

    [Fact]
    public void D_0_7_0_43_DEF_DUPLICATE_ID_a_handler_name_declared_twice_in_the_pipeline() =>
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "stages[2].handlers[0]"), (DefinitionErrorCodes.DuplicateId, "stages[2].handlers[1]")],
            Errors(
                OnSummarise("    handler: fixture.run\n    handlers: [fixture.run, fixture.poll]\n")
                    .Replace("    agent: { key: checker }\n", "    agent: { key: checker }\n    handlers: [fixture.poll]\n", StringComparison.Ordinal),
                WithHandlers));

    [Fact]
    public void D_0_7_0_43_StageOf_finds_the_stage_declaring_a_handler_either_way()
    {
        var definition = PipelineDefinitionLoader.Parse(OnSummarise("    handler: fixture.run\n    handlers: [fixture.poll]\n")).Definition!;

        Assert.Equal("summarise", StageHandlers.StageOf(definition, "fixture.run")?.Id);
        Assert.Equal("summarise", StageHandlers.StageOf(definition, "fixture.poll")?.Id);
        Assert.Null(StageHandlers.StageOf(definition, "fixture.other"));
    }

    [Fact]
    public void D_0_7_0_43_canonical_json_omits_handlers_when_there_are_none_so_hashes_are_unchanged()
    {
        Assert.DoesNotContain("\"handlers\"", DefinitionHash.CanonicalJson(PipelineDefinitionLoader.Parse(Minimal).Definition!), StringComparison.Ordinal);
        Assert.Equal(AgentBindingHashTests.MinimalHash, DefinitionHash.Compute(PipelineDefinitionLoader.Parse(OnSummarise("    handlers: []\n")).Definition!));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
    }

    [Fact]
    public void D_0_7_0_43_secondary_handlers_change_the_hash_and_read_back_from_canonical_json()
    {
        var definition = PipelineDefinitionLoader.Parse(OnSummarise("    handlers: [fixture.poll]\n")).Definition!;
        var canonical = DefinitionHash.CanonicalJson(definition);

        Assert.NotEqual(AgentBindingHashTests.MinimalHash, DefinitionHash.Compute(definition));
        Assert.Contains("\"handlers\":[\"fixture.poll\"]", canonical, StringComparison.Ordinal);
        var reread = PipelineDefinitionLoader.Parse(canonical).Definition!;
        Assert.Equal(["fixture.poll"], reread.FindStage("summarise")!.Handlers);
        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread));
    }

    [Fact]
    public void D_0_7_0_43_an_agent_view_of_a_stage_has_no_handlers()
    {
        var stage = Summarise(OnSummarise("    handlers: [fixture.poll]\n    agents:\n      - { key: reviewer, job_type: stage.run }\n"));

        Assert.Empty(stage.ForAgent("reviewer")!.Handlers);
    }
}
