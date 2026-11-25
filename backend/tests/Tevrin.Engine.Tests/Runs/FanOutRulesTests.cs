using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-345, FR-346, §4.65: the fan-out branches, join, isolation check and aggregate list, on <c>fixture-forecast</c>.</summary>
public class FanOutRulesTests
{
    private static readonly string[] Forecasters = ["forecaster-base-rate", "forecaster-trend", "forecaster-contrarian"];

    private static PipelineDefinition Forecast
    {
        get
        {
            var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Forecast)], [], Array.Empty<JobHandlerRegistration>());
            Assert.True(registry.TryGet(FlowFixturePack.Forecast, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    /// <summary>A forecast long enough for the isolation probe (<see cref="FanOutRules.MinIsolationProbeLength"/>).</summary>
    private static string Long(double probability) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"probability\": {probability:0.00}, \"basis\": \"winter base rate\"}}");

    private static StageDefinition FanOut => Forecast.FindStage("forecast")!;

    private static StageExecution Branch(string label, string? outcome, string stageId = "forecast") => new()
    {
        StageId = stageId,
        Branch = label,
        Variant = label,
        ExitedAt = outcome is null ? null : DateTimeOffset.UnixEpoch,
        Outcome = outcome,
    };

    [Fact]
    public void FR345_agents_fan_out_one_branch_per_agent_labelled_by_its_key()
    {
        var branches = FanOutRules.Branches(FanOut);

        Assert.Equal(Forecasters, branches.Select(b => b.Label));
        Assert.All(branches, b => Assert.Equal(b.Label, b.AgentKey));
        Assert.Empty(FanOutRules.Branches(Forecast.FindStage("aggregate")!));
    }

    [Fact]
    public void FR345_count_fans_out_the_stages_own_agent_labelled_hash_i()
    {
        var stage = FanOut with { FanOut = new FanOutDefinition([], 3) };

        var branches = FanOutRules.Branches(stage);

        Assert.Equal(["#1", "#2", "#3"], branches.Select(b => b.Label));
        Assert.All(branches, b => Assert.Null(b.AgentKey));
        Assert.Equal(new FanOutBranch("#2", null), FanOutRules.BranchOf(stage, "#2"));
        Assert.Null(FanOutRules.BranchOf(stage, "#4"));
    }

    [Fact]
    public void FR345_only_the_stages_own_labelled_executions_are_fan_out_branches()
    {
        Assert.True(FanOutRules.InFanOut(FanOut, Branch("forecaster-trend", null)));
        Assert.False(FanOutRules.InFanOut(FanOut, Branch("forecaster-other", null)));
        Assert.False(FanOutRules.InFanOut(FanOut, Branch("forecaster-trend", null, "aggregate")));
        Assert.False(FanOutRules.InFanOut(FanOut, new StageExecution { StageId = "forecast" }));
    }

    [Fact]
    public void FR345_the_stage_completes_only_when_every_branch_has_completed()
    {
        var done = Forecasters.Select(f => Branch(f, BranchOutcomes.Completed)).ToList();
        Assert.True(FanOutRules.Joined(FanOut, done));

        Assert.False(FanOutRules.Joined(FanOut, done.Take(2)));
        Assert.False(FanOutRules.Joined(FanOut, [.. done.Take(2), Branch(Forecasters[2], null)]));
        Assert.False(FanOutRules.Joined(FanOut, [.. done.Take(2), Branch(Forecasters[2], BranchOutcomes.Failed)]));
        Assert.False(FanOutRules.Joined(FanOut, []));
    }

    [Fact]
    public void FR345_section_4_65_a_rendered_input_holding_a_siblings_output_is_a_breach()
    {
        AggregateItem[] siblings =
        [
            new("forecaster-base-rate", Long(0.20) + "\n"),
            new("forecaster-trend", Long(0.55)),
        ];
        var leaked = "Forecast: will it rain?\nDesk notes: " + Long(0.55) + " from the trend desk.";

        Assert.Equal("forecaster-trend", FanOutRules.IsolationBreach(leaked, "forecaster-contrarian", siblings));
    }

    [Fact]
    public void FR345_section_4_65_an_isolated_input_its_own_output_and_blank_outputs_are_no_breach()
    {
        AggregateItem[] stored =
        [
            new("forecaster-contrarian", Long(0.70)),
            new("forecaster-trend", "   "),
            new("forecaster-base-rate", Long(0.20)),
        ];
        // A retry of the contrarian may see its own earlier forecast, never a sibling's.
        var input = "Forecast: will it rain?\nYour last answer: " + Long(0.70);

        Assert.Null(FanOutRules.IsolationBreach(input, "forecaster-contrarian", stored));
        Assert.Null(FanOutRules.IsolationBreach("Forecast: will it rain?", "forecaster-contrarian", []));
    }

    [Fact]
    public void FR345_section_4_65_a_sibling_output_shorter_than_the_probe_minimum_is_not_looked_for()
    {
        var input = "Forecast: will it rain? Answer yes or no, then OK. Return {} or [] when unsure.";
        AggregateItem[] shortOnes = [new("a", "yes"), new("b", "{}"), new("c", " [] "), new("d", "OK")];
        var justShort = new string('x', FanOutRules.MinIsolationProbeLength - 1);
        var atMinimum = new string('y', FanOutRules.MinIsolationProbeLength);

        Assert.Null(FanOutRules.IsolationBreach(input, "own", shortOnes));
        Assert.Null(FanOutRules.IsolationBreach(input + justShort, "own", [new("e", justShort)]));
        Assert.Equal("f", FanOutRules.IsolationBreach(input + atMinimum, "own", [new("f", atMinimum)]));
        Assert.Equal("g", FanOutRules.IsolationBreach(input + atMinimum + "y", "own", [new("g", atMinimum + "y")]));
    }

    [Fact]
    public void FANOUT_ISOLATION_BROKEN_is_an_engine_error_code() =>
        Assert.Equal("FANOUT_ISOLATION_BROKEN", EngineErrorCodes.FanoutIsolationBroken);

    [Fact]
    public void FR346_the_list_placeholder_is_the_keys_placeholder_with_ALL()
    {
        Assert.Equal("FORECAST_ALL", FanOutRules.AllPlaceholder("forecast"));
        Assert.Equal("BULL_CASE_ALL", FanOutRules.AllPlaceholder("bull-case"));
    }

    [Fact]
    public void FR346_the_list_labels_each_item_with_its_branch()
    {
        var list = FanOutRules.RenderList([new("forecaster-trend", " {\"p\": 0.5}\n"), new("#2", "B")]);

        Assert.Equal("### forecaster-trend\n{\"p\": 0.5}\n\n### #2\nB", list);
    }

    [Fact]
    public void FR346_an_aggregate_reads_a_fan_outs_output_or_every_parallel_branch_stages_output()
    {
        Assert.Equal([(string.Empty, "forecast")], FanOutRules.AggregateSources(FanOut));
        Assert.Empty(FanOutRules.AggregateSources(Forecast.FindStage("aggregate")!));

        var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Parallel)], [], Array.Empty<JobHandlerRegistration>());
        Assert.True(registry.TryGet(FlowFixturePack.Parallel, out var parallel));
        var group = parallel.Definition!.Stages.Single(s => s.Parallel is not null);
        var sources = FanOutRules.AggregateSources(group);
        Assert.Equal(group.Parallel!.Branches.SelectMany(b => b).Count(s => s.Output is not null), sources.Count);
        Assert.Contains(sources, s => s.ArtifactKey == "bear_case");
    }

    [Fact]
    public void FR346_an_aggregate_prompt_may_require_an_inputs_ALL_list()
    {
        var aggregate = Forecast.FindStage("aggregate")!;
        const string Template = "---\nversion: a-1\n---\nCombine.\n<!-- user -->\n{{QUESTION}}\n{{FORECAST_ALL}}\n";

        Assert.Null(AgentPromptPlaceholders.Problem(aggregate, Template, []));
        Assert.NotNull(AgentPromptPlaceholders.Problem(aggregate with { Aggregate = null }, Template, []));
    }
}
