using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-355–FR-357 (M70, §3.6, §4.49 amended, §4.68, §4.69): the definition syntax of map-reduce, conversation and the
/// <c>from_runs</c> stage input: loaded, validated (<c>DEF_MAP_SOURCE</c>, <c>DEF_CONVERSATION_PARTICIPANTS</c>,
/// <c>DEF_STAGE_KIND</c>), and left out of the canonical JSON when unused, so every existing hash is unchanged. Each
/// construct's fixture loads, validates and has a pinned hash.
/// </summary>
public class ScaleConstructDefinitionTests
{
    /// <summary>Pinned hashes of the M70 fixtures: a change to the canonical form of these constructs changes them.</summary>
    public const string QaHash = "b002a893d5f3f6b319f897f4423096267ea878bc97258bd2ad01e7163c1ce1e9";
    public const string CallSimHash = "ac2fcac0d60922568d2740da6db425451a6b34f6701cefe47ad18b4d3d6662db";
    public const string TrendsHash = "2d721f7f17379e7e54666f6560370f32d6a8f06a300c93d86d1113476c16643a";

    private static PipelineDefinition Load(string pipelineId) => PipelineDefinitionLoader.Parse(FlowFixturePack.Yaml(pipelineId)).Definition!;

    private static PipelineDefinition Parse(string yaml)
    {
        var parsed = PipelineDefinitionLoader.Parse(yaml);
        Assert.Empty(parsed.Errors);
        return parsed.Definition!;
    }

    /// <summary>The (code, path) errors of fixture <paramref name="pipelineId"/> with <paramref name="find"/> replaced.</summary>
    private static List<(string Code, string Path)> FlowErrors(string pipelineId, string find, string replace) =>
        Errors(EditFixture(pipelineId, find, replace), FlowFixturePack.Catalog);

    private static string EditFixture(string pipelineId, string find, string replace) => Edit(find, replace, FlowFixturePack.Yaml(pipelineId));

    private static string Hash(string yaml) => DefinitionHash.Compute(PipelineDefinitionLoader.Parse(yaml).Definition!);

    // ---- fixtures and hashes ----

