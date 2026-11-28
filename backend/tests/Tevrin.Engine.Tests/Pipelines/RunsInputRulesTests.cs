using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Agents;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-357 (§3.6, D-0.8.0-23): the <c>from_runs</c> rules (window, status, current run, order, cap, label), the
/// <c>{{&lt;ARTIFACT&gt;_RUNS}}</c> prompt placeholder, the validator's input positions, and the runtime list loader.
/// </summary>
public class RunsInputRulesTests
{
    private static readonly DateTimeOffset Now = new(2030, 12, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Current = Guid.CreateVersion7();
    private static readonly RunsInputDefinition Input = new("fixture-qa", "scores", "7d");

    private static RunsInputCandidate Candidate(
        TimeSpan ago, string content, GenericRunStatus status = GenericRunStatus.Completed, string pipeline = "fixture-qa", Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), pipeline, status, Now - ago - TimeSpan.FromHours(1), Now - ago, content);

    private static IReadOnlyList<AggregateItem> Select(params RunsInputCandidate[] candidates) =>
        RunsInputRules.Select(Input, candidates, Current, Now);

    // ---- pure rules ----

    [Fact]
    public void FR357_the_window_reaches_back_from_now_inclusive()
    {
        var items = Select(
            Candidate(TimeSpan.FromDays(1), "a"),
            Candidate(TimeSpan.FromDays(7), "edge"),
            Candidate(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1), "late"),
            Candidate(TimeSpan.FromDays(30), "old"),
            Candidate(-TimeSpan.FromMinutes(1), "future"));

