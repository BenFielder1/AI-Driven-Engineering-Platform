using Tevrin.Engine.Domain.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-276, §4.49: the reachability, loop, schema, producer, setting and trigger rows.</summary>
public class DefinitionValidatorFlowTests
{
    [Fact]
    public void FR276_DEF_UNREACHABLE_STAGE() =>
        Assert.Equal([(DefinitionErrorCodes.UnreachableStage, "stages[1]")], Errors(Edit("    output: draft", "    output: draft\n    on_success: summarise")));

    [Fact]
    public void FR276_a_loop_or_on_reject_makes_an_earlier_stage_reachable()
    {
        var yaml = Edit("start: [draft]", "start: [check]");
        Assert.Contains((DefinitionErrorCodes.UnreachableStage, "stages[0]"), Errors(yaml));
        Assert.Empty(Errors(Edit("    output: report", "    output: report\n    loop: { to: draft, max_iterations: 2 }", yaml)));
        Assert.Empty(Errors(Edit("    output: report", "    output: report\n    on_reject: { to: draft, max_iterations: 2 }", yaml)));
    }

    [Fact]
    public void FR276_DEF_UNCAPPED_LOOP_for_a_loop() =>
        Assert.Equal([(DefinitionErrorCodes.UncappedLoop, "stages[1].loop")], Errors(Edit("    output: report", "    output: report\n    loop: { to: draft, on: redo }")));

    [Fact]
    public void FR276_DEF_UNCAPPED_LOOP_for_a_backward_on_reject() =>
        Assert.Equal([(DefinitionErrorCodes.UncappedLoop, "stages[1].on_reject")], Errors(Edit("    output: report", "    output: report\n    on_reject: draft")));

    [Fact]
    public void FR276_a_forward_on_reject_needs_no_cap() =>
        Assert.Empty(Errors(Edit("    output: report", "    output: report\n    on_reject: summarise")));

    [Fact]
    public void FR276_DEF_LOOP_FORWARD() =>
        Assert.Equal([(DefinitionErrorCodes.LoopForward, "stages[1].loop.to")], Errors(Edit("    output: report", "    output: report\n    loop: { to: summarise, max_iterations: 2 }")));

    [Fact]
    public void FR276_DEF_LOOP_FORWARD_for_a_loop_to_itself() =>
        Assert.Equal([(DefinitionErrorCodes.LoopForward, "stages[1].loop.to")], Errors(Edit("    output: report", "    output: report\n    loop: { to: check, max_iterations: 2 }")));

    [Fact]
    public void FR276_DEF_PARSE_max_iterations_out_of_range() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].loop.max_iterations")], Errors(Edit("    output: report", "    output: report\n    loop: { to: draft, max_iterations: 101 }")));

    [Fact]
    public void FR276_DEF_SCHEMA_NOT_FOUND_when_not_embedded() =>
        Assert.Equal([(DefinitionErrorCodes.SchemaNotFound, "artifacts.draft.schema")], Errors(Edit("format: json,", "format: json, schema: missing.schema.json,")));

    [Fact]
    public void FR276_DEF_SCHEMA_NOT_FOUND_when_not_valid_json_schema()
    {
        var catalog = Catalog with
        {
            InvalidSchemas = new Dictionary<string, string>(StringComparer.Ordinal) { ["draft.schema.json"] = "not JSON" },
        };
        Assert.Equal([(DefinitionErrorCodes.SchemaNotFound, "artifacts.draft.schema")], Errors(Edit("format: json,", "format: json, schema: draft.schema.json,"), catalog));
    }

    [Fact]
    public void FR276_DEF_ARTIFACT_NO_PRODUCER() =>
        Assert.Equal([(DefinitionErrorCodes.ArtifactNoProducer, "stages[1].inputs[0]")], Errors(Edit("    output: draft", "    output: summary")));

    [Fact]
    public void FR276_an_artifact_a_pack_handler_produces_needs_no_stage() =>
        Assert.Empty(Errors(Edit("    output: draft", "    output: summary"), Catalog with { ProducedArtifacts = ["draft"] }));

    [Fact]
    public void FR276_DEF_UNKNOWN_SETTING_for_an_undeclared_setting() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownSetting, "stages[1].loop.max_iterations.setting")],
            Errors(Edit("    output: report", "    output: report\n    loop: { to: draft, max_iterations: { setting: review_cycles } }")));

    [Fact]
    public void FR276_DEF_UNKNOWN_SETTING_for_a_setting_no_pack_provides() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownSetting, "settings[0]")], Errors(Minimal, Catalog with { Settings = [] }));

    /// <summary>FR-336 (0.8.0) lifts D-0.7.0-3: schedule and webhook kinds are supported; an option the kind doesn't take is
    /// <c>DEF_TRIGGER_NOT_SUPPORTED</c> (more cases in <see cref="RunControlDefinitionTests"/>).</summary>
    [Fact]
    public void FR276_FR284_DEF_TRIGGER_NOT_SUPPORTED() =>
        Assert.Equal([(DefinitionErrorCodes.TriggerNotSupported, "trigger.cron")], Errors(Edit("{ kind: manual }", "{ kind: schedule, cron: '0 6 * * *' }")));

    [Fact]
    public void FR276_every_code_in_the_table_is_exercised() =>
        Assert.Equal(23, DefinitionErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
}
