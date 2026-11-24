using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-352 (M67): the test pack <c>fixture-cv</c> (agent score → agent tailor, engine-controlled, both reading the
/// project-scope master CV), read from the engine tests' fixture file.
/// </summary>
public sealed class FixtureCvPack : IPipelinePack
{
    public const string PipelineId = "fixture-cv";

    public string Id => PipelineId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-cv.pipeline.yaml",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", "fixture-cv.pipeline.yaml")));
        registry.AddPrompt("cv.score", "---\nversion: cs1\n---\nYou score how well a CV fits a job.\n<!-- user -->\n<cv>{{MASTER_CV?}}</cv>\n<job>{{JOB?}}</job>\n");
        registry.AddPrompt("cv.tailor", "---\nversion: ct1\n---\nYou tailor CVs.\n<!-- user -->\n<cv>{{MASTER_CV?}}</cv>\n<fit>{{FIT?}}</fit>\n");
    }
}

/// <summary>The API with <c>fixture-cv</c> registered; its <c>stage.run</c> jobs are driven with <see cref="FixtureLinearApp"/>.</summary>
public static class FixtureCvApp
{
    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddPipelinePack(new FixtureCvPack())));
}
