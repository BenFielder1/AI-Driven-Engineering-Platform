using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-358–FR-361 (M71, §3.7, §4.49 amended): the definition and registration syntax of tool tiers and external actions.
/// A pack registers each tool with a tier (default <c>write</c>) and a handler or tool may be <c>external</c>; an agent
/// stage or binding may set <c>tool_policy: { max_tier }</c> (default <c>write</c>), and a listed tool above it gives
/// <c>DEF_TOOL_TIER</c>. <c>tool_policy</c> is left out of the canonical JSON when it's the default, so every existing hash
/// is unchanged. <c>fixture-oncall</c> loads and validates against its real prompts.
/// </summary>
public class ToolTierDefinitionTests
{
    /// <summary>Pinned hash of <c>fixture-oncall</c>: a change to the canonical form of <c>tool_policy</c> changes it.</summary>
    public const string OnCallHash = "7dddacc716823fcd516b398f32990a7f7b2538a60b9443b13bade5046feea408";

    private const string Destructive = "tool_policy: { max_tier: destructive }";

    private static string OnCallYaml => FlowFixturePack.Yaml(FlowFixturePack.OnCall);

    private static PipelineDefinition Load(string yaml)
    {
        var parsed = PipelineDefinitionLoader.Parse(yaml);
        Assert.Empty(parsed.Errors);
        return parsed.Definition!;
    }

    private static string Hash(string yaml) => DefinitionHash.Compute(Load(yaml));

    private static List<(string Code, string Path)> OnCallErrors(string find, string replace) =>
        Errors(Edit(find, replace, OnCallYaml), FlowFixturePack.Catalog);

    private static PipelineRegistry RegistryOf(params IPipelinePack[] packs) =>
        new(packs, [], Array.Empty<JobHandlerRegistration>());

    // ---- fixture-oncall ----

