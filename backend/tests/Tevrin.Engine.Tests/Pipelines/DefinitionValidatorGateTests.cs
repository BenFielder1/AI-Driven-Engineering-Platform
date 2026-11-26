using Tevrin.Engine.Domain.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-276, FR-281–FR-283, FR-286: backward on_success, gate types, on_invalid and concurrency checks.</summary>
public class DefinitionValidatorGateTests
{
    [Theory]
    [InlineData("draft")]
    [InlineData("check")]
    public void FR281_an_on_success_to_an_earlier_or_the_same_stage_is_an_uncapped_loop(string target) =>
        Assert.Contains(
            (DefinitionErrorCodes.UncappedLoop, "stages[1].on_success"),
            Errors(Edit("    output: report", $"    output: report\n    on_success: {target}")));

    [Fact]
    public void FR283_an_escalation_gate_is_refused_because_the_engine_owns_it()
    {
        var errors = PipelineDefinitionLoaderErrors(Edit("type: report_approval", "type: escalation"));
        var error = Assert.Single(errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "stages[2].gate.type"), (error.Code, error.Path));
        Assert.Contains("engine-owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR359_a_tool_approval_entry_gate_is_refused_so_approving_a_tool_call_never_opens_a_stage()
    {
        var errors = PipelineDefinitionLoaderErrors(Edit("type: report_approval", "type: tool_approval"));
        var error = Assert.Single(errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "stages[2].gate.type"), (error.Code, error.Path));
        Assert.Contains("engine-owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR282_no_pending_gates_except_names_a_gate_of_the_definition_or_escalation()
    {
        var known = Edit("{ artifact_locked: report }", "{ artifact_locked: report }\n      - { no_pending_gates: { except: [report_approval, escalation] } }");
        Assert.Empty(Errors(known));

        var unknown = Edit("{ artifact_locked: report }", "{ artifact_locked: report }\n      - { no_pending_gates: { except: [pre_release] } }");
        Assert.Equal([(DefinitionErrorCodes.Parse, "stages[2].requires[1].no_pending_gates.except[0]")], Errors(unknown));
    }

    [Fact]
    public void FR282_on_invalid_without_guards_or_an_output_schema_has_nothing_to_validate() =>
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "stages[1].on_invalid")],
            Errors(Edit("    validate:\n      - { guard: fixture.report-consistent }\n", string.Empty)));

    [Fact]
    public void FR282_on_invalid_with_only_an_output_schema_is_valid()
    {
        var yaml = Edit("    validate:\n      - { guard: fixture.report-consistent }\n", string.Empty);
        yaml = Edit("report: { title: Report, format: markdown }", "report: { title: Report, format: json, schema: report.schema.json }", yaml);
        Assert.Empty(Errors(yaml));
    }

    [Fact]
    public void FR286_a_job_type_listed_twice_in_concurrency_is_a_duplicate() =>
        Assert.Equal(
            [(DefinitionErrorCodes.DuplicateId, "concurrency.exclusive[1].job_type")],
            Errors(Edit("stages:\n", "concurrency: { exclusive: [{ job_type: agent.run, scope: project }, { job_type: agent.run, scope: project }] }\nstages:\n")));

    [Fact]
    public void FR286_D_0_7_0_37_run_scoped_concurrency_is_refused_as_a_parse_error()
    {
        var yaml = Edit("stages:\n", "concurrency: { exclusive: [{ job_type: agent.run, scope: run }] }\nstages:\n");

        Assert.Equal([(DefinitionErrorCodes.Parse, "concurrency.exclusive[0].scope")], Errors(yaml));
        Assert.Contains(
            PipelineDefinitionLoaderErrors(yaml),
            e => e.Message == "run-scoped concurrency is not supported in 0.7.0");
    }

    private static IReadOnlyList<DefinitionError> PipelineDefinitionLoaderErrors(string yaml) =>
        Runtime.Pipelines.PipelineDefinitionLoader.ParseAndValidate(yaml, Catalog).Errors;
}
