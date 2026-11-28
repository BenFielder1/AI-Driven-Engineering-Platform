using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-335 (0.8.0): the generic run status <c>waiting_on_input</c>, entered and left only by the run driver, and the
/// <c>{ input: &lt;key&gt; }</c> entry requirement it waits on.
/// </summary>
public class WaitingOnInputTests
{
    private static readonly PipelineDefinition Definition = PipelineDefinitionLoader.Parse(Edit(
        "  summary: { title: Summary, format: markdown }\n",
        "  summary: { title: Summary, format: markdown }\n  brief: { title: Session brief, format: json, input: true }\n",
        Edit(
            "    agent: { key: drafter, tools: [fixture.lookup] }\n",
            "    agent: { key: drafter, tools: [fixture.lookup] }\n    requires:\n      - { input: brief }\n"))).Definition!;

    [Fact]
    public void FR335_waiting_on_input_is_a_generic_status_and_not_final()
    {
        Assert.Equal("waiting_on_input", GenericRunStatus.WaitingOnInput.ToWire());
        Assert.Contains("waiting_on_input", EnumStrings.Values<GenericRunStatus>());
        Assert.False(PipelineRunRules.IsFinal(GenericRunStatus.WaitingOnInput));
        Assert.Equal(GenericRunStatus.WaitingOnInput, Definition.GenericStatusOf("waiting_on_input"));
    }

    [Theory]
    [InlineData(GenericRunStatus.Active, true)]
    [InlineData(GenericRunStatus.Running, false)]
    [InlineData(GenericRunStatus.WaitingOnGate, false)]
    [InlineData(GenericRunStatus.WaitingOnInput, false)]
    [InlineData(GenericRunStatus.Completed, false)]
    [InlineData(GenericRunStatus.Failed, false)]
    [InlineData(GenericRunStatus.Cancelled, false)]
    public void FR335_only_an_active_run_enters_waiting_on_input(GenericRunStatus stored, bool allowed)
    {
        Assert.Equal(allowed, PipelineRunRules.CanWaitOnInput(stored));
        Assert.Equal(allowed ? GenericRunStatus.WaitingOnInput : null, PipelineRunRules.InputMove(stored, waiting: true));
    }

    [Theory]
    [InlineData(GenericRunStatus.WaitingOnInput, true)]
    [InlineData(GenericRunStatus.Active, false)]
    [InlineData(GenericRunStatus.Running, false)]
    [InlineData(GenericRunStatus.WaitingOnGate, false)]
    [InlineData(GenericRunStatus.Completed, false)]
    [InlineData(GenericRunStatus.Failed, false)]
    [InlineData(GenericRunStatus.Cancelled, false)]
    public void FR335_only_a_waiting_run_resumes_to_active(GenericRunStatus stored, bool allowed)
    {
        Assert.Equal(allowed, PipelineRunRules.CanResumeFromInput(stored));
        Assert.Equal(allowed ? GenericRunStatus.Active : null, PipelineRunRules.InputMove(stored, waiting: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FR335_the_derived_status_keeps_a_stored_waiting_on_input(bool hasPendingGate, bool hasActiveJob) =>
        Assert.Equal(GenericRunStatus.WaitingOnInput, RunViewRules.DeriveStatus(GenericRunStatus.WaitingOnInput, hasPendingGate, hasActiveJob));

    [Fact]
    public void FR335_the_derived_status_of_other_runs_is_unchanged()
    {
        Assert.Equal(GenericRunStatus.WaitingOnGate, RunViewRules.DeriveStatus(GenericRunStatus.Active, true, true));
        Assert.Equal(GenericRunStatus.Running, RunViewRules.DeriveStatus(GenericRunStatus.Active, false, true));
        Assert.Equal(GenericRunStatus.Active, RunViewRules.DeriveStatus(GenericRunStatus.Active, false, false));
        Assert.Equal(GenericRunStatus.Cancelled, RunViewRules.DeriveStatus(GenericRunStatus.Cancelled, true, true));
    }

    [Fact]
    public void FR335_an_input_requirement_is_unmet_until_the_run_has_the_input()
    {
        var requirement = new InputRequirement("brief");

        Assert.Equal("The session brief input hasn't been submitted.", BuiltInRequirements.Unmet(Definition, requirement, [], []));
        Assert.Equal("The session brief input hasn't been submitted.", BuiltInRequirements.Unmet(Definition, requirement, ["brief"], [], []));
        Assert.Null(BuiltInRequirements.Unmet(Definition, requirement, [], [], ["brief"]));
    }

    [Fact]
    public void FR335_the_state_machine_reports_a_missing_input_in_declared_order()
    {
        var requires = Definition.FindStage("draft")!.Requires;

        Assert.Equal(["The session brief input hasn't been submitted."], PipelineStateMachine.UnmetRequirements(requires, RunFacts.Empty, Definition));
        Assert.Empty(PipelineStateMachine.UnmetRequirements(requires, RunFacts.Empty with { PresentInputs = ["brief"] }, Definition));
    }
}
