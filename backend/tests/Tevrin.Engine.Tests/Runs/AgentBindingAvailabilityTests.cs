using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>D-0.7.0-42, FR-301, §6 <c>DEFINITION_UNAVAILABLE</c>: a secondary agent's tools must be registered too.</summary>
public class AgentBindingAvailabilityTests
{
    private static readonly PipelineRegistry Registry = new([new FixturePack()], [], Array.Empty<JobHandlerRegistration>());

    private static PipelineDefinition WithBindingTools(params string[] tools)
    {
        var definition = Fixtures.Load("fixture-review.pipeline.yaml");
        return definition with
        {
            Stages = [.. definition.Stages.Select(s => s.Id == "check"
                ? s with { Agents = [new AgentBindingDefinition("reviewer", "stage.run", null, s.Inputs, null, tools)] }
                : s)],
        };
    }

    [Fact]
    public void D_0_7_0_42_FR301_a_binding_with_registered_tools_is_runnable() =>
        Assert.True(PipelineDefinitionResolver.IsRunnable(Registry, WithBindingTools(FixturePack.LookupTool)));

    [Fact]
    public void D_0_7_0_42_FR301_an_unregistered_binding_tool_makes_it_unavailable()
    {
        var definition = WithBindingTools(FixturePack.LookupTool, "fixture.missing-tool");

        Assert.False(PipelineDefinitionResolver.IsRunnable(Registry, definition));
        Assert.Equal(["tool:fixture.missing-tool"], PipelineDefinitionResolver.MissingNames(Registry, definition));
    }
}
