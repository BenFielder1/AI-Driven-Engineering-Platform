using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-352 (M67): the test pack <c>fixture-cv</c> (agent score → agent tailor, engine-controlled, both reading the
/// project-scope master CV), read from the engine tests' fixture file. FR-360 (M71d): then the <c>submit</c> stage, whose
/// handler <see cref="SubmitApplicationHandler"/> is registered <c>external: true</c>, as in the engine tests' pack.
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
        registry.AddStageHandler(SubmitApplicationHandler.Name, external: true);
    }
}

/// <summary>FR-360: every payload <see cref="SubmitApplicationHandler"/> prepared and every one it sent, in order.</summary>
public sealed class SubmissionLog
{
    private readonly ConcurrentQueue<string> _prepared = new();
    private readonly ConcurrentQueue<string> _sent = new();

    public IReadOnlyList<string> Prepared => [.. _prepared];

    public IReadOnlyList<string> Sent => [.. _sent];

    public void AddPrepared(string payload) => _prepared.Enqueue(payload);

    public void AddSent(string payload) => _sent.Enqueue(payload);
}

/// <summary>
/// FR-360: the external "submit application" handler. Each payload it prepares carries a new sequence number, so a send
/// of a recomputed payload would differ from the one the gate showed; it records whether, how often and what it sent.
/// </summary>
public sealed class SubmitApplicationHandler(SubmissionLog log) : IExternalStageHandler
{
    public const string Name = "fixture-cv.submit-application";

    public Task<ExternalActionPayload> PreparePayloadAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = JsonSerializer.Serialize(new { to = "jobs@example.test", run = context.RunId, prepared = log.Prepared.Count + 1 });
        log.AddPrepared(payload);
        return Task.FromResult(new ExternalActionPayload(payload));
    }

    public Task<StageHandlerResult> SendAsync(StageHandlerContext context, ExternalActionPayload approved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approved);
        log.AddSent(approved.Content);
        return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Completed));
    }
}

/// <summary>The API with <c>fixture-cv</c> registered; its <c>stage.run</c> jobs are driven with <see cref="FixtureLinearApp"/>.</summary>
public static class FixtureCvApp
{
    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddPipelinePack(new FixtureCvPack());
            s.AddSingleton<SubmissionLog>();
            s.AddPipelineStageHandler<SubmitApplicationHandler>(SubmitApplicationHandler.Name);
        }));
}
