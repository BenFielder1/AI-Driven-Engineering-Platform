using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-275, FR-279–FR-286: the YAML loader maps every §4.49 key onto the model.</summary>
public class DefinitionLoaderTests
{
    private static readonly PipelineDefinition Fixture = Fixtures.Load("fixture-review.pipeline.yaml");

    [Fact]
    public void FR275_the_fixture_loads_with_its_top_level_keys()
    {
        Assert.Equal("fixture-review", Fixture.Pipeline);
        Assert.Equal(1, Fixture.Version);
        Assert.Equal("Fixture review", Fixture.Title);
        Assert.Equal([ApprovalMode.Strict, ApprovalMode.Advisory, ApprovalMode.Autonomous], Fixture.ApprovalModes);
        Assert.Equal(ApprovalMode.Advisory, Fixture.DefaultApprovalMode);
        Assert.Equal(["draft"], Fixture.Start);
        Assert.Equal(["redraft_cycles"], Fixture.Settings);
        Assert.Equal(["draft", "check", "summarise"], Fixture.Stages.Select(s => s.Id));
        Assert.Equal("rolled_back", Fixture.RollbackStatus);
    }

    [Fact]
    public void FR275_the_fixture_is_valid_against_its_pack() =>
        Assert.Empty(PipelineDefinitionValidator.Validate(Fixture, FixturePack.Catalog));

    [Fact]
    public void FR279_artifacts_carry_format_schema_approval_and_cascade()
    {
        Assert.Equal(
            new ArtifactDefinition("draft", "Draft", ArtifactFormat.Json, "draft.schema.json", ArtifactApproval.AutoLock, CascadeMode.Downstream),
            Fixture.FindArtifact("draft"));
        Assert.Equal(
            new ArtifactDefinition("summary", "Summary", ArtifactFormat.Markdown, null, ArtifactApproval.Required, CascadeMode.None),
            Fixture.FindArtifact("summary"));
    }

    [Fact]
    public void FR279_cascade_self_and_downstream_reads_from_snake_case()
    {
        var result = PipelineDefinitionLoader.Parse(DefinitionYaml.Minimal.Replace("cascade: downstream", "cascade: self_and_downstream", StringComparison.Ordinal));
        Assert.Empty(result.Errors);
        Assert.Equal(CascadeMode.SelfAndDownstream, result.Definition!.Artifacts[0].Cascade);
    }

    [Fact]
    public void FR280_stage_kinds_are_agent_code_and_manual()
    {
        var draft = Fixture.FindStage("draft")!;
        Assert.Equal(StageKind.Agent, draft.Kind);
        Assert.Equal(["fixture.lookup"], draft.Agent!.Tools);
        Assert.Equal("fixture.draft", draft.Agent.Prompt);
        Assert.Equal(EngineJobTypes.AgentRun, draft.Agent.JobType); // the default

        Assert.Equal(StageKind.Code, new StageDefinition { Id = "x", Handler = "fixture.run" }.Kind);
        Assert.Equal(StageKind.Manual, new StageDefinition { Id = "x" }.Kind);
    }

    [Fact]
    public void FR281_transitions_are_linear_by_default_with_on_success_on_reject_and_loop()
    {
        Assert.Equal("check", Fixture.SuccessorOf("draft"));
        Assert.Null(Fixture.SuccessorOf("summarise"));

        var loop = Fixture.FindStage("check")!.Loop!;
        Assert.Equal("draft", loop.To);
        Assert.Equal("needs_redraft", loop.On);
        Assert.Equal(IterationCap.FromSetting("redraft_cycles"), loop.MaxIterations);

        var yaml = DefinitionYaml.Minimal.Replace("    output: report", "    output: report\n    on_success: summarise\n    on_reject: { to: draft, max_iterations: 2 }", StringComparison.Ordinal);
        var stage = PipelineDefinitionLoader.Parse(yaml).Definition!.FindStage("check")!;
        Assert.Equal("summarise", stage.OnSuccess);
        Assert.Equal(new RejectTransition("draft", IterationCap.Fixed(2)), stage.OnReject);
    }

    [Fact]
    public void FR282_requires_validate_and_on_invalid_are_read()
    {
        var check = Fixture.FindStage("check")!;
        Assert.Equal([new ArtifactLockedRequirement("draft")], check.Requires);
        Assert.Equal([new StageValidation("fixture.report-consistent")], check.Validate);
        Assert.Equal(new OnInvalidPolicy(true, 3, InvalidOutcome.Escalate), check.OnInvalid);

        var summarise = Fixture.FindStage("summarise")!;
        Assert.IsType<NoPendingGatesRequirement>(summarise.Requires[1]);
        Assert.Empty(((NoPendingGatesRequirement)summarise.Requires[1]).Except);
    }

