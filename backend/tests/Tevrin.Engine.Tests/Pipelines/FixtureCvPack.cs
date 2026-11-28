using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-352 (M67): the <c>fixture-cv</c> pack: agent <c>score</c> (fit-scorer) → agent <c>tailor</c> (cv-tailor),
/// engine-controlled, both reading the project-scope artifact <c>master_cv</c> (§4.76); FR-360 (M71d): then the external
/// handler stage <c>submit</c>.
/// </summary>
public sealed class FixtureCvPack : IPipelinePack
{
    public const string PipelineId = "fixture-cv";
    public const string ScorePrompt = "cv.score";
    public const string TailorPrompt = "cv.tailor";

    /// <summary>
    /// FR-360 (M71): the "submit application" handler of the <c>submit</c> stage, registered <c>external: true</c>, so it
    /// sends only after an approved <c>external_action</c> gate. The integration tests' copy of the pack registers it too.
    /// </summary>
    public const string SubmitApplicationHandler = "fixture-cv.submit-application";

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
        registry.AddStageHandler(SubmitApplicationHandler, external: true);
    }
}
