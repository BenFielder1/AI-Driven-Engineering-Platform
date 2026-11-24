using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// M66 (0.8.0), §4.49 amended: the run-control fields of a definition (<c>trigger.input</c>, <c>trigger.on_active</c>,
/// <c>concurrency.runs</c>, artifact <c>input: true</c>, <c>requires: [{ input }]</c>), parsed, validated and left out of
/// the canonical JSON when absent, so every existing hash is unchanged; and FR-330's per-pipeline run control.
/// </summary>
public class RunControlDefinitionTests
{
    /// <summary><see cref="DefinitionYaml.Minimal"/> as a webhook pipeline: a <c>brief</c> input the draft stage requires.</summary>
    private static readonly string Webhook = Edit(
        "  summary: { title: Summary, format: markdown }\n",
        "  summary: { title: Summary, format: markdown }\n  brief: { title: Session brief, format: json, input: true }\n",
        Edit(
            "trigger: { kind: manual }",
            "trigger: { kind: webhook, input: brief, on_active: submit }\nconcurrency: { runs: many }",
            Edit(
                "    agent: { key: drafter, tools: [fixture.lookup] }\n",
                "    agent: { key: drafter, tools: [fixture.lookup] }\n    inputs: [brief]\n    requires:\n      - { input: brief }\n")));

    private static string Hash(string yaml) => DefinitionHash.Compute(Parse(yaml));

    private static PipelineDefinition Parse(string yaml) => PipelineDefinitionLoader.Parse(yaml).Definition!;

    private static string Json(string yaml) => DefinitionHash.CanonicalJson(Parse(yaml));

    // ---- Hash: absent fields are left out (§4.49 amended, §4.50) ----

