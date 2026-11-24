using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-331, FR-332 (M66): the <c>fixture-linear</c> pack: agent <c>draft</c> → agent <c>check</c> → gate <c>approve</c> →
/// handler <c>publish</c>, engine-controlled, with the input <c>brief</c> required by <c>draft</c>.
/// </summary>
public sealed class FixtureLinearPack : IPipelinePack
{
    public const string PipelineId = "fixture-linear";
    public const string PublishHandler = "fixture-linear.publish";
    public const string DraftPrompt = "linear.draft";
    public const string CheckPrompt = "linear.check";

    public const string DraftTemplate = """
            ---
            version: ld1
            ---
            You write drafts.
            <!-- user -->
            Write a draft for this brief:
            <brief>{{BRIEF?}}</brief>
            """;

    public const string CheckTemplate = """
            ---
            version: lc1
            ---
            You check drafts.
            <!-- user -->
            <draft>{{DRAFT?}}</draft>
            """;

    public string Id => PipelineId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-linear.pipeline.yaml", Fixtures.Read("fixture-linear.pipeline.yaml"));
        registry.AddSchema("brief.schema.json", Fixtures.Read("brief.schema.json"));
        registry.AddStageHandler(PublishHandler);
        registry.AddPrompt(DraftPrompt, DraftTemplate);
        registry.AddPrompt(CheckPrompt, CheckTemplate);
    }
}