    public static TheoryData<string> FixtureIds => [FlowFixturePack.Qa, FlowFixturePack.CallSim, FlowFixturePack.Trends];

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR355_FR357_each_fixture_loads_and_validates_in_a_registry(string pipelineId)
    {
        var registry = new PipelineRegistry([new FlowFixturePack(pipelineId)], [], Array.Empty<JobHandlerRegistration>());

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(pipelineId, out var loaded));
        Assert.Empty(PipelineDefinitionResolver.MissingNames(registry, loaded.Definition!));
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR355_FR357_each_fixture_is_valid_against_the_catalog(string pipelineId) =>
        Assert.Empty(Errors(FlowFixturePack.Yaml(pipelineId), FlowFixturePack.Catalog));

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR355_FR357_the_canonical_json_reads_back_with_the_same_hash(string pipelineId)
    {
        var json = DefinitionHash.CanonicalJson(Load(pipelineId));
        var reread = PipelineDefinitionLoader.Parse(json);

        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(Load(pipelineId)), DefinitionHash.Compute(reread.Definition!));
    }

    [Fact]
    public void FR355_FR357_the_fixture_hashes_are_pinned()
    {
        Assert.Equal(QaHash, DefinitionHash.Compute(Load(FlowFixturePack.Qa)));
        Assert.Equal(CallSimHash, DefinitionHash.Compute(Load(FlowFixturePack.CallSim)));
        Assert.Equal(TrendsHash, DefinitionHash.Compute(Load(FlowFixturePack.Trends)));
    }

    [Fact]
    public void FR355_FR357_the_sdlc_and_existing_definitions_hash_as_before()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));

        var json = DefinitionHash.CanonicalJson(PipelineDefinitionLoader.Parse(Minimal).Definition!);
        foreach (var key in new[] { "map", "reduce", "conversation", "from_runs" })
        {
            Assert.DoesNotContain($"\"{key}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR355_FR356_the_new_codes_are_in_the_code_table()
    {
        Assert.Contains("DEF_MAP_SOURCE", DefinitionErrorCodes.All);
        Assert.Contains("DEF_CONVERSATION_PARTICIPANTS", DefinitionErrorCodes.All);
    }

    // ---- FR-355 map-reduce ----

    private const string QaMap = "map: { over: transcripts.items, agent: qa-scorer, max_parallel: 8, max_items: 1000, on_item_failure: skip, max_failed: 5% }";

    [Fact]
    public void FR355_the_loader_reads_map_and_reduce()
    {
        var stage = Load(FlowFixturePack.Qa).Stages[0];
        var map = stage.Map!;

        Assert.Equal("transcripts.items", map.Over);
        Assert.Equal("transcripts", map.SourceArtifact);
        Assert.Equal("items", map.ItemPath);
        Assert.Equal("qa-scorer", map.Agent);
        Assert.Null(map.Handler);
        Assert.Equal(8, map.MaxParallel);
        Assert.Equal(1000, map.MaxItems);
        Assert.Equal(MapItemFailure.Skip, map.OnItemFailure);
        Assert.Equal(new MapFailureLimit(5, IsPercent: true), map.MaxFailed);
        Assert.Equal(new ReduceDefinition("qa-summariser", null), stage.Reduce);
        Assert.Equal(StageKind.Manual, stage.Kind);
    }

    [Fact]
    public void FR355_map_defaults_are_8_parallel_1000_items_skip_and_5_percent_and_writing_them_out_keeps_the_hash()
    {
        var yaml = EditFixture(FlowFixturePack.Qa, QaMap, "map: { over: transcripts.items, agent: qa-scorer }");
        var map = Parse(yaml).Stages[0].Map!;

        Assert.Equal(MapDefinition.DefaultMaxParallel, map.MaxParallel);
        Assert.Equal(8, map.MaxParallel);
        Assert.Equal(1000, map.MaxItems);
        Assert.Equal(MapItemFailure.Skip, map.OnItemFailure);
        Assert.Equal(MapFailureLimit.Default, map.MaxFailed);
        Assert.Equal("5%", map.MaxFailed.ToWire());
        Assert.Equal(Hash(FlowFixturePack.Yaml(FlowFixturePack.Qa)), Hash(yaml));
    }

    [Fact]
    public void FR355_a_changed_map_value_changes_the_hash()
    {
        var yaml = EditFixture(FlowFixturePack.Qa, "on_item_failure: skip, max_failed: 5%", "on_item_failure: fail, max_failed: 3");

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        var map = Parse(yaml).Stages[0].Map!;
        Assert.Equal(MapItemFailure.Fail, map.OnItemFailure);
        Assert.Equal(new MapFailureLimit(3, IsPercent: false), map.MaxFailed);
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Qa)), Hash(yaml));
        Assert.Equal(Hash(yaml), DefinitionHash.Compute(Parse(DefinitionHash.CanonicalJson(Parse(yaml)))));
    }

    [Theory]
    [InlineData(200, 5, true, 10)] // §4.68: 200 items at 5% allow 10
    [InlineData(199, 5, true, 9)] // rounded down
    [InlineData(10, 5, true, 0)]
    [InlineData(200, 3, false, 3)]
    [InlineData(0, 100, true, 0)]
    public void FR355_max_failed_is_a_count_or_a_percentage_rounded_down(int total, int value, bool percent, int allowed) =>
        Assert.Equal(allowed, new MapFailureLimit(value, percent).Allowed(total));

    [Theory]
    [InlineData("5%", 5, true)]
    [InlineData("0%", 0, true)]
    [InlineData("12", 12, false)]
    public void FR355_max_failed_parses_counts_and_percentages(string text, int value, bool percent) =>
        Assert.Equal(new MapFailureLimit(value, percent), MapFailureLimit.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("%")]
    [InlineData("5.5%")]
    [InlineData("-1")]
    [InlineData("five")]
    public void FR355_max_failed_refuses_other_text(string text) => Assert.Null(MapFailureLimit.Parse(text));

    [Fact]
    public void FR355_DEF_PARSE_max_failed_that_is_neither_a_count_nor_a_percentage() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "stages[0].map.max_failed")],
            FlowErrors(FlowFixturePack.Qa, "max_failed: 5%", "max_failed: lots"));

    [Theory]
    [InlineData("max_parallel: 8", "max_parallel: 0", "stages[0].map.max_parallel")]
    [InlineData("max_parallel: 8", "max_parallel: 101", "stages[0].map.max_parallel")]
    [InlineData("max_items: 1000", "max_items: 1001", "stages[0].map.max_items")]
    [InlineData("max_items: 1000", "max_items: 0", "stages[0].map.max_items")]
    [InlineData("max_failed: 5%", "max_failed: 101%", "stages[0].map.max_failed")]
    [InlineData("on_item_failure: skip", "on_item_failure: retry", "stages[0].map.on_item_failure")]
    public void FR355_DEF_PARSE_map_limits_out_of_range(string find, string replace, string path) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, path)], FlowErrors(FlowFixturePack.Qa, find, replace));

    [Fact]
    public void FR355_a_map_may_use_a_registered_handler_and_reduce_may_too()
    {
        var yaml = EditFixture(FlowFixturePack.Qa, "agent: qa-scorer, max_parallel", $"handler: {FlowFixturePack.QaTallyHandler}, max_parallel");

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Equal(FlowFixturePack.QaTallyHandler, Parse(yaml).Stages[0].Map!.Handler);
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Qa)), Hash(yaml));
        Assert.Empty(FlowErrors(FlowFixturePack.Qa, "reduce: { agent: qa-summariser }", $"reduce: {{ handler: {FlowFixturePack.QaTallyHandler} }}"));
    }

    [Theory]
    [InlineData("agent: qa-scorer, max_parallel", "agent: qa-scorer, handler: flow.qa-tally, max_parallel", "stages[0].map")]
    [InlineData("agent: qa-scorer, max_parallel", "max_parallel", "stages[0].map")]
    [InlineData("reduce: { agent: qa-summariser }", "reduce: { agent: qa-summariser, handler: flow.qa-tally }", "stages[0].reduce")]
    [InlineData("reduce: { agent: qa-summariser }", "reduce: {}", "stages[0].reduce")]
    public void FR355_DEF_PARSE_map_and_reduce_need_exactly_one_of_agent_and_handler(string find, string replace, string path) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, path)], FlowErrors(FlowFixturePack.Qa, find, replace));

    [Fact]
    public void FR355_DEF_FANOUT_AGENT_a_map_or_reduce_agent_the_stage_doesnt_bind()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.FanOutAgent, "stages[0].map.agent")],
            FlowErrors(FlowFixturePack.Qa, "agent: qa-scorer, max_parallel", "agent: qa-grader, max_parallel"));
        Assert.Equal(
            [(DefinitionErrorCodes.FanOutAgent, "stages[0].reduce.agent")],
            FlowErrors(FlowFixturePack.Qa, "reduce: { agent: qa-summariser }", "reduce: { agent: qa-editor }"));
    }

    [Fact]
    public void FR355_DEF_UNKNOWN_HANDLER_a_map_handler_no_pack_registers() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownHandler, "stages[0].map.handler")],
            FlowErrors(FlowFixturePack.Qa, "agent: qa-scorer, max_parallel", "handler: flow.qa-missing, max_parallel"));

    [Fact]
    public void FR355_DEF_DUPLICATE_ID_map_and_reduce_naming_one_handler() =>
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "stages[0].reduce.handler")],
            Errors(
                Edit("reduce: { agent: qa-summariser }", "reduce: { handler: flow.qa-tally }",
                    EditFixture(FlowFixturePack.Qa, "agent: qa-scorer, max_parallel", "handler: flow.qa-tally, max_parallel")),
                FlowFixturePack.Catalog));

    [Fact]
    public void FR355_DEF_STAGE_KIND_reduce_without_map() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].reduce"), FlowErrors(FlowFixturePack.Qa, $"    {QaMap}\n", string.Empty));

    [Fact]
    public void FR355_DEF_STAGE_KIND_a_map_stage_with_its_own_agent() =>
        Assert.Equal(
            [(DefinitionErrorCodes.StageKind, "stages[0].map")],
            FlowErrors(FlowFixturePack.Qa, "    output: scores\n", "    output: scores\n    agent: { key: qa-lead, prompt: flow.qa-score }\n"));

    [Fact]
    public void FR355_DEF_PARSE_a_map_stage_needs_an_output() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].output"), FlowErrors(FlowFixturePack.Qa, "    output: scores\n", string.Empty));

    [Theory]
    [InlineData("over: transcripts.items", "over: calls.items")] // not declared
    [InlineData("over: transcripts.items", "over: scores.items")] // declared, but this stage's own output
    [InlineData("over: transcripts.items", "over: transcripts..items")] // not an array path
    [InlineData("over: transcripts.items", "over: transcripts.items-all")]
    [InlineData("over: transcripts.items, ", "")] // missing
    public void FR355_DEF_MAP_SOURCE_over_that_isnt_a_readable_artifact_array(string find, string replace) =>
        Assert.Equal([(DefinitionErrorCodes.MapSource, "stages[0].map.over")], FlowErrors(FlowFixturePack.Qa, find, replace));

    [Fact]
    public void FR355_DEF_MAP_SOURCE_an_artifact_the_stage_doesnt_read() =>
        Assert.Equal(
            [(DefinitionErrorCodes.MapSource, "stages[0].map.over")],
            FlowErrors(FlowFixturePack.Qa, "    inputs: [transcripts]\n", string.Empty));

    [Fact]
    public void FR355_DEF_MAP_SOURCE_a_markdown_artifact() =>
        Assert.Equal(
            [(DefinitionErrorCodes.MapSource, "stages[0].map.over")],
            FlowErrors(FlowFixturePack.Qa, "transcripts: { title: Transcripts, format: json", "transcripts: { title: Transcripts, format: markdown"));

    [Fact]
    public void FR355_over_may_name_the_artifact_itself_or_an_earlier_stages_output()
    {
        Assert.Empty(FlowErrors(FlowFixturePack.Qa, "over: transcripts.items", "over: transcripts"));

        var yaml = EditFixture(FlowFixturePack.Qa, "start: [score]", "start: [collect]");
        yaml = Edit("format: json, approval: auto_lock, cascade: none, input: true }", "format: json, approval: auto_lock, cascade: none }", yaml);
        yaml = Edit("stages:\n", """
            stages:
              - id: collect
                title: Collect transcripts
                status: scoring
                agent: { key: qa-collector, prompt: flow.qa-score }
                output: transcripts

            """, yaml);
        yaml = Edit("    inputs: [transcripts]\n", string.Empty, yaml);
        yaml = Edit("    requires:\n      - { input: transcripts }\n", string.Empty, yaml);

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR355_DEF_STAGE_KIND_map_combined_with_a_conversation() =>
        Assert.Contains(
            (DefinitionErrorCodes.StageKind, "stages[0]"),
            FlowErrors(FlowFixturePack.Qa, "    reduce: { agent: qa-summariser }\n", """
                    reduce: { agent: qa-summariser }
                    conversation:
                      participants: [{ agent: qa-scorer, role: a }, { agent: qa-summariser, role: b }]
                      opener: a

                """));

    [Fact]
    public void FR355_DEF_PARSE_a_map_inside_a_parallel_branch() =>
        Assert.Contains(
            (DefinitionErrorCodes.Parse, "stages[0].parallel.branches[1][0].map"),
            FlowErrors(FlowFixturePack.Parallel, "            inputs: [ticker]\n            output: bear_case", "            inputs: [ticker]\n            output: bear_case\n            map: { over: ticker, agent: bear-analyst }"));

    [Fact]
    public void FR355_map_and_reduce_handlers_the_registry_lacks_are_missing_names()
    {
        var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Qa)], [], Array.Empty<JobHandlerRegistration>());
        var definition = PipelineDefinitionLoader.Parse(
            Edit("reduce: { agent: qa-summariser }", "reduce: { handler: flow.qa-gone }",
                EditFixture(FlowFixturePack.Qa, "agent: qa-scorer, max_parallel", "handler: flow.qa-lost, max_parallel"))).Definition!;

        Assert.Equal(["handler:flow.qa-lost", "handler:flow.qa-gone"], PipelineDefinitionResolver.MissingNames(registry, definition));
    }

    // ---- FR-356 conversation ----

    private const string Participants = "        - { agent: customer-sim, role: customer }\n        - { agent: support-sim, role: agent }\n";

    [Fact]
    public void FR356_the_loader_reads_a_conversation()
    {
        var stage = Load(FlowFixturePack.CallSim).Stages[0];
        var conversation = stage.Conversation!;

        Assert.Equal([new ConversationParticipant("customer-sim", "customer"), new ConversationParticipant("support-sim", "agent")], conversation.Participants);
        Assert.Equal("customer", conversation.Opener);
        Assert.Equal(6, conversation.TurnLimit);
        Assert.Equal(new ConversationStop(null, "goodbye"), conversation.Stop);
        Assert.Equal("transcript", stage.Output);
        Assert.Equal(StageKind.Manual, stage.Kind);
    }

    [Fact]
    public void FR356_max_turns_defaults_to_20_and_writing_it_out_keeps_the_hash()
    {
        var yaml = EditFixture(FlowFixturePack.CallSim, "      max_turns: 6\n", string.Empty);

        Assert.Equal(ConversationDefinition.DefaultMaxTurns, Parse(yaml).Stages[0].Conversation!.TurnLimit);
        Assert.Equal(20, ConversationDefinition.DefaultMaxTurns);
        Assert.Equal(Hash(yaml), Hash(Edit("      max_turns: 6\n", "      max_turns: 20\n", FlowFixturePack.Yaml(FlowFixturePack.CallSim))));
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.CallSim)), Hash(yaml));
    }

    [Fact]
    public void FR356_stop_is_optional_and_may_be_a_registered_guard()
    {
        var noStop = EditFixture(FlowFixturePack.CallSim, "      stop: { phrase: goodbye }\n", string.Empty);
        Assert.Empty(Errors(noStop, FlowFixturePack.Catalog));
        Assert.Null(Parse(noStop).Stages[0].Conversation!.Stop);

        var guard = EditFixture(FlowFixturePack.CallSim, "stop: { phrase: goodbye }", $"stop: {{ guard: {FlowFixturePack.CallResolvedGuard} }}");
        Assert.Empty(Errors(guard, FlowFixturePack.Catalog));
        Assert.Equal(new ConversationStop(FlowFixturePack.CallResolvedGuard, null), Parse(guard).Stages[0].Conversation!.Stop);
        Assert.Equal(Hash(guard), DefinitionHash.Compute(Parse(DefinitionHash.CanonicalJson(Parse(guard)))));
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.CallSim)), Hash(guard));
    }

    [Theory]
    [InlineData("stop: { phrase: goodbye }", "stop: { phrase: goodbye, guard: flow.call-resolved }", "stages[0].conversation.stop")]
    [InlineData("stop: { phrase: goodbye }", "stop: {}", "stages[0].conversation.stop")]
    [InlineData("stop: { phrase: goodbye }", "stop: { phrase: '  ' }", "stages[0].conversation.stop.phrase")]
    [InlineData("max_turns: 6", "max_turns: 0", "stages[0].conversation.max_turns")]
    [InlineData("max_turns: 6", "max_turns: 101", "stages[0].conversation.max_turns")]
    public void FR356_DEF_PARSE_stop_and_max_turns(string find, string replace, string path) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, path)], FlowErrors(FlowFixturePack.CallSim, find, replace));

    [Fact]
    public void FR356_DEF_UNKNOWN_GUARD_a_stop_guard_no_pack_registers() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownGuard, "stages[0].conversation.stop.guard")],
            FlowErrors(FlowFixturePack.CallSim, "stop: { phrase: goodbye }", "stop: { guard: flow.call-missing }"));

    [Fact]
    public void FR356_DEF_CONVERSATION_PARTICIPANTS_one_participant_is_too_few() =>
        Assert.Contains(
            (DefinitionErrorCodes.ConversationParticipants, "stages[0].conversation.participants"),
            FlowErrors(FlowFixturePack.CallSim, Participants, "        - { agent: customer-sim, role: customer }\n"));

    [Fact]
    public void FR356_DEF_CONVERSATION_PARTICIPANTS_seven_participants_are_too_many()
    {
        var participants = string.Concat(Enumerable.Range(1, 7).Select(i => $"        - {{ agent: sim-{i}, role: r{i} }}\n"));
        var yaml = EditFixture(FlowFixturePack.CallSim, Participants, participants);

        Assert.Contains((DefinitionErrorCodes.ConversationParticipants, "stages[0].conversation.participants"), Errors(yaml, FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR356_DEF_CONVERSATION_PARTICIPANTS_six_participants_are_allowed()
    {
        var participants = string.Concat(Enumerable.Range(1, 6).Select(i => $"        - {{ agent: sim-{i}, role: r{i} }}\n"));
        var agents = string.Concat(Enumerable.Range(1, 6).Select(i => $"      - {{ key: sim-{i}, job_type: flow.sim-{i}, prompt: flow.callsim-customer }}\n"));
        var yaml = EditFixture(FlowFixturePack.CallSim, Participants, participants);
        yaml = Edit(
            "      - { key: customer-sim, job_type: flow.callsim-customer, prompt: flow.callsim-customer }\n      - { key: support-sim, job_type: flow.callsim-support, prompt: flow.callsim-support }\n",
            agents, yaml);
        yaml = Edit("opener: customer", "opener: r1", yaml);
        var catalog = FlowFixturePack.Catalog with { JobTypes = [.. Enumerable.Range(1, 6).Select(i => $"flow.sim-{i}")] };

        Assert.Empty(Errors(yaml, catalog));
    }

    [Theory]
    [InlineData("role: agent }", "role: customer }", "stages[0].conversation.participants[1].role")] // a role twice
    [InlineData("role: agent }", "role: '' }", "stages[0].conversation.participants[1].role")] // no role
    [InlineData("agent: support-sim, role", "agent: customer-sim, role", "stages[0].conversation.participants[1].agent")] // an agent twice
    [InlineData("opener: customer", "opener: caller", "stages[0].conversation.opener")] // not a role
    [InlineData("      opener: customer\n", "", "stages[0].conversation.opener")] // missing
    public void FR356_DEF_CONVERSATION_PARTICIPANTS_roles_agents_and_the_opener(string find, string replace, string path) =>
        Assert.Equal([(DefinitionErrorCodes.ConversationParticipants, path)], FlowErrors(FlowFixturePack.CallSim, find, replace));

    [Fact]
    public void FR356_DEF_FANOUT_AGENT_a_participant_the_stage_doesnt_bind() =>
        Assert.Equal(
            [(DefinitionErrorCodes.FanOutAgent, "stages[0].conversation.participants[1].agent")],
            FlowErrors(FlowFixturePack.CallSim, "agent: support-sim, role", "agent: sales-sim, role"));

    [Fact]
    public void FR356_DEF_STAGE_KIND_a_conversation_stage_with_its_own_agent() =>
        Assert.Equal(
            [(DefinitionErrorCodes.StageKind, "stages[0].conversation")],
            FlowErrors(FlowFixturePack.CallSim, "    output: transcript\n", "    output: transcript\n    handler: flow.qa-tally\n"));

    [Fact]
    public void FR356_DEF_PARSE_a_conversation_stage_needs_an_output() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].output"), FlowErrors(FlowFixturePack.CallSim, "    output: transcript\n", string.Empty));

    [Fact]
    public void FR356_a_stop_guard_the_registry_lacks_is_a_missing_name()
    {
        var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.CallSim)], [], Array.Empty<JobHandlerRegistration>());
        var definition = Parse(EditFixture(FlowFixturePack.CallSim, "stop: { phrase: goodbye }", "stop: { guard: flow.call-gone }"));

        Assert.Equal(["guard:flow.call-gone"], PipelineDefinitionResolver.MissingNames(registry, definition));
    }

    [Fact]
    public void FR355_FR356_DEF_STAGE_KIND_counts_map_and_conversation_with_fan_out_and_variants()
    {
        var errors = FlowErrors(FlowFixturePack.CallSim, "      stop: { phrase: goodbye }\n", "      stop: { phrase: goodbye }\n    variants: { count: 2, select: gate }\n");

        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0]"), errors);
    }

    // ---- FR-357 from_runs ----

    private const string FromRuns = "      - from_runs: { pipeline: fixture-qa, artifact: scores, window: 7d, status: completed }\n";

    [Fact]
    public void FR357_the_loader_reads_a_from_runs_input()
    {
        var stage = Load(FlowFixturePack.Trends).Stages[0];

        Assert.Empty(stage.Inputs);
        var runs = Assert.Single(stage.FromRuns);
        Assert.Equal(new RunsInputDefinition("fixture-qa", "scores", "7d") { Status = GenericRunStatus.Completed, Position = 0 }, runs);
    }

    [Fact]
    public void FR357_status_defaults_to_completed_and_writing_it_out_keeps_the_hash()
    {
        var yaml = EditFixture(FlowFixturePack.Trends, ", status: completed }", " }");

        Assert.Equal(GenericRunStatus.Completed, Parse(yaml).Stages[0].FromRuns[0].Status);
        Assert.Equal(Hash(FlowFixturePack.Yaml(FlowFixturePack.Trends)), Hash(yaml));
        Assert.Empty(Errors(EditFixture(FlowFixturePack.Trends, "status: completed", "status: failed"), FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR357_from_runs_keeps_its_place_among_plain_inputs()
    {
        var yaml = EditFixture(FlowFixturePack.Trends, FromRuns, "      - focus\n" + FromRuns + "      - notes\n");
        yaml = Edit("  trend_report:", "  focus: { title: Focus, format: markdown, approval: auto_lock, cascade: none, input: true }\n  notes: { title: Notes, format: markdown, approval: auto_lock, cascade: none, input: true }\n  trend_report:", yaml);
        var stage = Parse(yaml).Stages[0];

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Equal(["focus", "notes"], stage.Inputs);
        Assert.Equal(1, Assert.Single(stage.FromRuns).Position);
        var json = DefinitionHash.CanonicalJson(Parse(yaml));
        Assert.Contains("\"inputs\":[\"focus\",{\"from_runs\":{", json, StringComparison.Ordinal);
        Assert.Equal(Hash(yaml), DefinitionHash.Compute(Parse(json)));
        Assert.NotEqual(Hash(yaml), Hash(Edit("      - focus\n" + FromRuns, FromRuns + "      - focus\n", yaml)));
    }

    [Theory]
    [InlineData("window: 7d", "window: 7w", "stages[0].inputs[0].from_runs.window")]
    [InlineData("window: 7d", "window: 0d", "stages[0].inputs[0].from_runs.window")]
    [InlineData("window: 7d, ", "", "stages[0].inputs[0].from_runs.window")]
    [InlineData("status: completed", "status: running", "stages[0].inputs[0].from_runs.status")]
    [InlineData("status: completed", "status: finished", "stages[0].inputs[0].from_runs.status")]
    [InlineData("pipeline: fixture-qa", "pipeline: Fixture_QA", "stages[0].inputs[0].from_runs.pipeline")]
    [InlineData("pipeline: fixture-qa, ", "", "stages[0].inputs[0].from_runs.pipeline")]
    [InlineData("artifact: scores, ", "", "stages[0].inputs[0].from_runs.artifact")]
    [InlineData("status: completed }", "status: completed, since: 1d }", "stages[0].inputs[0].from_runs.since")]
    public void FR357_DEF_PARSE_from_runs_fields(string find, string replace, string path) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, path)], FlowErrors(FlowFixturePack.Trends, find, replace));

    [Fact]
    public void FR357_DEF_PARSE_an_input_mapping_that_isnt_from_runs() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].inputs[0].latest"), FlowErrors(FlowFixturePack.Trends, "- from_runs: {", "- latest: {"));

    [Fact]
    public void FR357_DEF_UNKNOWN_ARTIFACT_from_runs_of_this_pipeline_names_an_undeclared_artifact()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownArtifact, "stages[0].inputs[0].from_runs.artifact")],
            FlowErrors(FlowFixturePack.Trends, "pipeline: fixture-qa", "pipeline: fixture-trends"));
        Assert.Empty(FlowErrors(FlowFixturePack.Trends, "pipeline: fixture-qa, artifact: scores", "pipeline: fixture-trends, artifact: trend_report"));
    }
}