    [Fact]
    public void FR330_the_sdlc_definition_hash_is_unchanged()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
    }

    [Fact]
    public void FR331_FR335_FR337_definitions_without_the_new_fields_hash_as_before()
    {
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));

        var json = Json(Minimal);
        Assert.DoesNotContain("\"runs\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"input\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"on_active\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FR331_writing_the_default_runs_one_or_input_false_keeps_the_hash()
    {
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Edit("settings: [redraft_cycles]", "settings: [redraft_cycles]\nconcurrency: { runs: one }")));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Edit("approval: auto_lock,", "approval: auto_lock, input: false,")));
    }

    [Fact]
    public void FR331_FR335_FR337_each_new_field_changes_the_hash_and_is_written_out()
    {
        Assert.NotEqual(AgentBindingHashTests.MinimalHash, Hash(Edit("settings: [redraft_cycles]", "settings: [redraft_cycles]\nconcurrency: { runs: many }")));
        Assert.NotEqual(Hash(Webhook), Hash(Webhook.Replace("on_active: submit", "on_active: skip", StringComparison.Ordinal)));

        var json = Json(Webhook);
        Assert.Contains("\"concurrency\":{\"exclusive\":[],\"runs\":\"many\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":{\"input\":\"brief\",\"kind\":\"webhook\",\"on_active\":\"submit\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"brief\":{\"approval\":\"required\",\"cascade\":\"downstream\",\"format\":\"json\",\"input\":true,\"title\":\"Session brief\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"requires\":[{\"input\":\"brief\"}]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FR331_FR335_FR337_canonical_json_with_the_new_fields_reads_back_to_the_same_hash()
    {
        var json = Json(Webhook);
        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(Hash(Webhook), DefinitionHash.Compute(reread.Definition!));
    }

    // ---- Loader ----

    [Fact]
    public void FR331_FR335_FR337_the_loader_reads_the_new_fields()
    {
        var definition = Parse(Webhook);

        Assert.Equal(TriggerKind.Webhook, definition.Trigger.Kind);
        Assert.Equal("brief", definition.Trigger.Input);
        Assert.Equal(TriggerOnActive.Submit, definition.Trigger.OnActive);
        Assert.Equal(RunConcurrency.Many, definition.Concurrency.Runs);
        Assert.True(definition.FindArtifact("brief")!.Input);
        Assert.False(definition.FindArtifact("draft")!.Input);
        Assert.Equal([new InputRequirement("brief")], definition.FindStage("draft")!.Requires);
    }

    [Fact]
    public void FR331_FR336_defaults_runs_one_and_on_active_skip()
    {
        var definition = Parse(Edit("trigger: { kind: manual }", "trigger: { kind: schedule }"));

        Assert.Equal(RunConcurrency.One, definition.Concurrency.Runs);
        Assert.Null(definition.Trigger.Input);
        Assert.Equal(TriggerOnActive.Skip, definition.Trigger.OnActive);
        Assert.Equal(RunConcurrency.One, Parse(Minimal).Concurrency.Runs);
    }

    // ---- Validator ----

    [Fact]
    public void FR335_FR337_a_webhook_definition_with_an_input_is_valid() => Assert.Empty(Errors(Webhook));

    [Fact]
    public void FR336_a_schedule_trigger_needs_no_input() =>
        Assert.Empty(Errors(Edit("trigger: { kind: manual }", "trigger: { kind: schedule, on_active: skip }")));

    [Fact]
    public void FR337_a_webhook_trigger_needs_an_input() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "trigger.input")], Errors(Edit("trigger: { kind: manual }", "trigger: { kind: webhook }")));

    [Fact]
    public void FR337_a_trigger_input_must_be_a_declared_artifact() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownArtifact, "trigger.input")],
            Errors(Webhook.Replace("kind: webhook, input: brief", "kind: webhook, input: nope", StringComparison.Ordinal)));

    [Fact]
    public void FR337_a_trigger_input_must_be_declared_input_true() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "trigger.input")],
            Errors(Webhook.Replace("kind: webhook, input: brief", "kind: webhook, input: draft", StringComparison.Ordinal)));

    [Fact]
    public void FR336_FR337_on_active_is_submit_or_skip() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "trigger.on_active")],
            Errors(Webhook.Replace("on_active: submit", "on_active: queue", StringComparison.Ordinal)));

    [Theory]
    [InlineData("trigger: { kind: schedule, cron: '0 6 * * *' }", "trigger.cron")]
    [InlineData("trigger: { kind: manual, on_active: submit }", "trigger.on_active")]
    [InlineData("trigger: { kind: manual, input: draft }", "trigger.input")]
    [InlineData("trigger: { kind: manual, path: /hooks/x }", "trigger.path")]
    public void FR276_FR336_DEF_TRIGGER_NOT_SUPPORTED_for_an_option_the_kind_does_not_take(string trigger, string path) =>
        Assert.Equal([(DefinitionErrorCodes.TriggerNotSupported, path)], Errors(Edit("trigger: { kind: manual }", trigger)));

    [Fact]
    public void FR331_DEF_PARSE_for_an_unknown_runs_value() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "concurrency.runs")],
            Errors(Edit("settings: [redraft_cycles]", "settings: [redraft_cycles]\nconcurrency: { runs: some }")));

    [Fact]
    public void FR335_a_required_input_must_be_declared_input_true()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "stages[0].requires[0].input")],
            Errors(Webhook.Replace("- { input: brief }", "- { input: report }", StringComparison.Ordinal)));
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownArtifact, "stages[0].requires[0].input")],
            Errors(Webhook.Replace("- { input: brief }", "- { input: nope }", StringComparison.Ordinal)));
    }

    [Fact]
    public void FR335_an_input_artifact_needs_no_producing_stage() =>
        Assert.DoesNotContain(Errors(Webhook), e => e.Code == DefinitionErrorCodes.ArtifactNoProducer);

    // ---- FR-330: run control ----

    private const string HostedYaml = """
        pipeline: hosted
        version: 1
        title: Hosted
        approval_modes: [strict]
        default_approval_mode: strict
        start: [one]
        stages:
          - { id: one, title: One, status: active }

        """;

    [Fact]
    public void FR330_run_control_defaults_to_engine()
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack<FixturePack>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.Equal(RunControl.Engine, registry.RunControlOf("fixture-review"));
        Assert.True(registry.TryGet("fixture-review", out var loaded));
        Assert.Equal(RunControl.Engine, loaded.RunControl);
        Assert.Null(registry.RunControlOf("nope"));
    }

    [Fact]
    public void FR330_a_pack_declares_host_control_per_pipeline_and_the_registry_exposes_it()
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack<FixturePack>();
        services.AddPipelinePack(new HostPack());
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.True(registry.IsValid, string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        Assert.Equal(RunControl.Host, registry.RunControlOf("hosted"));
        Assert.Equal(RunControl.Engine, registry.RunControlOf("fixture-review"));
    }

    [Fact]
    public void FR330_the_sdlc_pack_declares_host_control() =>
        Assert.Equal(RunControl.Host, ((IPipelinePack)new SdlcPack()).RunControlOf(SdlcPack.PipelineId));

    private sealed class HostPack : IPipelinePack
    {
        public string Id => "hosting";

        public void Register(IPackRegistry registry) => registry.AddDefinition("hosted.pipeline.yaml", HostedYaml);

        public RunControl RunControlOf(string pipelineId) => pipelineId == "hosted" ? RunControl.Host : RunControl.Engine;
    }
}