        Assert.Equal(["a", "edge"], items.Select(i => i.Content));
        Assert.Equal(Now - TimeSpan.FromDays(7), RunsInputRules.Since(Input, Now));
        Assert.Null(RunsInputRules.Since(Input with { Window = "7w" }, Now));
        Assert.Empty(RunsInputRules.Select(Input with { Window = "soon" }, [Candidate(TimeSpan.FromDays(1), "a")], Current, Now));
    }

    [Fact]
    public void FR357_only_runs_with_the_input_status_count_completed_by_default()
    {
        RunsInputCandidate[] candidates =
        [
            Candidate(TimeSpan.FromDays(1), "done"),
            Candidate(TimeSpan.FromDays(2), "failed", GenericRunStatus.Failed),
            Candidate(TimeSpan.FromDays(3), "cancelled", GenericRunStatus.Cancelled),
            Candidate(TimeSpan.FromDays(4), "active", GenericRunStatus.Active),
        ];

        Assert.Equal(GenericRunStatus.Completed, Input.Status);
        Assert.Equal(["done"], Select(candidates).Select(i => i.Content));
        Assert.Equal(["failed"], RunsInputRules.Select(Input with { Status = GenericRunStatus.Failed }, candidates, Current, Now).Select(i => i.Content));
        Assert.Equal(["cancelled"], RunsInputRules.Select(Input with { Status = GenericRunStatus.Cancelled }, candidates, Current, Now).Select(i => i.Content));
    }

    [Fact]
    public void FR357_the_current_run_and_other_pipelines_are_left_out()
    {
        var items = Select(
            Candidate(TimeSpan.FromDays(1), "self", id: Current),
            Candidate(TimeSpan.FromDays(2), "other pipeline", pipeline: "fixture-trends"),
            Candidate(TimeSpan.FromDays(3), "kept"));

        Assert.Equal(["kept"], items.Select(i => i.Content));
    }

    [Fact]
    public void FR357_newest_completion_first_labelled_with_the_run_start_time()
    {
        var items = Select(
            Candidate(TimeSpan.FromDays(5), "five"),
            Candidate(TimeSpan.FromDays(1), "one"),
            Candidate(TimeSpan.FromDays(3), "three"));

        Assert.Equal(["one", "three", "five"], items.Select(i => i.Content));
        Assert.Equal("2030-12-08T05:00:00Z", items[0].Label);
        Assert.Equal("2030-12-02T05:00:00Z", RunsInputRules.Label(new DateTimeOffset(2030, 12, 2, 6, 0, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void FR357_at_most_1000_runs_the_newest()
    {
        var candidates = Enumerable.Range(0, RunsInputDefinition.MaxRuns + 5)
            .Select(i => Candidate(TimeSpan.FromMinutes(i), $"run {i}"))
            .Reverse()
            .ToArray();

        var items = Select(candidates);

        Assert.Equal(1000, RunsInputDefinition.MaxRuns);
        Assert.Equal(RunsInputDefinition.MaxRuns, items.Count);
        Assert.Equal("run 0", items[0].Content);
        Assert.Equal("run 999", items[^1].Content);
    }

    [Fact]
    public void FR357_the_placeholder_is_the_artifact_key_with_RUNS()
    {
        Assert.Equal("SCORES_RUNS", RunsInputRules.Placeholder(Input));
        Assert.Equal("TREND_REPORT_RUNS", RunsInputRules.Placeholder(Input with { Artifact = "trend-report" }));
    }

    [Fact]
    public void FR357_a_plain_inputs_yaml_position_counts_the_from_runs_entries_before_it()
    {
        var stage = new StageDefinition
        {
            Id = "report",
            Inputs = ["focus", "notes", "extra"],
            FromRuns = [Input with { Position = 0 }, Input with { Artifact = "other", Position = 2 }],
        };

        // YAML: [from_runs, focus, from_runs, notes, extra]
        Assert.Equal([1, 3, 4], Enumerable.Range(0, 3).Select(j => RunsInputRules.InputPosition(stage, j)));
        Assert.Equal(2, RunsInputRules.InputPosition(stage with { FromRuns = [] }, 2));
    }

    // ---- the validator ----

    private const string FromRunsLine = "      - from_runs: { pipeline: fixture-qa, artifact: scores, window: 7d, status: completed }\n";

    /// <summary>The flow catalog with the real <c>flow.trends</c> prompt (or <paramref name="prompt"/>), so placeholders are checked.</summary>
    private static PackCatalog WithPrompt(string? prompt = null) =>
        FlowFixturePack.Catalog with { Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { ["flow.trends"] = prompt ?? FlowFixturePack.TrendsPrompt } };

    private static string Trends(string find, string replace) => Edit(find, replace, FlowFixturePack.Yaml(FlowFixturePack.Trends));

    [Fact]
    public void FR357_fixture_trends_validates_against_its_real_prompt_in_a_registry()
    {
        Assert.Contains("{{SCORES_RUNS}}", FlowFixturePack.TrendsPrompt, StringComparison.Ordinal);
        Assert.Empty(Errors(FlowFixturePack.Yaml(FlowFixturePack.Trends), WithPrompt()));

        var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Trends)], [], Array.Empty<JobHandlerRegistration>());
        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(FlowFixturePack.Trends, out var loaded));
        Assert.True(loaded.IsValid);
    }

    [Fact]
    public void FR357_DEF_UNKNOWN_PROMPT_the_runs_placeholder_needs_a_from_runs_input_of_that_artifact()
    {
        // Without the from_runs input (a plain input instead), {{SCORES_RUNS}} is filled by nothing.
        var plain = Trends(FromRunsLine, "      - scores\n");
        plain = Edit("  trend_report:", "  scores: { title: Scores, format: json, approval: auto_lock, cascade: none, input: true }\n  trend_report:", plain);
        Assert.Contains((DefinitionErrorCodes.UnknownPrompt, "stages[0].agent.prompt"), Errors(plain, WithPrompt()));

        // Another artifact's list isn't this input's.
        var other = FlowFixturePack.TrendsPrompt.Replace("{{SCORES_RUNS}}", "{{SCORES_ALL}}", StringComparison.Ordinal);
        Assert.Contains((DefinitionErrorCodes.UnknownPrompt, "stages[0].agent.prompt"), Errors(FlowFixturePack.Yaml(FlowFixturePack.Trends), WithPrompt(other)));
    }

    [Fact]
    public void FR357_DEF_PARSE_two_from_runs_inputs_of_one_artifact_key()
    {
        var yaml = Trends(FromRunsLine, FromRunsLine + "      - from_runs: { pipeline: fixture-callsim, artifact: scores, window: 30d }\n");

        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[0].inputs[1].from_runs.artifact")], Errors(yaml, FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR357_a_plain_input_after_a_from_runs_entry_is_reported_at_its_yaml_index()
    {
        // M70a review: the error path counted only the plain inputs (inputs[0]); the YAML index is 1.
        var yaml = Trends(FromRunsLine, FromRunsLine + "      - nowhere\n");

        Assert.Equal([(DefinitionErrorCodes.UnknownArtifact, "stages[0].inputs[1]")], Errors(yaml, FlowFixturePack.Catalog));
    }

    /// <summary>The integration tests' <c>fixture-trends-recap</c> and <c>fixture-trends-handler</c> (the same edits as their
    /// <c>FixtureFlow.cs</c>), so a variant that stops validating fails here rather than as a 404 there.</summary>
    private sealed class TrendVariantsPack : IPipelinePack
    {
        public const string RecapPrompt =
            "---\nversion: flow.trends-recap-1\n---\nYou recap trend reports.\n<!-- user -->\nEarlier reports:\n{{TREND_REPORT_RUNS?}}\n";

        public string Id => "flow";

        public static string RecapYaml() =>
            Trends("pipeline: fixture-trends\n", "pipeline: fixture-trends-recap\n")
                .Replace(
                    "  trend_report: { title: Trend report, format: markdown, approval: auto_lock, cascade: none }\n",
                    "  trend_report: { title: Trend report, format: markdown, approval: auto_lock, cascade: none }\n  recap: { title: Recap, format: markdown, approval: auto_lock, cascade: none }\n",
                    StringComparison.Ordinal)
            + "\n  - id: recap\n    title: Recap the earlier reports\n    status: reporting\n"
            + "    agent: { key: trend-recapper, prompt: flow.trends-recap }\n    inputs:\n"
            + "      - from_runs: { pipeline: fixture-trends-recap, artifact: trend_report, window: 7d }\n    output: recap\n"
            + "\nfollowups:\n  - { stage: recap, after: [1d] }\n";

        public static string HandlerYaml() =>
            Trends("pipeline: fixture-trends\n", "pipeline: fixture-trends-handler\n")
                .Replace("    agent: { key: trend-analyst, prompt: flow.trends }\n", "    handler: flow.trend-tally\n", StringComparison.Ordinal)
                .Replace("    output: trend_report\n", string.Empty, StringComparison.Ordinal);

        public void Register(IPackRegistry registry)
        {
            registry.AddDefinition("fixture-trends-recap.pipeline.yaml", RecapYaml());
            registry.AddDefinition("fixture-trends-handler.pipeline.yaml", HandlerYaml());
            registry.AddPrompt("flow.trends", FlowFixturePack.TrendsPrompt);
            registry.AddPrompt("flow.trends-recap", RecapPrompt);
            registry.AddStageHandler("flow.trend-tally");
        }
    }

    [Fact]
    public void FR357_the_integration_variants_of_fixture_trends_register()
    {
        Assert.EndsWith("    output: trend_report\n", FlowFixturePack.Yaml(FlowFixturePack.Trends), StringComparison.Ordinal);
        var registry = new PipelineRegistry([new TrendVariantsPack()], [], Array.Empty<JobHandlerRegistration>());

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet("fixture-trends-recap", out var recap));
        Assert.Equal(["recap"], recap.Definition!.FollowUps.Select(f => f.Stage));
        Assert.True(registry.TryGet("fixture-trends-handler", out var handler));
        Assert.Equal("flow.trend-tally", handler.Definition!.Stages[0].Handler);
    }

    // ---- the runtime loader ----

    [Fact]
    public async Task FR357_the_loader_reads_each_from_runs_list_of_the_project_and_the_handler_fact_holds_it()
    {
        var store = new InMemoryAgentStageStore();
        var projectId = Guid.CreateVersion7();
        var current = new PipelineRun { ProjectId = projectId, PipelineId = "fixture-trends", StartedAt = Now };
        store.Runs.Add(current);
        PipelineRun Seed(Guid project, TimeSpan ago, string content, GenericRunStatus status = GenericRunStatus.Completed)
        {
            var run = new PipelineRun
            {
                ProjectId = project,
                PipelineId = "fixture-qa",
                Status = status,
                StartedAt = Now - ago - TimeSpan.FromHours(1),
                CompletedAt = Now - ago,
            };
            store.Runs.Add(run);
            store.Artifacts.Add(new Artifact { ProjectId = project, PipelineRunId = run.Id, Type = "scores", Version = 1, Content = content + " v1", Status = ArtifactStatus.Superseded });
            store.Artifacts.Add(new Artifact { ProjectId = project, PipelineRunId = run.Id, Type = "scores", Version = 2, Content = content, ContentHash = ContentHash.Compute(content), Status = ArtifactStatus.Locked });
            store.Artifacts.Add(new Artifact { ProjectId = project, PipelineRunId = run.Id, Type = "scores", Version = 3, Content = content + " item", ItemIndex = 0, Status = ArtifactStatus.Locked });
            return run;
        }

        Seed(projectId, TimeSpan.FromDays(3), "{\"mean\": 3}");
        Seed(projectId, TimeSpan.FromDays(1), "{\"mean\": 4}");
        Seed(projectId, TimeSpan.FromDays(2), "{\"mean\": 0}", GenericRunStatus.Failed);
        Seed(projectId, TimeSpan.FromDays(9), "{\"mean\": 1}");
        Seed(Guid.CreateVersion7(), TimeSpan.FromDays(1), "{\"mean\": 5}");
        var stage = PipelineDefinitionLoader.Parse(FlowFixturePack.Yaml(FlowFixturePack.Trends)).Definition!.Stages[0];

        var list = Assert.Single(await RunsInputs.LoadAsync(store, current, stage, Now, TestContext.Current.CancellationToken));

        Assert.Equal("SCORES_RUNS", list.Placeholder);
        Assert.Equal(["{\"mean\": 4}", "{\"mean\": 3}"], list.Items.Select(i => i.Content));
        Assert.Equal(Now - TimeSpan.FromDays(7), Assert.Single(store.RunsReads).Since);

        var facts = await RunsInputs.WithFactAsync(store, current, stage, Now, FactBag.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(list.Items, facts.Get<RunsInputFact>()!.For("fixture-qa", "scores")!.Items);
        Assert.Same(FactBag.Empty, await RunsInputs.WithFactAsync(store, current, stage with { FromRuns = [] }, Now, FactBag.Empty, TestContext.Current.CancellationToken));
        Assert.Empty(await RunsInputs.LoadAsync(store, current, stage, Now, TestContext.Current.CancellationToken, new HashSet<string>(StringComparer.Ordinal)));
    }

    // ---- integrity (FR-20) and follow-up outputs ----

    private static Artifact Version(PipelineRun run, string type, int version, string content, string? variant = null, ArtifactStatus status = ArtifactStatus.Locked, bool tampered = false) => new()
    {
        ProjectId = run.ProjectId,
        PipelineRunId = run.Id,
        Type = type,
        Version = version,
        Variant = variant,
        Content = content,
        ContentHash = ContentHash.Compute(tampered ? content + " (before)" : content),
        Status = status,
        CreatedAt = run.StartedAt.AddMinutes(version),
    };

    [Fact]
    public async Task FR357_FR20_a_run_whose_final_version_fails_its_integrity_check_drops_out_without_an_older_stand_in()
    {
        var store = new InMemoryAgentStageStore();
        var projectId = Guid.CreateVersion7();
        var current = new PipelineRun { ProjectId = projectId, PipelineId = "fixture-trends", StartedAt = Now };
        store.Runs.Add(current);
        PipelineRun Qa(int hoursAgo)
        {
            var run = new PipelineRun { ProjectId = projectId, PipelineId = "fixture-qa", Status = GenericRunStatus.Completed, StartedAt = Now.AddHours(-hoursAgo - 1), CompletedAt = Now.AddHours(-hoursAgo) };
            store.Runs.Add(run);
            return run;
        }

        var corrupted = Qa(1);
        store.Artifacts.Add(Version(corrupted, "scores", 1, "good older"));
        store.Artifacts.Add(Version(corrupted, "scores", 2, "marked corrupted", status: ArtifactStatus.Corrupted));
        var tampered = Qa(2);
        store.Artifacts.Add(Version(tampered, "scores", 1, "good older too"));
        store.Artifacts.Add(Version(tampered, "scores", 2, "edited in the database", tampered: true));
        var intact = Qa(3);
        store.Artifacts.Add(Version(intact, "scores", 1, "intact"));
        var stage = PipelineDefinitionLoader.Parse(FlowFixturePack.Yaml(FlowFixturePack.Trends)).Definition!.Stages[0];

        var list = Assert.Single(await RunsInputs.LoadAsync(store, current, stage, Now, TestContext.Current.CancellationToken));

        Assert.Equal(["intact"], list.Items.Select(i => i.Content));
    }

    [Fact]
    public async Task FR357_FR354_a_run_with_only_follow_up_outputs_gives_its_newest_and_a_plain_version_wins()
    {
        var store = new InMemoryAgentStageStore();
        var projectId = Guid.CreateVersion7();
        var current = new PipelineRun { ProjectId = projectId, PipelineId = "fixture-trends", StartedAt = Now };
        store.Runs.Add(current);
        PipelineRun Graded(int hoursAgo)
        {
            var run = new PipelineRun { ProjectId = projectId, PipelineId = "fixture-postmortem", Status = GenericRunStatus.Completed, StartedAt = Now.AddHours(-hoursAgo - 1), CompletedAt = Now.AddHours(-hoursAgo) };
            store.Runs.Add(run);
            return run;
        }

        var followUpsOnly = Graded(1);
        store.Artifacts.Add(Version(followUpsOnly, "grade", 1, "grade after 1d", "followup:1d"));
        store.Artifacts.Add(Version(followUpsOnly, "grade", 2, "grade after 5d", "followup:5d"));
        store.Artifacts.Add(Version(followUpsOnly, "grade", 3, "a fan-out branch", "v1"));
        var plain = Graded(2);
        store.Artifacts.Add(Version(plain, "grade", 1, "plain grade"));
        store.Artifacts.Add(Version(plain, "grade", 2, "later follow-up", "followup:1d"));
        var stage = new StageDefinition { Id = "report", FromRuns = [new RunsInputDefinition("fixture-postmortem", "grade", "7d")] };

        var list = Assert.Single(await RunsInputs.LoadAsync(store, current, stage, Now, TestContext.Current.CancellationToken));

        Assert.Equal(["grade after 5d", "plain grade"], list.Items.Select(i => i.Content));
    }

    [Fact]
    public async Task FR346_FR20_an_aggregate_leaves_out_branch_outputs_that_fail_their_integrity_check()
    {
        var store = new InMemoryAgentStageStore();
        var ct = TestContext.Current.CancellationToken;

        // A parallel group's branch outputs (RunLatestAsync): the bear case was edited in the database.
        var parallelYaml = Edit(
            "    output: decision\n", "    output: decision\n    aggregate: { from: analysts }\n", FlowFixturePack.Yaml(FlowFixturePack.Parallel));
        var parallel = PipelineDefinitionLoader.Parse(parallelYaml).Definition!;
        var run = new PipelineRun { ProjectId = Guid.CreateVersion7(), PipelineId = FlowFixturePack.Parallel, StartedAt = Now };
        store.Artifacts.Add(Version(run, "bull_case", 1, "bull"));
        store.Artifacts.Add(Version(run, "bull_risks", 1, "risks", status: ArtifactStatus.Corrupted));
        store.Artifacts.Add(Version(run, "bear_case", 1, "bear", tampered: true));

        var items = await AggregateInputs.ItemsAsync(store, run, parallel, "decide", ct);

        Assert.Equal(["bull"], items.Select(i => i.Content));

        // A fan-out's variants (RunVariantsAsync): the trend branch's forecast is marked corrupted.
        var forecast = PipelineDefinitionLoader.Parse(FlowFixturePack.Yaml(FlowFixturePack.Forecast)).Definition!;
        var fanned = new PipelineRun { ProjectId = run.ProjectId, PipelineId = FlowFixturePack.Forecast, StartedAt = Now };
        store.Artifacts.Add(Version(fanned, "forecast", 1, "base rate", "forecaster-base-rate"));
        store.Artifacts.Add(Version(fanned, "forecast", 2, "trend", "forecaster-trend", ArtifactStatus.Corrupted));
        store.Artifacts.Add(Version(fanned, "forecast", 3, "contrarian", "forecaster-contrarian"));

        var forecasts = await AggregateInputs.ItemsAsync(store, fanned, forecast, "aggregate", ct);

        Assert.Equal(["base rate", "contrarian"], forecasts.Select(i => i.Content));
    }
}
