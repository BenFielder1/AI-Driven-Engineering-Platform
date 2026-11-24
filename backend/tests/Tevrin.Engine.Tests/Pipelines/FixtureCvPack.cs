using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-352 (M67): the <c>fixture-cv</c> pack: agent <c>score</c> (fit-scorer) → agent <c>tailor</c> (cv-tailor),
/// engine-controlled, both reading the project-scope artifact <c>master_cv</c> (§4.76).
/// </summary>
public sealed class FixtureCvPack : IPipelinePack
{
    public const string PipelineId = "fixture-cv";
    public const string ScorePrompt = "cv.score";
    public const string TailorPrompt = "cv.tailor";

    public const string ScoreTemplate = """
            ---
            version: cs1
            ---
            You score how well a CV fits a job.
            <!-- user -->
            <cv>{{MASTER_CV?}}</cv>
            <job>{{JOB?}}</job>
            """;

    public const string TailorTemplate = """
            ---
            version: ct1
            ---
            You tailor CVs.
            <!-- user -->
            <cv>{{MASTER_CV?}}</cv>
            <fit>{{FIT?}}</fit>
            """;

    public string Id => PipelineId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-cv.pipeline.yaml", Fixtures.Read("fixture-cv.pipeline.yaml"));
        registry.AddPrompt(ScorePrompt, ScoreTemplate);
        registry.AddPrompt(TailorPrompt, TailorTemplate);
    }
}
