using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-301, §6 <c>DEFINITION_UNAVAILABLE</c>: when a pinned definition can run in this build.</summary>
public class DefinitionAvailabilityTests
{
    private static readonly PipelineRegistry Registry = new([new FixturePack()], [], Array.Empty<JobHandlerRegistration>());

    private static PipelineDefinition Fixture() => Fixtures.Load("fixture-review.pipeline.yaml");

    private static PipelineDefinition EditStage(PipelineDefinition definition, string stageId, Func<StageDefinition, StageDefinition> edit) =>
        definition with { Stages = [.. definition.Stages.Select(s => s.Id == stageId ? edit(s) : s)] };

    [Fact]
    public void FR301_a_definition_whose_guards_and_tools_are_registered_is_runnable()
    {
        Assert.True(Registry.Registers(PipelineComponentKind.Guard, FixturePack.ReportConsistentGuard));
        Assert.True(Registry.Registers(PipelineComponentKind.Tool, FixturePack.LookupTool));
        Assert.True(PipelineDefinitionResolver.IsRunnable(Registry, Fixture()));
    }

    [Fact]
    public void FR301_an_unregistered_validate_guard_makes_it_unavailable()
    {
        var definition = EditStage(Fixture(), "check", s => s with { Validate = [.. s.Validate, new StageValidation("fixture.missing")] });
        Assert.False(Registry.Registers(PipelineComponentKind.Guard, "fixture.missing"));
        Assert.False(PipelineDefinitionResolver.IsRunnable(Registry, definition));
    }

    [Fact]
    public void FR301_an_unregistered_requires_guard_makes_it_unavailable()
    {
        var definition = EditStage(Fixture(), "check", s => s with { Requires = [.. s.Requires, new GuardRequirement("fixture.missing")] });
        Assert.False(PipelineDefinitionResolver.IsRunnable(Registry, definition));
    }

    [Fact]
    public void FR301_an_unregistered_handler_makes_it_unavailable()
    {
        var definition = EditStage(Fixture(), "draft", s => s with { Agent = null, Handler = "fixture.missing-handler" });
        Assert.False(PipelineDefinitionResolver.IsRunnable(Registry, definition));
    }

    [Fact]
    public void FR301_an_unregistered_tool_makes_it_unavailable()
    {
        var definition = EditStage(Fixture(), "draft", s => s with { Agent = s.Agent! with { Tools = ["fixture.missing-tool"] } });
        Assert.False(PipelineDefinitionResolver.IsRunnable(Registry, definition));
    }

    [Fact]
    public void FR277_the_missing_names_are_listed_once_each_by_kind_for_the_log()
    {
        var definition = EditStage(Fixture(), "check", s => s with
        {
            Requires = [.. s.Requires, new GuardRequirement("fixture.missing")],
            Validate = [.. s.Validate, new StageValidation("fixture.missing")],
        });
        definition = EditStage(definition, "draft", s => s with { Agent = s.Agent! with { Tools = ["fixture.missing-tool"] } });

        Assert.Empty(PipelineDefinitionResolver.MissingNames(Registry, Fixture()));
        Assert.Equal(["tool:fixture.missing-tool", "guard:fixture.missing"], PipelineDefinitionResolver.MissingNames(Registry, definition));
    }

    [Fact]
    public void FR301_without_a_registry_nothing_is_runnable() =>
        Assert.False(PipelineDefinitionResolver.IsRunnable(null, Fixture()));

    [Fact]
    public void FR299_a_stored_definition_parses_only_when_it_matches_its_hash()
    {
        var definition = Fixture();
        var json = DefinitionHash.CanonicalJson(definition);
        var hash = DefinitionHash.Compute(definition);

        var parsed = PipelineDefinitionResolver.ParseStored(hash, json);
        Assert.NotNull(parsed);
        Assert.Equal(hash, DefinitionHash.Compute(parsed));

        Assert.Null(PipelineDefinitionResolver.ParseStored(new string('0', 64), json));
        Assert.Null(PipelineDefinitionResolver.ParseStored(hash, "{not json"));
    }
}
