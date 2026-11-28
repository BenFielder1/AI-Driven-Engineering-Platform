using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-331, FR-332 (M66): the test pack <c>fixture-linear</c> (agent draft → agent check → gate approve → handler publish,
/// engine-controlled; draft requires the input brief), read from the engine tests' fixture files.
/// </summary>
public sealed class FixtureLinearPack : IPipelinePack
{
    public const string PipelineId = "fixture-linear";
    public const string PublishHandler = "fixture-linear.publish";

    public string Id => PipelineId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-linear.pipeline.yaml", Read("fixture-linear.pipeline.yaml"));
        registry.AddSchema("brief.schema.json", Read("brief.schema.json"));
        registry.AddStageHandler(PublishHandler);
        registry.AddPrompt("linear.draft", "---\nversion: ld1\n---\nYou write drafts.\n<!-- user -->\nWrite a draft for this brief:\n<brief>{{BRIEF?}}</brief>\n");
        registry.AddPrompt("linear.check", "---\nversion: lc1\n---\nYou check drafts.\n<!-- user -->\n<draft>{{DRAFT?}}</draft>\n");
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", name));
}

/// <summary>The <c>publish</c> handler: records the runs it published.</summary>
public sealed class PublishStageHandler(PublishLog log) : IStageHandler
{
    public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        log.Runs.Enqueue(context.RunId);
        return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Completed));
    }
}

/// <summary>The runs <see cref="PublishStageHandler"/> ran for.</summary>
public sealed class PublishLog
{
    public ConcurrentQueue<Guid> Runs { get; } = new();
}

/// <summary>The API with <c>fixture-linear</c> registered, and a way to drive its <c>stage.run</c> jobs.</summary>
public static class FixtureLinearApp
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddPipelinePack(new FixtureLinearPack());
            s.AddSingleton<PublishLog>();
            s.AddPipelineStageHandler<PublishStageHandler>(FixtureLinearPack.PublishHandler);
        }));

    /// <summary>Runs the project's queued <c>stage.run</c> jobs (and those they queue) until none is left; returns how many ran.</summary>
    public static async Task<int> DriveAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var ran = 0;
        for (var round = 0; round < 20; round++)
        {
            var queued = await QueuedDriverJobsAsync(app, projectId);
            if (queued.Count == 0)
            {
                break;
            }

            foreach (var job in queued)
            {
                await AgentJobs.RunAsync(app, job);
                ran++;
            }
        }

        return ran;
    }

    /// <summary>The project's queued main-flow <c>stage.run</c> jobs (FR-354 follow-ups, due days later, are left out).</summary>
    public static async Task<List<Job>> QueuedDriverJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        [.. (await QueuedStageRunJobsAsync(app, projectId)).Where(j => StageRunPayload.Parse(j.Payload)?.FollowUp is null)];

    /// <summary>FR-354: the project's queued follow-up <c>stage.run</c> jobs, oldest first.</summary>
    public static async Task<List<Job>> QueuedFollowUpJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        [.. (await QueuedStageRunJobsAsync(app, projectId)).Where(j => StageRunPayload.Parse(j.Payload)?.FollowUp is not null)];

    private static Task<List<Job>> QueuedStageRunJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(Ct));
}
