using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-355, §4.68: the map's source array, item window, failure limit and placeholders, on <c>fixture-qa</c>.</summary>
public class MapRulesTests
{
    private static PipelineDefinition Qa
    {
        get
        {
            var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Qa)], [], Array.Empty<JobHandlerRegistration>());
            Assert.True(registry.TryGet(FlowFixturePack.Qa, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    private static StageDefinition Score => Qa.FindStage("score")!;

    private static MapDefinition Map(MapItemFailure onFailure = MapItemFailure.Skip, string maxFailed = "5%", int maxParallel = 8) =>
        new("transcripts.items", "qa-scorer", null)
        {
            OnItemFailure = onFailure,
            MaxFailed = MapFailureLimit.Parse(maxFailed)!,
            MaxParallel = maxParallel,
        };

    /// <summary><paramref name="total"/> items: the first <paramref name="failed"/> failed, the next <paramref name="completed"/>
    /// completed, the rest open (the first <paramref name="busy"/> of those busy).</summary>
    private static List<MapItemState> Items(int total, int completed = 0, int failed = 0, int busy = 0) =>
        [.. Enumerable.Range(0, total).Select(i => i < failed
            ? new MapItemState(i, MapItemStatus.Failed, false, false)
            : i < failed + completed
                ? new MapItemState(i, MapItemStatus.Completed, false, true)
                : new MapItemState(i, MapItemStatus.Open, i < failed + completed + busy, true))];

    [Fact]
    public void FR355_reads_the_array_at_the_item_path_as_json_text_per_element()
    {
        var read = MapRules.ReadItems("""{"items":[{"id":1},"two",3]}""", "items");

        Assert.Null(read.Problem);
        Assert.Equal(["""{"id":1}""", "\"two\"", "3"], read.Items);
    }

    [Fact]
    public void FR355_a_bare_key_means_the_artifact_is_the_array()
    {
        Assert.Equal(["1", "2"], MapRules.ReadItems("[1,2]", string.Empty).Items);
        Assert.Equal(["true"], MapRules.ReadItems("""{"a":{"b":[true]}}""", "a.b").Items);
    }

    [Theory]
    [InlineData("""{"items":{}}""", "items")]
    [InlineData("""{"other":[]}""", "items")]
    [InlineData("""{"items":[]}""", "")]
    [InlineData("not json", "items")]
    [InlineData("", "items")]
    public void FR355_a_source_that_is_not_an_array_there_is_a_problem(string json, string path)
    {
        var read = MapRules.ReadItems(json, path);

        Assert.NotNull(read.Problem);
        Assert.Empty(read.Items);
    }

    [Fact]
    public void FR355_section_4_68_1001_items_over_max_items_1000_are_too_many()
    {
        var map = Map();

        Assert.True(MapRules.TooMany(map, 1001));
        Assert.False(MapRules.TooMany(map, 1000));
        Assert.False(MapRules.TooMany(map, 0));
    }

    [Fact]
    public void FR355_section_4_68_first_items_start_in_index_order_up_to_max_parallel()
    {
        var next = Assert.IsType<MapNext.Start>(MapRules.Next(Map(), Items(200)));

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], next.Items);
    }

    [Fact]
    public void FR355_section_4_68_a_finished_item_frees_one_slot_for_the_next_idle_item()
    {
        // Items 0–7 queued; 0 finished, 1–7 still busy: only item 8 starts.
        var next = Assert.IsType<MapNext.Start>(MapRules.Next(Map(), Items(200, completed: 1, busy: 7)));

        Assert.Equal([8], next.Items);
    }

    [Fact]
    public void FR355_a_full_window_starts_nothing()
    {
        Assert.Empty(Assert.IsType<MapNext.Start>(MapRules.Next(Map(), Items(200, completed: 10, busy: 8))).Items);
    }

    [Fact]
    public void FR355_an_open_item_waiting_on_a_retry_is_not_started()
    {
        List<MapItemState> items = [new(0, MapItemStatus.Open, false, Resumable: false), new(1, MapItemStatus.Open, false, true)];

        Assert.Equal([1], Assert.IsType<MapNext.Start>(MapRules.Next(Map(), items)).Items);
    }

    [Fact]
    public void FR355_every_item_finished_finishes_the_map()
    {
        Assert.IsType<MapNext.Finish>(MapRules.Next(Map(), Items(200, completed: 197, failed: 3)));
    }

    [Fact]
    public void FR355_zero_items_finish_at_once()
    {
        Assert.IsType<MapNext.Finish>(MapRules.Next(Map(), []));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void FR355_section_4_68_200_items_at_5_percent_allow_10_failures(int failed, bool stageFails)
    {
        var map = Map();
        var progress = new MapProgress(200, 200 - failed, failed);

        Assert.Equal(10, map.MaxFailed.Allowed(200));
        Assert.Equal(stageFails, MapRules.StageFailed(map, progress));
        Assert.Equal(stageFails, MapRules.Next(map, Items(200, completed: 200 - failed, failed: failed)) is MapNext.Failed);
    }

    [Fact]
    public void FR355_section_4_68_failures_beyond_the_limit_stop_further_items()
    {
        var next = Assert.IsType<MapNext.Failed>(MapRules.Next(Map(maxFailed: "2"), Items(20, failed: 3)));

        Assert.Contains("3 of 20 items failed", next.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FR355_section_4_68_with_fail_the_first_failure_fails_the_stage()
    {
        var map = Map(MapItemFailure.Fail);

        Assert.IsType<MapNext.Start>(MapRules.Next(map, Items(200, completed: 5)));
        var next = Assert.IsType<MapNext.Failed>(MapRules.Next(map, Items(200, completed: 5, failed: 1)));
        Assert.Contains("item 0 failed", next.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FR355_progress_is_grouped_from_the_item_rows()
    {
        var item = new StageExecution { ItemIndex = 0 };
        Assert.Equal(MapItemStatus.Open, MapRules.StatusOf(item));
        item.ExitedAt = DateTimeOffset.UnixEpoch;
        item.Outcome = BranchOutcomes.Completed;
        Assert.Equal(MapItemStatus.Completed, MapRules.StatusOf(item));
        item.Outcome = BranchOutcomes.Failed;
        Assert.Equal(MapItemStatus.Failed, MapRules.StatusOf(item));

        Assert.Equal(new MapProgress(5, 2, 1), MapRules.Progress(Items(5, completed: 2, failed: 1)));
        Assert.Equal(2, new MapProgress(5, 2, 1).Open);
    }

    [Fact]
    public void FR355_the_driver_runs_a_map_stage_not_a_manual_stop()
    {
        Assert.IsType<DriverStep.RunMap>(RunDriverRules.Plan(Score, new StageEntryFacts()));
        Assert.IsType<DriverStep.WaitOnInput>(RunDriverRules.Plan(Score, new StageEntryFacts { MissingInputs = ["transcripts"] }));
    }

    [Fact]
    public void FR355_the_item_agent_gets_MAP_ITEM_and_MAP_INDEX_and_the_reduce_agent_the_items_list()
    {
        Assert.Equal([MapRules.ItemPlaceholder, MapRules.IndexPlaceholder], MapRules.SuppliedPlaceholders(Score, "qa-scorer"));
        Assert.Equal(["SCORES_ALL"], MapRules.SuppliedPlaceholders(Score, "qa-summariser"));
        Assert.Empty(MapRules.SuppliedPlaceholders(Score, "someone-else"));
    }

    [Fact]
    public void FR355_a_map_items_prompt_placeholders_are_refused_for_another_agent()
    {
        var view = Score.ForAgent("qa-summariser")!;
        const string Template = "---\nversion: x-1\n---\nSystem.\n<!-- user -->\n{{MAP_ITEM}}\n";

        Assert.NotNull(AgentPromptPlaceholders.Problem(view, Template, [], MapRules.SuppliedPlaceholders(Score, "qa-summariser")));
        Assert.Null(AgentPromptPlaceholders.Problem(Score.ForAgent("qa-scorer")!, Template, [], MapRules.SuppliedPlaceholders(Score, "qa-scorer")));
    }

    [Fact]
    public void FR355_the_role_of_an_agent_request_on_a_map_stage()
    {
        Assert.Equal(MapAgentRole.Item, MapRules.RoleOf(Score, "qa-scorer", 3));
        Assert.Equal(MapAgentRole.Reduce, MapRules.RoleOf(Score, "qa-summariser", null));
        Assert.Equal(MapAgentRole.None, MapRules.RoleOf(Score, "qa-summariser", 3));
        Assert.Equal(MapAgentRole.None, MapRules.RoleOf(Score, "qa-scorer", null));
    }

    [Fact]
    public void FR355_map_and_reduce_handlers_are_declared_by_their_stage()
    {
        var stage = Score with
        {
            Map = Score.Map! with { Agent = null, Handler = FlowFixturePack.QaTallyHandler },
            Reduce = new ReduceDefinition(null, "flow.qa-reduce"),
        };

        Assert.True(stage.Declares(FlowFixturePack.QaTallyHandler));
        Assert.True(stage.Declares("flow.qa-reduce"));
        Assert.False(stage.Declares("flow.other"));
    }

    [Fact]
    public void FR355_item_labels_are_zero_based()
    {
        Assert.Equal("item-0", MapRules.ItemLabel(0));
        Assert.Equal("item-199", MapRules.ItemLabel(199));
    }
    [Theory]
    [InlineData(null, true, false, false, true)]
    [InlineData(JobStatus.Succeeded, true, false, false, false)]
    [InlineData(JobStatus.Succeeded, false, true, false, false)]
    [InlineData(JobStatus.Succeeded, false, false, false, true)]
    [InlineData(JobStatus.Failed, false, false, false, false)]
    [InlineData(JobStatus.Failed, false, false, true, true)]
    [InlineData(JobStatus.Dead, true, false, true, false)]
    public void FR355_an_item_that_ran_before_is_resumed_only_by_the_maps_own_job_without_a_pending_gate(
        JobStatus? lastJob, bool fromItemEnd, bool gatePending, bool retry, bool resumable) =>
        Assert.Equal(resumable, MapRules.Resumable(lastJob, fromItemEnd, gatePending, retry));
    [Fact]
    public void FR355_map_placeholders_validate_for_the_map_and_reduce_agents_only()
    {
        var catalog = FlowFixturePack.Catalog with
        {
            Prompts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["flow.qa-score"] = "---\nversion: flow.qa-score-1\n---\nYou score calls.\n<!-- user -->\n{{MAP_INDEX}}: {{MAP_ITEM}}\n",
                ["flow.qa-summarise"] = "---\nversion: flow.qa-summarise-1\n---\nYou summarise.\n<!-- user -->\n{{SCORES_ALL}}\n",
            },
        };
        var yaml = FlowFixturePack.Yaml(FlowFixturePack.Qa);
        var handlers = yaml.Replace("agent: qa-scorer, max_parallel", $"handler: {FlowFixturePack.QaTallyHandler}, max_parallel", StringComparison.Ordinal);

        Assert.Empty(DefinitionYaml.Errors(yaml, catalog));

        // A scorer binding left behind by a handler map no longer gets {{MAP_ITEM}}, so its prompt is refused (the
        // definition never registers: the M70c CI failure of fixture-qa-handlers).
        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[0].agents[0].prompt")], DefinitionYaml.Errors(handlers, catalog));
    }
}
