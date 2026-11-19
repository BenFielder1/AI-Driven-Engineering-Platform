using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-289, §4.54: the cascade computed from the fixture definition's stage inputs (draft → report → summary).</summary>
public class DefinitionCascadeTests
{
    private static readonly PipelineDefinition Definition = Fixtures.Load("fixture-review.pipeline.yaml");

    [Theory]
    [InlineData("draft", "summarise", "report,summary", "check")]
    [InlineData("draft", "check", "report,summary", null)]
    [InlineData("draft", "draft", "report,summary", null)]
    [InlineData("report", "summarise", "summary", null)]
    [InlineData("summary", "summarise", "", null)]
    public void FR289_approving_invalidates_the_transitive_dependants_and_falls_back_only_from_a_later_stage(
        string approved, string current, string invalidated, string? fallback)
    {
        var effect = DefinitionCascade.Effect(approved, current, Definition);

        Assert.Equal(invalidated.Split(',', StringSplitOptions.RemoveEmptyEntries), effect.InvalidatedArtifactKeys);
        Assert.Equal(fallback, effect.FallbackStage);
        Assert.Empty(effect.PackFallbackStages);
    }

    [Fact]
    public void FR289_cascade_none_invalidates_nothing_and_never_calls_the_hook()
    {
        var definition = WithCascade("draft", CascadeMode.None);
        var hook = new StageHook(definition.Pipeline, "draft");

        var effect = DefinitionCascade.Effect("draft", "summarise", definition, hook);

        Assert.Equal((0, (string?)null, 0), (effect.InvalidatedArtifactKeys.Count, effect.FallbackStage, hook.Calls));
    }

    [Theory]
    [InlineData("summarise", "draft")]
    [InlineData("check", "draft")]
    [InlineData("draft", null)]
    public void FR289_self_and_downstream_includes_the_approved_artifact_producing_stage(string current, string? fallback)
    {
        var effect = DefinitionCascade.Effect("draft", current, WithCascade("draft", CascadeMode.SelfAndDownstream));

        Assert.Equal(["report", "summary"], effect.InvalidatedArtifactKeys);
        Assert.Equal(fallback, effect.FallbackStage);
    }

    [Fact]
    public void FR289_the_hook_adds_stages_considered_for_the_fallback()
    {
        var hook = new StageHook(Definition.Pipeline, "check");

        var effect = DefinitionCascade.Effect("report", "summarise", Definition, hook);

        Assert.Equal(["summary"], effect.InvalidatedArtifactKeys);
        Assert.Equal(["check"], effect.PackFallbackStages);
        Assert.Equal("check", effect.FallbackStage);
        Assert.Equal("report", hook.LastApproved);
        Assert.Equal(["summary"], hook.LastInvalidated);
    }

    [Fact]
    public void FR289_a_hook_for_another_pipeline_or_naming_an_unknown_stage_fails_loudly()
    {
        Assert.Throws<ArgumentException>(() => DefinitionCascade.Effect("report", "summarise", Definition, new StageHook("other", "check")));
        Assert.Throws<InvalidOperationException>(() => DefinitionCascade.Effect("report", "summarise", Definition, new StageHook(Definition.Pipeline, "nowhere")));
    }

    [Fact]
    public void FR289_an_unknown_artifact_or_stage_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DefinitionCascade.Effect("nope", "draft", Definition));
        Assert.Throws<ArgumentOutOfRangeException>(() => DefinitionCascade.Effect("draft", "nope", Definition));
    }

    [Fact]
    public void FR289_dependants_are_listed_in_the_definition_artifact_order()
    {
        var yaml = DefinitionYaml.Edit("  draft: { title: Draft, format: json, approval: auto_lock, cascade: downstream }\n  report: { title: Report, format: markdown }\n  summary: { title: Summary, format: markdown }",
            "  summary: { title: Summary, format: markdown }\n  report: { title: Report, format: markdown }\n  draft: { title: Draft, format: json, approval: auto_lock, cascade: downstream }");
        var definition = PipelineDefinitionLoader.Parse(yaml).Definition!;

        Assert.Equal(["summary", "report"], DefinitionCascade.Downstream("draft", definition));
    }

    private static PipelineDefinition WithCascade(string key, CascadeMode mode) =>
        Definition with { Artifacts = [.. Definition.Artifacts.Select(a => a.Key == key ? a with { Cascade = mode } : a)] };

    private sealed class StageHook(string pipelineId, params string[] stages) : IInvalidationHook
    {
        public string PipelineId => pipelineId;

        public int Calls { get; private set; }

        public string? LastApproved { get; private set; }

        public IReadOnlyList<string> LastInvalidated { get; private set; } = [];

        public IReadOnlyList<string> FallbackStages(string approvedArtifactKey, IReadOnlyList<string> invalidatedArtifactKeys)
        {
            Calls++;
            LastApproved = approvedArtifactKey;
            LastInvalidated = invalidatedArtifactKeys;
            return stages;
        }

        public Task OnInvalidatedAsync(InvalidationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
