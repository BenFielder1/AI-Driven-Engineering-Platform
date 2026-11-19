using Tevrin.Engine.Domain.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-276, §4.49: one test per validator row, each changing one thing in the valid <see cref="DefinitionYaml.Minimal"/>.</summary>
public class DefinitionValidatorRowTests
{
    [Fact]
    public void FR276_the_minimal_definition_is_valid() => Assert.Empty(Errors(Minimal));

    [Fact]
    public void FR276_DEF_PARSE_a_key_of_the_wrong_type() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "start")], Errors(Edit("start: [draft]", "start: { a: b }")));

    [Fact]
    public void FR276_DEF_PARSE_max_attempts_out_of_range() =>
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[1].on_invalid.max_attempts")], Errors(Edit("max_attempts: 3", "max_attempts: 11")));

    [Fact]
    public void FR276_DEF_DUPLICATE_ID_two_stages() =>
        Assert.Contains((DefinitionErrorCodes.DuplicateId, "stages[2].id"), Errors(Edit("  - id: summarise", "  - id: check")));

    [Fact]
    public void FR276_DEF_DUPLICATE_ID_two_artifacts() =>
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "artifacts.report")],
            Errors(Edit("  summary: { title: Summary, format: markdown }", "  summary: { title: Summary, format: markdown }\n  report: { title: Again, format: json }")));

    [Fact]
    public void FR276_DEF_DUPLICATE_ID_two_statuses() =>
        Assert.Equal([(DefinitionErrorCodes.DuplicateId, "statuses.done")], Errors(Edit("  done: { generic: completed }", "  done: { generic: completed }\n  done: { generic: failed }")));

    [Fact]
    public void FR276_DEF_DUPLICATE_ID_two_status_changes() =>
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "status_changes.finish")],
            Errors(Edit("error_code: NOT_WORKING }", "error_code: NOT_WORKING }\n  finish: { stage: draft, from: [working], to: done, error_code: X }")));

    [Fact]
    public void FR276_DEF_UNKNOWN_STAGE() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownStage, "status_changes.finish.stage")], Errors(Edit("{ stage: summarise,", "{ stage: publish,")));

    [Fact]
    public void FR276_DEF_UNKNOWN_STAGE_in_on_success() =>
        Assert.Contains((DefinitionErrorCodes.UnknownStage, "stages[1].on_success"), Errors(Edit("    output: report", "    output: report\n    on_success: publish")));

    [Fact]
    public void FR276_DEF_UNKNOWN_ARTIFACT() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownArtifact, "stages[2].requires[0].artifact_locked")], Errors(Edit("{ artifact_locked: report }", "{ artifact_locked: plan }")));

    [Fact]
    public void FR276_DEF_UNKNOWN_STATUS() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownStatus, "status_changes.finish.to")], Errors(Edit("to: done,", "to: shipped,")));

    [Fact]
    public void FR276_DEF_UNKNOWN_STATUS_for_rollback_status() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownStatus, "rollback_status")], Errors(Edit("stages:\n", "rollback_status: undone\nstages:\n")));

    [Fact]
    public void FR276_DEF_UNKNOWN_GUARD() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownGuard, "stages[1].validate[0].guard")], Errors(Edit("fixture.report-consistent", "fixture.missing")));

    [Fact]
    public void FR276_DEF_UNKNOWN_HANDLER() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownHandler, "stages[2].handler")], Errors(Edit("    inputs: [report]", "    handler: fixture.publish\n    inputs: [report]")));

    [Fact]
    public void FR276_DEF_UNKNOWN_TOOL() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownTool, "stages[0].agent.tools[0]")], Errors(Edit("tools: [fixture.lookup]", "tools: [fixture.search]")));

    [Fact]
    public void FR276_DEF_UNKNOWN_JOB_TYPE() =>
        Assert.Equal([(DefinitionErrorCodes.UnknownJobType, "stages[1].agent.job_type")], Errors(Edit("{ key: checker }", "{ key: checker, job_type: checking }")));

    [Fact]
    public void FR276_DEF_UNKNOWN_JOB_TYPE_in_concurrency() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownJobType, "concurrency.exclusive[0].job_type")],
            Errors(Edit("stages:\n", "concurrency: { exclusive: [{ job_type: checking, scope: project }] }\nstages:\n")));

    [Fact]
    public void FR276_registered_and_engine_job_types_are_known()
    {
        var yaml = Edit("{ key: checker }", "{ key: checker, job_type: checking }");
        Assert.Empty(Errors(yaml, Catalog with { JobTypes = ["checking"] }));
        Assert.Empty(Errors(Edit("{ key: checker }", "{ key: checker, job_type: stage.run }")));
    }

    [Fact]
    public void FR276_DEF_STAGE_KIND() =>
        Assert.Equal(
            [(DefinitionErrorCodes.StageKind, "stages[1]")],
            Errors(Edit("{ key: checker }", "{ key: checker }\n    handler: fixture.check"), Catalog with { Handlers = ["fixture.check"] }));
}
