using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// The fixture pipeline wired as a host would wire it, with only engine code: the fixture pack, its guard and tool, the
/// Fake provider with scripted answers, an in-memory store and the offline engine context. No SDLC code is loaded.
/// </summary>
internal sealed class AgentStageHarness : IDisposable
{
    public const string ValidDraft = """{"title":"Plan","items":["run 5k"]}""";
    public const string ValidReport = """{"consistent":true,"findings":[]}""";

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public AgentStageHarness(
        IEnumerable<IFakeAgentResponder> responders,
        Func<string, string>? editYaml = null,
        IEnumerable<IFakeToolScript>? scripts = null,
        IEnumerable<IAgentContextProvider>? contextProviders = null,
        ILlmResolver? resolver = null,
        AgentStageExtension? extension = null)
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack(editYaml is null ? new FixturePack() : new FixturePack(editYaml));
        services.AddPipelineGuard<ReportConsistentGuard>(FixturePack.ReportConsistentGuard);
        services.AddPipelineTool<EchoTool>(FixturePack.LookupTool);
        services.AddSingleton<IAgentStageStore>(Store);
        services.AddSingleton(resolver ?? new FakeResolver(new FakeLlmProvider(responders, scripts ?? [])));
        services.AddSingleton<IAgentStageHooks>(Hooks);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IEngineDbContext, OfflineEngineDbContext>();
        foreach (var contextProvider in contextProviders ?? [])
        {
            services.AddSingleton(contextProvider);
        }

        if (extension is not null)
        {
            services.AddSingleton(extension);
        }

        services.AddAgentStages();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        Registry = _provider.GetRequiredService<PipelineRegistry>();
        Assert.True(Registry.IsValid, string.Join(" | ", Registry.Errors.Select(e => e.Error.ToString())));
        Assert.True(Registry.TryGet("fixture-review", out var loaded));
        Run = new PipelineRun
        {
            ProjectId = Guid.CreateVersion7(),
            PipelineId = "fixture-review",
            DefinitionHash = loaded.Hash!,
            SubjectId = Guid.CreateVersion7(),
            CurrentStage = "draft",
            PackStatus = "drafting",
            ApprovalMode = ApprovalMode.Advisory,
        };
        Store.Runs.Add(Run);
    }

    public InMemoryAgentStageStore Store { get; } = new();

    public RecordingHooks Hooks { get; } = new();

    public PipelineRegistry Registry { get; }

    public PipelineRun Run { get; }

    public AgentStageRunner Runner => _scope.ServiceProvider.GetRequiredService<AgentStageRunner>();

    public AgentStageCoordinator Coordinator => _scope.ServiceProvider.GetRequiredService<AgentStageCoordinator>();

    public IServiceProvider Services => _scope.ServiceProvider;

    public Task<AgentStageResult> RunStageAsync(string stageId, string trigger = JobTriggers.StageEntered, string? feedback = null) =>
        Coordinator.RunAsync(new AgentStageRequest(Run.Id, stageId, trigger, feedback), TestContext.Current.CancellationToken);

    /// <summary>FR-310: runs a stage under the caller's agent run id (as a job would on every delivery).</summary>
    public Task<AgentStageResult> RunStageAsync(string stageId, Guid agentRunId) =>
        Coordinator.RunAsync(new AgentStageRequest(Run.Id, stageId, JobTriggers.StageEntered) { AgentRunId = agentRunId }, TestContext.Current.CancellationToken);

    /// <summary>Locks the latest version of <paramref name="key"/> (as an approval would).</summary>
    public void Lock(string key) => Store.Artifacts.Where(a => a.Type == key).MaxBy(a => a.Version)!.Status = ArtifactStatus.Locked;

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
