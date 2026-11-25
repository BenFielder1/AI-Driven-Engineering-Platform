using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-343–FR-349 (M68, §3.4, §4.49 amended): the flow constructs' definition syntax: loaded, validated, and left out of
/// the canonical JSON when unused, so every existing hash is unchanged. Each construct's fixture loads and validates.
/// </summary>
public class FlowConstructDefinitionTests
{
    private static PipelineDefinition Load(string pipelineId) => PipelineDefinitionLoader.Parse(FlowFixturePack.Yaml(pipelineId)).Definition!;

    /// <summary>The (code, path) errors of fixture <paramref name="pipelineId"/> with <paramref name="find"/> replaced.</summary>
    private static List<(string Code, string Path)> FlowErrors(string pipelineId, string find, string replace) =>
        Errors(Edit(find, replace, FlowFixturePack.Yaml(pipelineId)), FlowFixturePack.Catalog);

    private static string Hash(string yaml) => DefinitionHash.Compute(PipelineDefinitionLoader.Parse(yaml).Definition!);

    // ---- fixtures and hashes ----

    public static TheoryData<string> FixtureIds => [.. FlowFixturePack.All];

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR343_FR349_each_flow_fixture_loads_and_validates_in_a_registry(string pipelineId)
    {
        var registry = new PipelineRegistry([new FlowFixturePack(pipelineId)], [], Array.Empty<JobHandlerRegistration>());

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(pipelineId, out var loaded));
        Assert.Equal(pipelineId, loaded.Definition!.Pipeline);
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR343_FR349_each_flow_fixture_is_valid_against_the_catalog(string pipelineId) =>
        Assert.Empty(Errors(FlowFixturePack.Yaml(pipelineId), FlowFixturePack.Catalog));

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void FR343_FR349_the_canonical_json_reads_back_with_the_same_hash(string pipelineId)
    {
        var json = DefinitionHash.CanonicalJson(Load(pipelineId));
        var reread = PipelineDefinitionLoader.Parse(json);

        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(Load(pipelineId)), DefinitionHash.Compute(reread.Definition!));
    }

    [Fact]
    public void FR343_FR349_the_sdlc_and_existing_definitions_hash_as_before()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));

        var json = DefinitionHash.CanonicalJson(PipelineDefinitionLoader.Parse(Minimal).Definition!);
        foreach (var key in new[] { "parallel", "veto", "on_veto", "fan_out", "aggregate", "routes", "variants", "limits" })
        {
            Assert.DoesNotContain($"\"{key}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR343_FR349_the_new_codes_are_in_the_code_table()
    {
        Assert.Contains("DEF_BRANCH_INPUT", DefinitionErrorCodes.All);
        Assert.Contains("DEF_ROUTE_TARGET", DefinitionErrorCodes.All);
        Assert.Contains("DEF_FANOUT_AGENT", DefinitionErrorCodes.All);
    }

    // ---- FR-343 parallel ----

    private const string BearBranch = """
                - - id: bear
                    title: Bear analyst
                    status: analysing
                    agent: { key: bear-analyst, prompt: flow.bear }
                    inputs: [ticker]
                    output: bear_case

        """;

    [Fact]
    public void FR343_the_loader_reads_a_parallel_group()
    {
        var definition = Load(FlowFixturePack.Parallel);
        var group = definition.Stages[0];

        Assert.Equal(["analysts", "decide"], definition.Stages.Select(s => s.Id));
        Assert.Equal("analysts", group.Id);
        var parallel = group.Parallel!;
        Assert.Equal(ParallelJoin.All, parallel.Join);
        Assert.Equal(BranchFailurePolicy.Fail, parallel.OnBranchFailure);
        Assert.Equal([["bull", "bull_review"], ["bear"]], parallel.Branches.Select(b => b.Select(s => s.Id).ToList()));
        Assert.Equal(["analysts", "bull", "bull_review", "bear", "decide"], definition.AllStages.Select(s => s.Id));
        Assert.Equal(StageKind.Agent, parallel.Branches[1][0].Kind);
    }

    [Fact]
    public void FR343_on_branch_failure_continue_is_read_and_changes_the_hash()
    {
        var yaml = Edit("on_branch_failure: fail", "on_branch_failure: continue", FlowFixturePack.Yaml(FlowFixturePack.Parallel));

        Assert.Equal(BranchFailurePolicy.Continue, PipelineDefinitionLoader.Parse(yaml).Definition!.Stages[0].Parallel!.OnBranchFailure);
        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Parallel)), Hash(yaml));
    }

    [Fact]
    public void FR343_the_group_id_may_come_from_parallel_id_alone()
    {
        var yaml = Edit("  - id: analysts\n    title: Analysts", "  - title: Analysts", FlowFixturePack.Yaml(FlowFixturePack.Parallel));

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Equal("analysts", PipelineDefinitionLoader.Parse(yaml).Definition!.Stages[0].Id);
    }

    [Fact]
    public void FR343_DEF_BRANCH_INPUT_a_branch_stage_reads_another_branchs_output() =>
        Assert.Equal(
            [(DefinitionErrorCodes.BranchInput, "stages[0].parallel.branches[1][0].inputs[1]")],
            FlowErrors(FlowFixturePack.Parallel, "            inputs: [ticker]\n            output: bear_case", "            inputs: [ticker, bull_case]\n            output: bear_case"));

    [Fact]
    public void FR343_DEF_BRANCH_INPUT_two_branches_produce_the_same_artifact() =>
        Assert.Contains(
            (DefinitionErrorCodes.BranchInput, "stages[0].parallel.branches[1][0]"),
            FlowErrors(FlowFixturePack.Parallel, "            inputs: [ticker]\n            output: bear_case", "            inputs: [ticker]\n            output: bull_case"));

    [Fact]
    public void FR343_a_branch_stage_may_read_an_earlier_stage_of_its_own_branch() =>
        Assert.Contains("inputs: [ticker, bull_case]", FlowFixturePack.Yaml(FlowFixturePack.Parallel), StringComparison.Ordinal);

    [Fact]
    public void FR343_DEF_BRANCH_INPUT_also_applies_to_a_branch_stages_bound_agents() =>
        Assert.Contains(
            (DefinitionErrorCodes.BranchInput, "stages[0].parallel.branches[1][0].agents[0].inputs[0]"),
            FlowErrors(FlowFixturePack.Parallel, "            output: bear_case\n",
                "            output: bear_case\n            agents:\n              - { key: bear-checker, job_type: flow.judge, inputs: [bull_case] }\n"));

    [Fact]
    public void FR343_DEF_PARSE_a_group_needs_two_branches() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.branches"), FlowErrors(FlowFixturePack.Parallel, BearBranch, "\n"));

    [Fact]
    public void FR343_DEF_PARSE_a_branch_needs_a_stage() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.branches[1]"), FlowErrors(FlowFixturePack.Parallel, BearBranch, "        - []\n\n"));

    [Fact]
    public void FR343_DEF_PARSE_a_branch_is_a_list_of_stages() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.branches[1]"), FlowErrors(FlowFixturePack.Parallel, BearBranch, "        - bear\n\n"));

    [Fact]
    public void FR343_DEF_DUPLICATE_ID_branch_stage_ids_are_unique_across_branches() =>
        Assert.Contains((DefinitionErrorCodes.DuplicateId, "stages[0].parallel.branches[1][0].id"), FlowErrors(FlowFixturePack.Parallel, "- - id: bear", "- - id: bull"));

    [Fact]
    public void FR343_DEF_DUPLICATE_ID_branch_stage_ids_are_unique_across_the_pipeline() =>
        Assert.Contains((DefinitionErrorCodes.DuplicateId, "stages[0].parallel.branches[1][0].id"), FlowErrors(FlowFixturePack.Parallel, "- - id: bear", "- - id: decide"));

    [Fact]
    public void FR343_DEF_PARSE_parallel_id_must_match_the_stage_id() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.id"), FlowErrors(FlowFixturePack.Parallel, "      id: analysts\n", "      id: analysts-group\n"));

    [Fact]
    public void FR343_DEF_PARSE_join_is_all() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.join"), FlowErrors(FlowFixturePack.Parallel, "join: all", "join: any"));

    [Fact]
    public void FR343_DEF_PARSE_on_branch_failure_is_fail_or_continue() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.on_branch_failure"), FlowErrors(FlowFixturePack.Parallel, "on_branch_failure: fail", "on_branch_failure: retry"));

    [Fact]
    public void FR343_DEF_STAGE_KIND_a_group_has_no_agent() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].parallel"),
            FlowErrors(FlowFixturePack.Parallel, "    status: analysing\n    requires:", "    status: analysing\n    agent: { key: lead }\n    requires:"));

    [Fact]
    public void FR343_DEF_PARSE_a_branch_stage_has_no_transitions() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.branches[1][0].on_success"),
            FlowErrors(FlowFixturePack.Parallel, "            output: bear_case\n", "            output: bear_case\n            on_success: decide\n"));

    [Fact]
    public void FR343_branch_stages_are_validated_as_stages() =>
        Assert.Contains((DefinitionErrorCodes.UnknownStatus, "stages[0].parallel.branches[1][0].status"),
            FlowErrors(FlowFixturePack.Parallel, "            title: Bear analyst\n            status: analysing", "            title: Bear analyst\n            status: sulking"));

    // ---- FR-344 veto ----

    [Fact]
    public void FR344_the_loader_reads_veto_and_on_veto()
    {
        var size = Load(FlowFixturePack.Veto).FindStage("size")!;

        Assert.Equal([new VetoDefinition(FlowFixturePack.PositionLimitGuard, null), new VetoDefinition(null, "risk-manager")], size.Veto);
        Assert.Equal("propose", size.OnVeto!.To);
    }

    [Fact]
    public void FR344_on_veto_end_vetoed_is_valid_and_changes_the_hash()
    {
        var yaml = Edit("on_veto: { to: propose }", "on_veto: { end: vetoed }", FlowFixturePack.Yaml(FlowFixturePack.Veto));

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Null(PipelineDefinitionLoader.Parse(yaml).Definition!.FindStage("size")!.OnVeto!.To);
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Veto)), Hash(yaml));
    }

    [Theory]
    [InlineData("order")]
    [InlineData("size")]
    [InlineData("nowhere")]
    public void FR344_DEF_ROUTE_TARGET_on_veto_to_must_be_an_earlier_stage(string target) =>
        Assert.Equal([(DefinitionErrorCodes.RouteTarget, "stages[1].on_veto.to")],
            FlowErrors(FlowFixturePack.Veto, "on_veto: { to: propose }", $"on_veto: {{ to: {target} }}"));

    [Fact]
    public void FR344_DEF_UNKNOWN_GUARD_a_veto_guard_must_be_registered() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownGuard, "stages[1].veto[0].guard")],
            FlowErrors(FlowFixturePack.Veto, "{ guard: flow.position-limit }", "{ guard: flow.no-such-guard }"));

    [Fact]
    public void FR344_DEF_FANOUT_AGENT_a_veto_agent_must_be_bound_by_the_stage() =>
        Assert.Equal([(DefinitionErrorCodes.FanOutAgent, "stages[1].veto[1].agent")],
            FlowErrors(FlowFixturePack.Veto, "{ agent: risk-manager }", "{ agent: compliance }"));

    [Fact]
    public void FR344_DEF_PARSE_a_veto_entry_is_a_guard_or_an_agent() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[1].veto[0]"),
            FlowErrors(FlowFixturePack.Veto, "{ guard: flow.position-limit }", "{ guard: flow.position-limit, agent: risk-manager }"));

    [Fact]
    public void FR343_a_handler_declared_by_a_branch_stage_is_found_for_dispatch()
    {
        var yaml = Edit(
            "            agent: { key: bear-analyst, prompt: flow.bear }\n            inputs: [ticker]\n            output: bear_case\n",
            "            handler: flow.bear-desk\n",
            FlowFixturePack.Yaml(FlowFixturePack.Parallel));

        Assert.Equal("bear", StageHandlers.StageOf(PipelineDefinitionLoader.Parse(yaml).Definition!, "flow.bear-desk")?.Id);
    }

    [Fact]
    public void FR343_FR344_DEF_PARSE_a_branch_stage_may_not_declare_a_veto() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].parallel.branches[1][0].veto"),
            FlowErrors(
                FlowFixturePack.Parallel,
                "            output: bear_case\n",
                "            output: bear_case\n            veto:\n              - { guard: flow.position-limit }\n            on_veto: { end: vetoed }\n"));

    [Fact]
    public void FR344_DEF_PARSE_a_veto_needs_on_veto() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].on_veto")],
            FlowErrors(FlowFixturePack.Veto, "    on_veto: { to: propose }\n", string.Empty));

    [Fact]
    public void FR344_DEF_PARSE_on_veto_needs_a_veto() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[2].on_veto"),
            FlowErrors(FlowFixturePack.Veto, "    output: order\n", "    output: order\n    on_veto: { end: vetoed }\n"));

    [Fact]
    public void FR344_DEF_PARSE_on_veto_end_is_vetoed() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[1].on_veto.end"),
            FlowErrors(FlowFixturePack.Veto, "on_veto: { to: propose }", "on_veto: { end: rejected }"));

    [Fact]
    public void FR344_DEF_PARSE_on_veto_is_to_or_end_not_both() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[1].on_veto"),
            FlowErrors(FlowFixturePack.Veto, "on_veto: { to: propose }", "on_veto: { to: propose, end: vetoed }"));

    // ---- FR-345 fan-out ----

    [Fact]
    public void FR345_the_loader_reads_fan_out_agents()
    {
        var fanOut = Load(FlowFixturePack.Forecast).FindStage("forecast")!.FanOut!;

        Assert.Equal(["forecaster-base-rate", "forecaster-trend", "forecaster-contrarian"], fanOut.Agents);
        Assert.Null(fanOut.Count);
    }

    private const string FanOutAgents = "fan_out: { agents: [forecaster-base-rate, forecaster-trend, forecaster-contrarian] }";

    [Fact]
    public void FR345_fan_out_count_is_valid_and_changes_the_hash()
    {
        var yaml = Edit(FanOutAgents, "fan_out: { count: 3 }", FlowFixturePack.Yaml(FlowFixturePack.Forecast));

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Equal(3, PipelineDefinitionLoader.Parse(yaml).Definition!.Stages[0].FanOut!.Count);
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Forecast)), Hash(yaml));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void FR345_DEF_PARSE_fan_out_count_is_2_to_10(int count) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[0].fan_out.count")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, $"fan_out: {{ count: {count} }}"));

    [Fact]
    public void FR345_DEF_FANOUT_AGENT_a_fan_out_agent_must_be_bound_by_the_stage() =>
        Assert.Equal([(DefinitionErrorCodes.FanOutAgent, "stages[0].fan_out.agents[2]")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, "fan_out: { agents: [forecaster-base-rate, forecaster-trend, forecaster-optimist] }"));

    [Fact]
    public void FR345_DEF_STAGE_KIND_a_fan_out_stage_declares_no_routes() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].routes"),
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, FanOutAgents + "\n    routes: [forecast]"));

    [Fact]
    public void FR345_DEF_STAGE_KIND_a_fan_out_stage_does_not_escalate_an_invalid_output()
    {
        Assert.Equal([(DefinitionErrorCodes.StageKind, "stages[0].on_invalid.then")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, FanOutAgents + "\n    validate: [{ guard: flow.forecast-spread }]\n    on_invalid: { retry_with_feedback: true, max_attempts: 2, then: escalate }"));
        Assert.Empty(FlowErrors(FlowFixturePack.Forecast, FanOutAgents, FanOutAgents + "\n    validate: [{ guard: flow.forecast-spread }]\n    on_invalid: { retry_with_feedback: true, max_attempts: 2, then: fail }"));
    }

    [Fact]
    public void FR345_fixture_forecast_and_its_leak_copy_validate()
    {
        var yaml = FlowFixturePack.Yaml(FlowFixturePack.Forecast);
        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Empty(Errors(Edit("pipeline: fixture-forecast\n", "pipeline: fixture-forecast-leak\n", yaml), FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR345_DEF_FANOUT_AGENT_the_stages_own_agent_is_not_a_binding() =>
        Assert.Equal([(DefinitionErrorCodes.FanOutAgent, "stages[0].fan_out.agents[0]")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, "fan_out: { agents: [forecaster, forecaster-trend] }"));

    [Fact]
    public void FR345_DEF_DUPLICATE_ID_a_fan_out_agent_is_listed_once() =>
        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[0].fan_out.agents[1]")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, "fan_out: { agents: [forecaster-trend, forecaster-trend] }"));

    [Fact]
    public void FR345_DEF_PARSE_fan_out_needs_two_agents() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[0].fan_out.agents")],
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, "fan_out: { agents: [forecaster-trend] }"));

    [Fact]
    public void FR345_DEF_PARSE_fan_out_is_agents_or_count() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].fan_out"),
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, "fan_out: { agents: [forecaster-base-rate, forecaster-trend], count: 2 }"));

    [Fact]
    public void FR345_DEF_STAGE_KIND_fan_out_needs_an_agent_stage() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].fan_out"),
            FlowErrors(FlowFixturePack.Forecast, "    agent: { key: forecaster, prompt: flow.forecast }\n", string.Empty));

    [Fact]
    public void FR345_DEF_STAGE_KIND_fan_out_and_variants_on_one_stage() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0]"),
            FlowErrors(FlowFixturePack.Forecast, FanOutAgents, FanOutAgents + "\n    variants: { count: 2, select: gate }"));

    // ---- FR-346 aggregate ----

    [Fact]
    public void FR346_the_loader_reads_aggregate() =>
        Assert.Equal(new AggregateDefinition("forecast", FlowFixturePack.ForecastSpreadGuard), Load(FlowFixturePack.Forecast).FindStage("aggregate")!.Aggregate);

    [Fact]
    public void FR346_aggregate_without_disagreement_is_valid_and_hashes_differently()
    {
        var yaml = Edit("      from: forecast\n      disagreement: { guard: flow.forecast-spread }\n", "      from: forecast\n",
            FlowFixturePack.Yaml(FlowFixturePack.Forecast));

        Assert.Empty(Errors(yaml, FlowFixturePack.Catalog));
        Assert.Null(PipelineDefinitionLoader.Parse(yaml).Definition!.Stages[1].Aggregate!.DisagreementGuard);
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Forecast)), Hash(yaml));
    }

    [Fact]
    public void FR346_aggregate_may_read_a_parallel_group() =>
        Assert.Empty(FlowErrors(FlowFixturePack.Parallel, "    output: decision\n", "    output: decision\n    aggregate: { from: analysts }\n"));

    [Fact]
    public void FR346_DEF_UNKNOWN_STAGE_aggregate_from_an_unknown_stage() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownStage, "stages[1].aggregate.from")],
            FlowErrors(FlowFixturePack.Forecast, "      from: forecast\n", "      from: crystal-ball\n"));

    [Fact]
    public void FR346_DEF_PARSE_aggregate_from_a_stage_without_fan_out_or_parallel() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].aggregate.from")],
            FlowErrors(FlowFixturePack.Editorial, "    output: draft\n", "    output: draft\n    aggregate: { from: research }\n"));

    [Fact]
    public void FR346_DEF_PARSE_aggregate_from_must_be_an_earlier_stage() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].aggregate.from")],
            FlowErrors(FlowFixturePack.Forecast, "      from: forecast\n", "      from: aggregate\n"));

    [Fact]
    public void FR346_DEF_UNKNOWN_GUARD_the_disagreement_guard_must_be_registered() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownGuard, "stages[1].aggregate.disagreement.guard")],
            FlowErrors(FlowFixturePack.Forecast, "guard: flow.forecast-spread", "guard: flow.no-such-guard"));

    [Fact]
    public void FR346_DEF_STAGE_KIND_aggregate_needs_an_agent_or_handler_stage() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[1].aggregate"),
            FlowErrors(FlowFixturePack.Forecast, "    agent: { key: aggregator, prompt: flow.aggregate }\n", string.Empty));

    // ---- FR-347 routes ----

    [Fact]
    public void FR347_the_loader_reads_routes() =>
        Assert.Equal(["research", "write"], Load(FlowFixturePack.Editorial).FindStage("edit")!.Routes);

    [Theory]
    [InlineData("routes: [research, publish]", "stages[2].routes[1]")]
    [InlineData("routes: [edit]", "stages[2].routes[0]")]
    public void FR347_DEF_ROUTE_TARGET_a_route_to_an_unknown_stage_or_itself(string routes, string path) =>
        Assert.Equal([(DefinitionErrorCodes.RouteTarget, path)], FlowErrors(FlowFixturePack.Editorial, "routes: [research, write]", routes));

    [Fact]
    public void FR347_DEF_ROUTE_TARGET_a_route_to_a_later_stage() =>
        Assert.Equal([(DefinitionErrorCodes.RouteTarget, "stages[1].routes[0]")],
            FlowErrors(FlowFixturePack.Editorial, "    output: draft\n", "    output: draft\n    routes: [edit]\n"));

    [Fact]
    public void FR347_DEF_DUPLICATE_ID_a_route_is_listed_once() =>
        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "stages[2].routes[1]")],
            FlowErrors(FlowFixturePack.Editorial, "routes: [research, write]", "routes: [research, research]"));

    [Fact]
    public void FR347_DEF_STAGE_KIND_routes_need_an_agent_stage() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[2].routes"),
            FlowErrors(FlowFixturePack.Editorial, "    agent: { key: editor, prompt: flow.edit }\n", string.Empty));

    [Fact]
    public void FR347_routes_change_the_hash() =>
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Editorial)),
            Hash(Edit("routes: [research, write]", "routes: [research]", FlowFixturePack.Yaml(FlowFixturePack.Editorial))));

    // ---- FR-348 limits ----

    [Fact]
    public void FR348_the_loader_reads_limits() =>
        Assert.Equal(new RunLimits(3, InvalidOutcome.Escalate), Load(FlowFixturePack.Editorial).Limits);

    [Fact]
    public void FR348_limits_are_absent_by_default() => Assert.Null(Load(FlowFixturePack.Veto).Limits);

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void FR348_DEF_PARSE_revisions_is_1_to_100(int revisions) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "limits.revisions")],
            FlowErrors(FlowFixturePack.Editorial, "revisions: 3", $"revisions: {revisions}"));

    [Fact]
    public void FR348_DEF_PARSE_revisions_is_required() =>
        Assert.Contains((DefinitionErrorCodes.Parse, "limits.revisions"),
            FlowErrors(FlowFixturePack.Editorial, "limits: { revisions: 3, then: escalate }", "limits: { then: escalate }"));

    [Fact]
    public void FR348_DEF_PARSE_then_is_required_and_escalate_or_fail()
    {
        Assert.Contains((DefinitionErrorCodes.Parse, "limits.then"), FlowErrors(FlowFixturePack.Editorial, ", then: escalate }", " }"));
        Assert.Contains((DefinitionErrorCodes.Parse, "limits.then"), FlowErrors(FlowFixturePack.Editorial, "then: escalate", "then: retry"));
        Assert.Empty(FlowErrors(FlowFixturePack.Editorial, "then: escalate", "then: fail"));
    }

    [Fact]
    public void FR348_limits_change_the_hash()
    {
        var yaml = FlowFixturePack.Yaml(FlowFixturePack.Editorial);

        Assert.NotEqual(Hash(yaml), Hash(Edit("limits: { revisions: 3, then: escalate }\n", string.Empty, yaml)));
        Assert.NotEqual(Hash(yaml), Hash(Edit("revisions: 3", "revisions: 4", yaml)));
    }

    // ---- FR-349 variants ----

    [Fact]
    public void FR349_the_loader_reads_variants()
    {
        var definition = Load(FlowFixturePack.Variants);

        Assert.Equal(new VariantsDefinition(3, null), definition.FindStage("headline")!.Variants);
        Assert.Equal(new VariantsDefinition(2, "tagline-judge"), definition.FindStage("tagline")!.Variants);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void FR349_DEF_PARSE_variant_count_is_2_to_5(int count) =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[0].variants.count")],
            FlowErrors(FlowFixturePack.Variants, "variants: { count: 3, select: gate }", $"variants: {{ count: {count}, select: gate }}"));

    [Fact]
    public void FR349_DEF_FANOUT_AGENT_the_selector_agent_must_be_bound_by_the_stage() =>
        Assert.Equal([(DefinitionErrorCodes.FanOutAgent, "stages[1].variants.select.agent")],
            FlowErrors(FlowFixturePack.Variants, "select: { agent: tagline-judge }", "select: { agent: copy-chief }"));

    [Theory]
    [InlineData("variants: { count: 3 }")]
    [InlineData("variants: { count: 3, select: vote }")]
    [InlineData("variants: { count: 3, select: [gate] }")]
    public void FR349_DEF_PARSE_select_is_gate_or_an_agent(string variants) =>
        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].variants.select"),
            FlowErrors(FlowFixturePack.Variants, "variants: { count: 3, select: gate }", variants));

    [Fact]
    public void FR349_DEF_STAGE_KIND_variants_need_an_agent_stage() =>
        Assert.Contains((DefinitionErrorCodes.StageKind, "stages[0].variants"),
            FlowErrors(FlowFixturePack.Variants, "    agent: { key: copywriter, prompt: flow.headline }\n", string.Empty));

    [Fact]
    public void FR349_the_selector_changes_the_hash() =>
        Assert.NotEqual(Hash(FlowFixturePack.Yaml(FlowFixturePack.Variants)),
            Hash(Edit("select: { agent: tagline-judge }", "select: gate", FlowFixturePack.Yaml(FlowFixturePack.Variants))));
}
