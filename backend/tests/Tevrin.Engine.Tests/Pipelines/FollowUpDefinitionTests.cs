using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-354, §4.70 (M69): follow-up stages: the <c>followups:</c> syntax, its validation (<c>DEF_FOLLOWUP_STAGE</c>), the
/// main flow skipping follow-up stages, durations and due times, labels, and the hash left unchanged when unused.
/// </summary>
public class FollowUpDefinitionTests
{
    private static string Yaml => FlowFixturePack.Yaml(FlowFixturePack.Postmortem);

    private static PipelineDefinition Load(string yaml) => PipelineDefinitionLoader.Parse(yaml).Definition!;

    private static string Hash(string yaml) => DefinitionHash.Compute(Load(yaml));

    private static List<(string Code, string Path)> PostmortemErrors(string find, string replace) =>
        Errors(Edit(find, replace, Yaml), FlowFixturePack.Catalog);

    private const string FollowUps = "  - { stage: grade, after: [1d, 5d, 20d] }";

    [Fact]
    public void FR354_the_fixture_loads_its_followups_and_is_valid()
    {
        var definition = Load(Yaml);

        var followUp = Assert.Single(definition.FollowUps);
        Assert.Equal("grade", followUp.Stage);
        Assert.Equal(["1d", "5d", "20d"], followUp.After);
        Assert.Empty(Errors(Yaml, FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR354_the_main_flow_skips_the_followup_stage()
    {
        var definition = Load(Yaml);

        Assert.Equal("review", definition.SuccessorOf("work"));
        Assert.Null(definition.SuccessorOf("review"));
        Assert.True(FollowUpRules.IsFollowUpStage(definition, "grade"));
        Assert.False(FollowUpRules.IsFollowUpStage(definition, "review"));
    }

    [Theory]
    [InlineData("1d", 24 * 60)]
    [InlineData("5d", 5 * 24 * 60)]
    [InlineData("20d", 20 * 24 * 60)]
    [InlineData("12h", 12 * 60)]
    [InlineData("30m", 30)]
    public void FR354_durations_parse_days_as_24_hours(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), FollowUpRules.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("0d")]
    [InlineData("-1d")]
    [InlineData("1w")]
    [InlineData("1.5d")]
    [InlineData(" 1d")]
    [InlineData("1D")]
    [InlineData("99999999999d")]
    public void FR354_invalid_or_zero_durations_do_not_parse(string text) => Assert.Null(FollowUpRules.ParseDuration(text));

    [Fact]
    public void FR354_the_section_4_70_example_gives_the_exact_due_times()
    {
        var completedAt = new DateTimeOffset(2026, 12, 1, 18, 0, 0, TimeSpan.Zero);

        var scheduled = FollowUpRules.Schedule(Load(Yaml), completedAt);

        Assert.Equal(
            [
                new ScheduledFollowUp("grade", "followup:1d", new DateTimeOffset(2026, 12, 2, 18, 0, 0, TimeSpan.Zero)),
                new ScheduledFollowUp("grade", "followup:5d", new DateTimeOffset(2026, 12, 6, 18, 0, 0, TimeSpan.Zero)),
                new ScheduledFollowUp("grade", "followup:20d", new DateTimeOffset(2026, 12, 21, 18, 0, 0, TimeSpan.Zero)),
            ],
            scheduled);
    }

    [Fact]
    public void FR354_a_definition_without_followups_schedules_none() =>
        Assert.Empty(FollowUpRules.Schedule(Load(Minimal), DateTimeOffset.UnixEpoch));

    [Fact]
    public void FR354_labels_are_followup_durations_and_only_declared_ones_count()
    {
        var definition = Load(Yaml);

        Assert.Equal("followup:5d", FollowUpRules.Label("5d"));
        Assert.True(FollowUpRules.IsDeclared(definition, "grade", "followup:5d"));
        Assert.False(FollowUpRules.IsDeclared(definition, "grade", "followup:2d"));
        Assert.False(FollowUpRules.IsDeclared(definition, "review", "followup:5d"));
        Assert.False(FollowUpRules.IsDeclared(definition, "grade", "v1"));
        Assert.False(FollowUpRules.IsLabel("v1"));
    }

    [Fact]
    public void FR354_unused_followups_leave_every_existing_hash_unchanged()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));
        Assert.DoesNotContain("\"followups\"", DefinitionHash.CanonicalJson(Load(Minimal)), StringComparison.Ordinal);
    }

    [Fact]
    public void FR354_followups_are_hashed()
    {
        Assert.Contains("\"followups\"", DefinitionHash.CanonicalJson(Load(Yaml)), StringComparison.Ordinal);
        Assert.NotEqual(Hash(Yaml), Hash(Edit("after: [1d, 5d, 20d]", "after: [1d, 5d, 21d]", Yaml)));
    }

    [Fact]
    public void FR354_DEF_FOLLOWUP_STAGE_is_in_the_code_table() => Assert.Contains("DEF_FOLLOWUP_STAGE", DefinitionErrorCodes.All);

    [Fact]
    public void FR354_an_unknown_followup_stage_is_DEF_FOLLOWUP_STAGE() =>
        Assert.Contains((DefinitionErrorCodes.FollowUpStage, "followups[0].stage"), PostmortemErrors(FollowUps, "  - { stage: audit, after: [1d] }"));

    [Fact]
    public void FR354_a_stage_listed_twice_is_DEF_FOLLOWUP_STAGE() =>
        Assert.Contains(
            (DefinitionErrorCodes.FollowUpStage, "followups[1].stage"),
            PostmortemErrors(FollowUps, FollowUps + "\n  - { stage: grade, after: [2d] }"));

    [Theory]
    [InlineData("0d")]
    [InlineData("1w")]
    [InlineData("soon")]
    public void FR354_a_bad_duration_is_DEF_FOLLOWUP_STAGE(string after) =>
        Assert.Equal(
            [(DefinitionErrorCodes.FollowUpStage, "followups[0].after[1]")],
            PostmortemErrors("after: [1d, 5d, 20d]", $"after: [1d, {after}]"));

    [Fact]
    public void FR354_a_repeated_duration_is_DEF_FOLLOWUP_STAGE() =>
        Assert.Equal(
            [(DefinitionErrorCodes.FollowUpStage, "followups[0].after[2]")],
            PostmortemErrors("after: [1d, 5d, 20d]", "after: [1d, 5d, 1d]"));

    [Fact]
    public void FR354_an_empty_after_is_DEF_FOLLOWUP_STAGE() =>
        Assert.Equal(
            [(DefinitionErrorCodes.FollowUpStage, "followups[0].after")],
            PostmortemErrors("after: [1d, 5d, 20d]", "after: []"));

    [Fact]
    public void FR354_a_followup_stage_reached_by_the_main_flow_is_DEF_FOLLOWUP_STAGE()
    {
        Assert.Contains(
            (DefinitionErrorCodes.FollowUpStage, "followups[0].stage"),
            PostmortemErrors("    writes_feedback: { agent: worker, from: lessons }", "    writes_feedback: { agent: worker, from: lessons }\n    on_success: grade"));
        Assert.Contains((DefinitionErrorCodes.FollowUpStage, "followups[0].stage"), PostmortemErrors("start: [work]", "start: [grade]"));
    }

    [Fact]
    public void FR354_a_followup_stage_is_not_reported_unreachable() =>
        Assert.DoesNotContain(Errors(Yaml, FlowFixturePack.Catalog), e => e.Code == DefinitionErrorCodes.UnreachableStage);

    [Fact]
    public void FR354_without_followups_the_trailing_stage_is_part_of_the_main_flow()
    {
        var definition = Load(Edit("followups:\n" + FollowUps + "\n", string.Empty, Yaml));

        Assert.Empty(definition.FollowUps);
        Assert.Equal("grade", definition.SuccessorOf("review"));
    }

    [Fact]
    public void FR354_a_followup_stage_with_a_gate_is_DEF_FOLLOWUP_STAGE() =>
        Assert.Contains(
            (DefinitionErrorCodes.FollowUpStage, "followups[0].stage"),
            PostmortemErrors("    output: grade\n", "    output: grade\n    gate: { type: grade_approval, modes: [strict] }\n"));

    [Fact]
    public void FR354_a_followup_job_runs_only_on_a_completed_run_and_once()
    {
        var run = new PipelineRun { Status = GenericRunStatus.Completed };
        var open = new StageExecution { PipelineRunId = run.Id, StageId = "grade", FollowUp = "followup:1d" };

        Assert.False(FollowUpRules.IsStale(run, null));
        Assert.False(FollowUpRules.IsStale(run, open));
        Assert.True(FollowUpRules.IsStale(run, new StageExecution { PipelineRunId = run.Id, StageId = "grade", FollowUp = "followup:1d", ExitedAt = DateTimeOffset.UnixEpoch }));
        Assert.True(FollowUpRules.IsStale(run, new StageExecution { PipelineRunId = Guid.NewGuid(), StageId = "grade", FollowUp = "followup:1d" }));
        Assert.True(FollowUpRules.IsStale(run, new StageExecution { PipelineRunId = run.Id, StageId = "grade" }));
        Assert.True(FollowUpRules.IsStale(new PipelineRun { Status = GenericRunStatus.Cancelled }, null));
        Assert.True(FollowUpRules.IsStale(new PipelineRun { Status = GenericRunStatus.Active }, null));
    }

    [Fact]
    public void FR354_outcome_scores_render_one_line_each()
    {
        Assert.Equal("(none)", FollowUpRules.RenderScores([]));
        Assert.Equal("accuracy: 0.25\nrevenue: 1200", FollowUpRules.RenderScores([("accuracy", 0.25), ("revenue", 1200)]));
    }

    [Fact]
    public void FR354_a_followup_payload_round_trips_and_a_main_flow_payload_omits_it()
    {
        var payload = new StageRunPayload(Guid.CreateVersion7(), Guid.CreateVersion7()) { FollowUpStage = "grade", FollowUp = "followup:1d" };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web);

        Assert.Equal(payload, StageRunPayload.Parse(json));
        Assert.DoesNotContain("followUp", System.Text.Json.JsonSerializer.Serialize(new StageRunPayload(payload.RunId, payload.StageExecutionId), System.Text.Json.JsonSerializerOptions.Web), StringComparison.Ordinal);
    }
}