    [Fact]
    public void FR358_FR361_the_oncall_fixture_loads_and_validates_in_a_registry_with_its_real_prompts()
    {
        var registry = RegistryOf(new FlowFixturePack(FlowFixturePack.OnCall));

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(FlowFixturePack.OnCall, out var loaded));
        Assert.Empty(PipelineDefinitionResolver.MissingNames(registry, loaded.Definition!));
        Assert.Equal(TriggerKind.Webhook, loaded.Definition!.Trigger.Kind);
        Assert.Equal("alert", loaded.Definition.Trigger.Input);
        Assert.Equal(RunControl.Engine, loaded.RunControl);
    }

    [Fact]
    public void FR358_FR359_the_oncall_fixture_is_valid_against_the_catalog() =>
        Assert.Empty(Errors(OnCallYaml, FlowFixturePack.Catalog));

    [Fact]
    public void FR358_FR359_the_loader_reads_tool_policy_on_the_executor_only()
    {
        var definition = Load(OnCallYaml);
        var investigate = definition.Stages[0];
        var remediate = definition.Stages[1];

        Assert.Null(investigate.ToolPolicy);
        Assert.Equal(ToolTier.Write, ToolTierRules.MaxTierOf(investigate.ToolPolicy));
        Assert.Equal([FlowFixturePack.ReadLogsTool], investigate.Agent!.Tools);
        Assert.Equal(new ToolPolicyDefinition(ToolTier.Destructive), remediate.ToolPolicy);
        Assert.Equal([FlowFixturePack.ReadLogsTool, FlowFixturePack.RestartPodTool], remediate.Agent!.Tools);
    }

    [Fact]
    public void FR359_restart_pod_under_max_tier_write_gives_DEF_TOOL_TIER_at_the_tool() =>
        Assert.Equal(
            [(DefinitionErrorCodes.ToolTier, "stages[1].agent.tools[1]")],
            OnCallErrors(Destructive, "tool_policy: { max_tier: write }"));

    [Fact]
    public void FR358_without_tool_policy_the_ceiling_is_write_so_restart_pod_is_refused() =>
        Assert.Equal(
            [(DefinitionErrorCodes.ToolTier, "stages[1].agent.tools[1]")],
            OnCallErrors("    " + Destructive + "\n", string.Empty));

    [Fact]
    public void FR358_max_tier_read_allows_read_tools_and_refuses_the_rest()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.ToolTier, "stages[1].agent.tools[1]")],
            OnCallErrors(Destructive, "tool_policy: { max_tier: read }"));

        // The investigator's read-logs is within read.
        Assert.Empty(OnCallErrors(
            "    inputs: [alert]\n    output: findings",
            "    tool_policy: { max_tier: read }\n    inputs: [alert]\n    output: findings"));
    }

    // ---- bindings and stage kinds ----

    /// <summary>The investigator stage with a secondary agent binding <paramref name="binding"/>.</summary>
    private static string WithBinding(string binding) =>
        Edit("    requires:\n      - { input: alert }", $"    requires:\n      - {{ input: alert }}\n    agents:\n      - {binding}", OnCallYaml);

    [Fact]
    public void FR358_a_binding_listing_a_tool_above_its_ceiling_gives_DEF_TOOL_TIER_at_the_binding_tool() =>
        Assert.Equal(
            [(DefinitionErrorCodes.ToolTier, "stages[0].agents[0].tools[1]")],
            Errors(WithBinding("{ key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.read-logs, flow.restart-pod] }"), FlowFixturePack.Catalog));

    [Fact]
    public void FR358_a_binding_sets_its_own_tool_policy_and_does_not_inherit_the_stage_s()
    {
        var yaml = WithBinding("{ key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.restart-pod], " + Destructive + " }");
        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));

        var stage = Load(yaml).Stages[0];
        Assert.Equal(ToolTier.Destructive, stage.ForAgent("helper")!.ToolPolicy!.MaxTier);
        Assert.Null(stage.ForAgent("investigator")!.ToolPolicy);

        // The executor's destructive ceiling isn't its binding's: a binding without tool_policy stays at write.
        var onExecutor = Edit(
            "    " + Destructive + "\n",
            "    " + Destructive + "\n    agents:\n      - { key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.restart-pod] }\n",
            OnCallYaml);
        Assert.Equal([(DefinitionErrorCodes.ToolTier, "stages[1].agents[0].tools[0]")], Errors(onExecutor, FlowFixturePack.Catalog));
        Assert.Null(Load(onExecutor).Stages[1].ForAgent("helper")!.ToolPolicy);
    }

    [Fact]
    public void FR358_tool_policy_needs_an_agent_stage()
    {
        var yaml = Edit(
            "    agent: { key: investigator, prompt: flow.oncall-investigate, tools: [flow.read-logs] }\n",
            "    handler: flow.qa-tally\n    tool_policy: { max_tier: read }\n",
            OnCallYaml);

        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].tool_policy"), Errors(yaml, FlowFixturePack.Catalog));
    }

    [Theory]
    [InlineData("tool_policy: { max_tier: admin }", "stages[1].tool_policy.max_tier")]
    [InlineData("tool_policy: {}", "stages[1].tool_policy.max_tier")]
    [InlineData("tool_policy: { max_tier: destructive, ask: true }", "stages[1].tool_policy.ask")]
    [InlineData("tool_policy: destructive", "stages[1].tool_policy")]
    public void FR358_a_malformed_tool_policy_is_a_parse_error(string policy, string path) =>
        Assert.Contains((DefinitionErrorCodes.Parse, path), OnCallErrors(Destructive, policy));

    [Fact]
    public void FR358_an_unknown_tool_is_reported_once_as_unknown_not_as_a_tier()
    {
        var errors = OnCallErrors("flow.read-logs, flow.restart-pod", "flow.read-logs, flow.wipe-cluster");
        Assert.Equal([(DefinitionErrorCodes.UnknownTool, "stages[1].agent.tools[1]")], errors);
    }

    [Fact]
    public void FR358_the_new_code_is_in_the_code_table() =>
        Assert.Contains("DEF_TOOL_TIER", DefinitionErrorCodes.All);

    // ---- hashes ----

    [Fact]
    public void FR358_the_oncall_hash_is_pinned_and_the_canonical_json_reads_back()
    {
        var definition = Load(OnCallYaml);
        Assert.Equal(OnCallHash, DefinitionHash.Compute(definition));

        var json = DefinitionHash.CanonicalJson(definition);
        Assert.Contains("\"tool_policy\"", json, StringComparison.Ordinal);
        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(OnCallHash, DefinitionHash.Compute(reread.Definition!));
    }

    [Fact]
    public void FR358_the_default_tool_policy_is_left_out_of_the_canonical_json()
    {
        var withoutPolicy = Edit("    " + Destructive + "\n", string.Empty, OnCallYaml);
        var withDefault = Edit(Destructive, "tool_policy: { max_tier: write }", OnCallYaml);

        Assert.Equal(Hash(withoutPolicy), Hash(withDefault));
        Assert.DoesNotContain("\"tool_policy\"", DefinitionHash.CanonicalJson(Load(withDefault)), StringComparison.Ordinal);
        Assert.NotEqual(Hash(withoutPolicy), Hash(OnCallYaml));
        Assert.NotEqual(Hash(OnCallYaml), Hash(Edit(Destructive, "tool_policy: { max_tier: read }", OnCallYaml)));

        // The same holds for a binding's tool_policy.
        var binding = "{ key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.read-logs] }";
        var bindingDefault = "{ key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.read-logs], tool_policy: { max_tier: write } }";
        var bindingRead = "{ key: helper, job_type: flow.judge, prompt: flow.judge, tools: [flow.read-logs], tool_policy: { max_tier: read } }";
        Assert.Equal(Hash(WithBinding(binding)), Hash(WithBinding(bindingDefault)));
        Assert.NotEqual(Hash(WithBinding(binding)), Hash(WithBinding(bindingRead)));
    }

    [Fact]
    public void FR358_FR360_the_sdlc_and_existing_definitions_hash_as_before()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));
        Assert.Equal(ScaleConstructDefinitionTests.QaHash, DefinitionHash.Compute(Load(FlowFixturePack.Yaml(FlowFixturePack.Qa))));
        Assert.DoesNotContain("\"tool_policy\"", DefinitionHash.CanonicalJson(Load(Minimal)), StringComparison.Ordinal);
    }

    // ---- registration ----

    [Fact]
    public void FR358_the_sdlc_registers_its_repo_tools_as_read_and_still_validates()
    {
        var registry = RegistryOf(new SdlcPack());

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(SdlcPack.PipelineId, out var loaded));
        Assert.Equal(AgentBindingHashTests.SdlcHash, loaded.Hash);
        Assert.All(SdlcToolNames.All, tool => Assert.Equal(ToolTier.Read, registry.ToolTierOf(tool)));
    }

    [Fact]
    public void FR358_the_oncall_tools_carry_their_registered_tiers()
    {
        var registry = RegistryOf(new FlowFixturePack(FlowFixturePack.OnCall));

        Assert.Equal(ToolTier.Read, registry.ToolTierOf(FlowFixturePack.ReadLogsTool));
        Assert.Equal(ToolTier.Destructive, registry.ToolTierOf(FlowFixturePack.RestartPodTool));
        Assert.Null(registry.ToolTierOf("flow.not-registered"));
    }

    [Fact]
    public void FR358_a_tool_registered_without_a_tier_is_write()
    {
        IPackRegistry registration = new PackRegistration("acme");
        registration.AddTool("acme.legacy");
        var pack = (PackRegistration)registration;

        Assert.Equal(ToolTier.Write, pack.ToolTiers["acme.legacy"]);
        Assert.Equal(ToolTierRules.DefaultRegisteredTier, ToolTierRules.TierOf(new PackCatalog { Tools = ["acme.di-only"] }, "acme.di-only"));
        Assert.Empty(pack.ExternalTools);
    }

    [Fact]
    public void FR358_registering_a_tool_again_never_lowers_its_tier()
    {
        var registration = new PackRegistration("acme");
        registration.AddTool("acme.reset", ToolTier.Destructive);
        registration.AddTool("acme.reset", ToolTier.Read);
        registration.AddTool("acme.reset");

        Assert.Equal(ToolTier.Destructive, registration.ToolTiers["acme.reset"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => registration.AddTool("acme.odd", (ToolTier)7));
        Assert.Throws<ArgumentException>(() => registration.AddTool("other.reset", ToolTier.Read));
    }

    [Fact]
    public void FR358_a_tool_bound_only_in_DI_is_write()
    {
        var registry = new PipelineRegistry(
            [],
            [new PipelineComponentRegistration(PipelineComponentKind.Tool, "acme.di-only", typeof(object))],
            Array.Empty<JobHandlerRegistration>());

        Assert.Equal(ToolTier.Write, registry.ToolTierOf("acme.di-only"));
    }

    [Fact]
    public void FR360_external_handlers_and_tools_are_registered_in_code_only()
    {
        var registry = RegistryOf(new FixtureCvPack(), new FlowFixturePack(FlowFixturePack.OnCall));

        Assert.True(registry.IsExternal(PipelineComponentKind.StageHandler, FixtureCvPack.SubmitApplicationHandler));
        Assert.True(registry.Registers(PipelineComponentKind.StageHandler, FixtureCvPack.SubmitApplicationHandler));
        Assert.False(registry.IsExternal(PipelineComponentKind.StageHandler, FlowFixturePack.QaTallyHandler));
        Assert.False(registry.IsExternal(PipelineComponentKind.Tool, FlowFixturePack.RestartPodTool));

        // fixture-cv still validates and hashes as its YAML alone: registration never changes a definition's hash.
        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(FixtureCvPack.PipelineId, out var cv));
        Assert.Equal(DefinitionHash.Compute(Fixtures.Load("fixture-cv.pipeline.yaml")), cv.Hash);

        var registration = new PackRegistration("acme");
        registration.AddTool("acme.send-email", ToolTier.Write, external: true);
        registration.AddStageHandler("acme.publish", external: true);
        registration.AddStageHandler("acme.publish");
        Assert.Equal(["acme.send-email"], registration.ExternalTools);
        Assert.Equal(["acme.publish"], registration.ExternalHandlers);
        Assert.Equal(ToolTier.Write, registration.ToolTiers["acme.send-email"]);
    }
}
