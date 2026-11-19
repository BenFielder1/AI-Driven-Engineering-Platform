using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-299, FR-301, FR-303: the generic API's DTO mappings.</summary>
public class PipelineDtoMapperTests
{
    private static PipelineDefinition Fixture() => Fixtures.Load("fixture-review.pipeline.yaml");

    [Fact]
    public void FR299_the_definition_lists_stages_with_kinds_gates_and_transitions_artifacts_and_statuses()
    {
        var definition = Fixture();
        var dto = PipelineDtoMapper.ToDefinitionDto(definition, "abc123", "fixture");

        Assert.Equal("fixture-review", dto.PipelineId);
        Assert.Equal("Fixture review", dto.Title);
        Assert.Equal("abc123", dto.Hash);
        Assert.Equal("fixture", dto.PackId);
        Assert.Equal(definition.Stages.Select(s => s.Id), dto.Stages.Select(s => s.Id));
        Assert.Contains(dto.Artifacts, a => a is { Key: "report", Title: "Check report", Format: ArtifactFormat.Json });
        Assert.Contains(dto.Statuses, s => s is { Name: "published", Generic: GenericRunStatus.Completed });

        var draft = dto.Stages[0];
        Assert.Equal(StageKind.Agent, draft.Kind);
        Assert.Equal("next", draft.Transitions[0].Kind);
        Assert.Equal(definition.Stages[1].Id, draft.Transitions[0].To);

        var gated = dto.Stages.Single(s => s.Gate is not null);
        var stage = definition.Stages.Single(s => s.Gate is not null);
        Assert.Equal(stage.Gate!.Type, gated.Gate!.Type);
        Assert.Equal(stage.Gate.Approves, gated.Gate.Approves);
        Assert.Equal(stage.Gate.Modes, gated.Gate.Modes);

        foreach (var (mapped, source) in dto.Stages.Zip(definition.Stages))
        {
            Assert.Equal(source.Loop is not null, mapped.Transitions.Any(t => t.Kind == "loop"));
            Assert.Equal(source.OnReject is not null, mapped.Transitions.Any(t => t.Kind == "on_reject"));
            Assert.Equal(source.Inputs, mapped.Inputs);
            Assert.Equal(source.Output, mapped.Output);
        }
    }

    [Fact]
    public void FR301_a_gate_names_the_stage_that_declares_its_type_and_its_artifact()
    {
        var definition = Fixture();
        var stage = definition.Stages.Single(s => s.Gate is not null);
        var runId = Guid.CreateVersion7();
        var gate = new ApprovalGate
        {
            ProjectId = Guid.CreateVersion7(),
            Type = stage.Gate!.Type,
            Status = GateStatus.Approved,
            Decision = null,
            Comment = "fine",
            CreatedAt = DateTimeOffset.UnixEpoch,
            DecidedAt = DateTimeOffset.UnixEpoch.AddHours(1),
            ReviewerId = Guid.CreateVersion7(),
        };

        var dto = PipelineDtoMapper.ToGateDto(gate, runId, definition, "report", 2);

        Assert.Equal(stage.Id, dto.StageId);
        Assert.Equal(runId, dto.RunId);
        Assert.Equal(KeyWords.Humanise(gate.Type), dto.Title);
        Assert.Equal(("report", 2), (dto.ArtifactKey, dto.ArtifactVersion));
        Assert.Equal(gate.ReviewerId, dto.DecidedByUserId);
        Assert.Null(PipelineDtoMapper.ToGateDto(WithType("unknown_gate"), runId, definition, null, null).StageId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("plain text", "plain text")]
    [InlineData("{\"text\":\"hello\",\"stopReason\":\"end\"}", "hello")]
    [InlineData("{\"other\":1}", "{\"other\":1}")]
    [InlineData("{broken", "{broken")]
    public void FR303_replay_output_is_the_stored_text(string? stored, string? expected) =>
        Assert.Equal(expected, PipelineDtoMapper.OutputText(stored));

    [Fact]
    public void FR300_the_short_hash_is_the_first_twelve_characters() =>
        Assert.Equal("0123456789ab", PipelineDtoMapper.ShortHash("0123456789abcdef"));

    [Fact]
    public void FR303_replay_input_and_output_are_capped_and_flagged()
    {
        var run = new AgentRun
        {
            Input = new string('i', TimelineItems.MaxReplayTextLength + 1),
            Output = "{\"text\":\"short\"}",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var replay = TimelineItems.AgentRun(run, replay: true);
        Assert.Equal(TimelineItems.MaxReplayTextLength, replay.Input!.Length);
        Assert.Equal((true, "short", false), (replay.InputTruncated, replay.Output, replay.OutputTruncated));

        var timeline = TimelineItems.AgentRun(run, replay: false);
        Assert.Equal(((string?)null, (bool?)null, (string?)null, (bool?)null), (timeline.Input, timeline.InputTruncated, timeline.Output, timeline.OutputTruncated));

        var longOutput = TimelineItems.AgentRun(new AgentRun { Output = new string('o', TimelineItems.MaxReplayTextLength + 5), InputTruncated = false }, replay: true);
        Assert.Equal((false, true), (longOutput.InputTruncated, longOutput.OutputTruncated));
    }

    [Fact]
    public void FR302_an_inbox_item_can_be_decided_only_on_projects_the_caller_may_decide()
    {
        var definition = Fixture();
        var ownProject = Guid.CreateVersion7();
        var viewedProject = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var names = new Dictionary<Guid, string> { [ownProject] = "Mine", [viewedProject] = "Theirs" };
        var pipelines = new Dictionary<Guid, (string Id, string Title)> { [runId] = ("fixture-review", "Fixture review") };
        HashSet<Guid> decidable = [ownProject];
        PipelineGateDto Gate(Guid projectId, string type) =>
            PipelineDtoMapper.ToGateDto(new ApprovalGate { ProjectId = projectId, Type = type, CreatedAt = DateTimeOffset.UnixEpoch }, runId, definition, null, null);

        var mine = PipelineGateInbox.ToItem(Gate(ownProject, "report_approval"), names, pipelines, decidable);
        var escalation = PipelineGateInbox.ToItem(Gate(ownProject, "escalation"), names, pipelines, decidable);
        var theirs = PipelineGateInbox.ToItem(Gate(viewedProject, "report_approval"), names, pipelines, decidable);

        Assert.Equal((true, "Mine", "fixture-review", "Fixture review"), (mine.CanDecide, mine.ProjectName, mine.PipelineId, mine.PipelineTitle));
        Assert.True(escalation.CanDecide);
        Assert.Equal((false, "Theirs"), (theirs.CanDecide, theirs.ProjectName));
        var unknownRun = PipelineGateInbox.ToItem(
            PipelineDtoMapper.ToGateDto(new ApprovalGate { ProjectId = ownProject, Type = "x" }, Guid.CreateVersion7(), null, null, null), names, pipelines, decidable);
        Assert.Equal((string.Empty, string.Empty), (unknownRun.PipelineId, unknownRun.PipelineTitle));
    }

    private static ApprovalGate WithType(string type) => new() { Type = type, CreatedAt = DateTimeOffset.UnixEpoch };
}
