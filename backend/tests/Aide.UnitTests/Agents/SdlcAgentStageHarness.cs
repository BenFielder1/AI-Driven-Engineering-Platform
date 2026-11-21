using Aide.Api.Agents;
using Aide.Api.Pipelines;
using Aide.Domain.Agents;
using Aide.Domain.Specs;
using Aide.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318: the SDLC's engine wiring as the host registers it (<see cref="SdlcAgentExtensionRegistration"/>, the SDLC
/// pipeline, the infrastructure's LLM catalog), with the database seams in memory and a scripted Fake provider. A spec job
/// is <see cref="SpecGenerationJobHandler"/>'s request for a first version without a stack.
/// </summary>
internal sealed class SdlcAgentStageHarness : IDisposable
{
    public const string ProjectRequest = "# Demo: Todo API\n\n## Summary\nA todo list API.";

    private readonly ServiceProvider _provider;

    public SdlcAgentStageHarness(params string?[] replies)
        : this(SdlcAgents.SpecGenerator, [], replies)
    {
    }

    public SdlcAgentStageHarness(string agent, IEnumerable<IFakeToolScript> scripts, params string?[] replies)
    {
        Provider = new SequenceProvider(new FakeLlmProvider([new AgentReplies(agent, replies)], scripts));
        Data = new MemoryRunData(Store);
        Context = new NoIterationSource(Data);
        var settings = new Dictionary<string, string?>
        {
            ["Llm:DefaultProvider"] = "fake",
            ["Llm:Providers:Anthropic:DefaultModel"] = "claude-opus-5-5",
            ["Llm:Providers:OpenAI:DefaultModel"] = "gpt-6-astra",
            ["Llm:Providers:Ollama:DefaultModel"] = "qwen2.5-coder",
            ["Llm:Providers:Fake:DefaultModel"] = "fake-1",
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddAideInfrastructure();
        services.AddSdlcPipeline();
        services.AddSdlcAgentExtensions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAgentStageStore>(Store);
        services.AddSingleton<ISdlcAgentRunData>(Data);
        services.AddSingleton<ILlmResolver>(new FixedResolver(Provider));
        services.AddSingleton<ILlmOutputLimits, FixedOutputLimits>();
        services.AddSingleton<IRepoToolOpener>(Repo);
        services.AddSingleton<ISdlcContextSource>(Context);
        services.AddScoped<IEngineDbContext, OfflineEngineDbContext>();
        _provider = services.BuildServiceProvider();

        var registry = _provider.GetRequiredService<PipelineRegistry>();
        Assert.True(registry.TryGet(SdlcPack.PipelineId, out var loaded));
        Data.Project.CurrentReleaseVersionId = Guid.CreateVersion7();
        Run = new PipelineRun
        {
            ProjectId = Data.Project.Id,
            PipelineId = SdlcPack.PipelineId,
            DefinitionHash = loaded.Hash!,
            SubjectId = Data.Project.CurrentReleaseVersionId,
            CurrentStage = "specification",
            PackStatus = "active",
        };
        Store.Runs.Add(Run);
    }

    public MemoryAgentStageStore Store { get; } = new();

    public MemoryRunData Data { get; }

    public NoIterationSource Context { get; }

    public TestRepoOpener Repo { get; } = new();

    public SequenceProvider Provider { get; }

    public PipelineRun Run { get; }

    public IServiceProvider Services => _provider;

    /// <summary>The spec job's request and spec, as <see cref="SpecGenerationJobHandler"/> builds them.</summary>
    public static SdlcAgentStageCall SpecCall(JobExecution job, Guid projectId, string? previousSpec = null, string? feedback = null, bool missingInput = false)
    {
        var context = IterationContext.None.SpecContext;
        AgentRunRequest Request(string? previous, string? userFeedback) => new()
        {
            ProjectId = projectId,
            Agent = SdlcAgents.SpecGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = missingInput ? null : ProjectRequest,
                ["SOURCE_ITEMS"] = null,
                ["SPEC_MODE"] = context.Mode,
                ["BASELINE_SPECIFICATION"] = null,
                ["CODEBASE_SUMMARY"] = null,
                ["PREVIOUS_SPECIFICATION"] = previous,
                ["STACK_GUIDANCE"] = null,
            },
            UserFeedback = userFeedback,
            Repo = RepoScope.BaseCommit,
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.TechnicalSpecification,
            text => SpecValidator.Check(AgentOutput.UnwrapFence(text), context),
            (rejected, error) => Request(AgentOutput.UnwrapFence(rejected), ArtifactAgentJob.JoinFeedback(feedback, SpecValidator.CorrectiveFeedback(error, context))));
        return new SdlcAgentStageCall(Request(previousSpec, feedback), spec);
    }

    /// <summary>Runs the spec stage for <paramref name="job"/> in a new scope, as the job does.</summary>
    public Task<AgentStageResult> RunAsync(JobExecution job, string? previousSpec = null, string? feedback = null, bool missingInput = false) =>
        RunCallAsync(SpecCall(job, Data.Project.Id, previousSpec, feedback, missingInput), "specification");

    /// <summary>Runs <paramref name="call"/> on <paramref name="stageId"/> in a new scope, as the job does.</summary>
    public async Task<AgentStageResult> RunCallAsync(SdlcAgentStageCall call, string stageId)
    {
        ArgumentNullException.ThrowIfNull(call);
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AgentStageCoordinator>()
            .RunAsync(call.ToStageRequest(Run.Id, stageId, JobTriggers.StageEntered), TestContext.Current.CancellationToken);
        result.ThrowIfFailed();
        return result;
    }

    /// <summary>Runs the job layer (<see cref="SdlcAgentStageJob"/>) with <paramref name="target"/> and a recording legacy flow.</summary>
    public async Task<Guid> RunJobAsync(SdlcAgentStageCall call, SdlcStageTarget target, RecordingLegacyJob legacy)
    {
        ArgumentNullException.ThrowIfNull(call);
        await using var scope = _provider.CreateAsyncScope();
        var job = new SdlcAgentStageJob(
            scope.ServiceProvider.GetRequiredService<AgentStageCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SdlcAgentExtension>(),
            new FixedLocator(target),
            legacy,
            NullLogger<SdlcAgentStageJob>.Instance);
        return await job.RunAsync(call.Request, call.Spec, JobTriggers.StageEntered, TestContext.Current.CancellationToken);
    }

    public static JobExecution Job(int attempt = 1, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), SdlcJobTypes.SpecGeneration, "{}", null, attempt);

    public void Dispose() => _provider.Dispose();
}