    [Fact]
    public void FR283_a_gate_has_type_modes_approves_and_approval_links()
    {
        var gate = Fixture.FindStage("summarise")!.Gate!;
        Assert.Equal("report_approval", gate.Type);
        Assert.Equal([ApprovalMode.Strict, ApprovalMode.Advisory], gate.Modes);
        Assert.Equal("report", gate.Approves);
        Assert.True(gate.ApprovalLinks);
    }

    [Fact]
    public void FR283_approves_none_means_no_artifact()
    {
        var yaml = DefinitionYaml.Minimal.Replace("approves: report", "approves: none", StringComparison.Ordinal);
        Assert.Null(PipelineDefinitionLoader.Parse(yaml).Definition!.FindStage("summarise")!.Gate!.Approves);
    }

    [Fact]
    public void FR284_triggers_parse_all_three_kinds_and_default_to_manual()
    {
        Assert.Equal(TriggerKind.Manual, Fixture.Trigger.Kind);
        var schedule = PipelineDefinitionLoader.Parse(DefinitionYaml.Minimal.Replace("{ kind: manual }", "{ kind: schedule, cron: '0 6 * * 1' }", StringComparison.Ordinal));
        Assert.Empty(schedule.Errors);
        Assert.Equal(TriggerKind.Schedule, schedule.Definition!.Trigger.Kind);
        Assert.Equal("0 6 * * 1", schedule.Definition.Trigger.Options["cron"]);

        var omitted = PipelineDefinitionLoader.Parse(DefinitionYaml.Minimal.Replace("trigger: { kind: manual }\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal(TriggerKind.Manual, omitted.Definition!.Trigger.Kind);
    }

    [Fact]
    public void FR285_statuses_and_status_changes_are_read()
    {
        Assert.Equal(GenericRunStatus.Completed, Fixture.GenericStatusOf("published"));
        Assert.Equal(GenericRunStatus.Active, Fixture.GenericStatusOf("drafting"));
        Assert.Equal(GenericRunStatus.Failed, Fixture.GenericStatusOf("failed")); // generic names are implicit
        Assert.Null(Fixture.GenericStatusOf("nope"));

        var change = Assert.Single(Fixture.StatusChanges);
        Assert.Equal(["summarising"], change.From);
        Assert.Equal(("summary_published", "summarise", "published", true, "NOT_SUMMARISING"),
            (change.Name, change.Stage, change.To, change.CompletesRun, change.ErrorCode));
    }

    [Fact]
    public void FR286_concurrency_lists_exclusive_job_types_by_scope() =>
        Assert.Equal([new ExclusiveJob(EngineJobTypes.AgentRun, ConcurrencyScope.Project)], Fixture.Concurrency.Exclusive);

    [Fact]
    public void FR275_an_unknown_key_is_a_parse_error_at_its_path()
    {
        var result = PipelineDefinitionLoader.Parse(DefinitionYaml.Minimal.Replace("    output: report", "    output: report\n    on_sucess: summarise", StringComparison.Ordinal));
        var error = Assert.Single(result.Errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "stages[1].on_sucess"), (error.Code, error.Path));
    }

    [Fact]
    public void FR275_a_wrong_type_is_a_parse_error_at_its_path_and_reported_once()
    {
        var yaml = DefinitionYaml.Minimal.Replace("max_attempts: 3", "max_attempts: three", StringComparison.Ordinal);
        var errors = PipelineDefinitionLoader.ParseAndValidate(yaml, DefinitionYaml.Catalog).Errors;
        var error = Assert.Single(errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "stages[1].on_invalid.max_attempts"), (error.Code, error.Path));
    }

    [Fact]
    public void FR275_an_unknown_enum_value_is_a_parse_error()
    {
        var result = PipelineDefinitionLoader.Parse(DefinitionYaml.Minimal.Replace("format: json", "format: xml", StringComparison.Ordinal));
        Assert.Equal((DefinitionErrorCodes.Parse, "artifacts.draft.format"), (result.Errors[0].Code, result.Errors[0].Path));
    }

    [Fact]
    public void FR275_broken_yaml_is_a_parse_error_for_the_document()
    {
        var result = PipelineDefinitionLoader.Parse("pipeline: x\nstages: [ { id: a\n");
        Assert.Null(result.Definition);
        Assert.Equal((DefinitionErrorCodes.Parse, "$"), (result.Errors[0].Code, result.Errors[0].Path));
    }
}
