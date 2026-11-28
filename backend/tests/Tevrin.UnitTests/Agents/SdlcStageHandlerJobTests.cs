using Microsoft.Extensions.DependencyInjection;
using Tevrin.Api.Agents;
using Tevrin.Api.Features.BuildChecks;
using Tevrin.Api.Jobs;
using Tevrin.Api.Pipelines;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Stages;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// FR-319, D-0.7.0-43: the execution and build check jobs run their stage work as the <c>execution</c> stage's handlers
/// (<c>handlers: [sdlc.execution, sdlc.build-check]</c>, no <c>handler:</c>), dispatched by the engine when
/// the run's pinned definition declares them, and directly otherwise (versions 1 and 2, no run, or a definition this build
/// can't run). Both paths run the same scoped handler once with the same job, and its exceptions reach the job layer
/// unchanged, so the events, steps, commits, queueing and job outcome are the same either way: the integration tests and
/// characterisation snapshots (frozen) check those on the real handlers.
/// </summary>
public sealed class SdlcStageHandlerJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly FakePins _pins = new();

    public SdlcStageHandlerJobTests()
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        // The shipped definition, version 3 (D-0.7.0-43: execution's handlers: list).
        services.AddPipelinePack(new SdlcPack());
        services.AddSingleton(Calls);
        services.AddPipelineStageHandler<FakeExecution>(SdlcStageHandlerNames.Execution);
        services.AddPipelineStageHandler<FakeBuildCheck>(SdlcStageHandlerNames.BuildCheck);
        services.AddScoped<IEngineDbContext, OfflineEngineDbContext>();
        services.AddStageHandlers();
        services.AddSingleton<ISdlcRunPins>(_pins);
        services.AddScoped<SdlcStageHandlerJob>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        var registry = _provider.GetRequiredService<PipelineRegistry>();
        Assert.True(registry.IsValid, string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        Assert.True(registry.TryGet(SdlcPack.PipelineId, out var v3));
        V3Hash = v3.Hash!;

        // Version 2 (no handlers), as a run pinned to it finds it: stored.
        var version2 = SdlcOutputGuardFixture.Version2(v3.Definition!);
        Version2Hash = DefinitionHash.Compute(version2);
        Assert.Equal(Tevrin.Infrastructure.Persistence.SdlcRunRepin.Version2Hash, Version2Hash);
        _provider.GetRequiredService<PipelineDefinitionCache>().Add(Version2Hash, new StoredPipelineDefinition(SdlcPack.PackId, version2));
    }

    private string V3Hash { get; }

    private string Version2Hash { get; }

    private CallLog Calls { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid ProjectId = Guid.CreateVersion7();

    private static JobExecution Job(string type) => new(Guid.CreateVersion7(), type, "{}", ProjectId, 1);

    private SdlcStageHandlerJob Dispatch => _scope.ServiceProvider.GetRequiredService<SdlcStageHandlerJob>();

    private FakeExecution Execution => _scope.ServiceProvider.GetRequiredService<FakeExecution>();

    private FakeBuildCheck BuildCheck => _scope.ServiceProvider.GetRequiredService<FakeBuildCheck>();

    public void Dispose()
    {
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _provider.Dispose();
    }

    [Fact]
    public void FR319_version_3s_handler_lines_declare_both_handlers_on_execution_and_keep_it_the_execution_stage()
    {
        var definition = _provider.GetRequiredService<PipelineRegistry>().GetByHash(V3Hash)!.Definition!;
        var execution = definition.FindStage("execution")!;

        Assert.Null(execution.Handler); // D-0.7.0-43: the stage stays manual
        Assert.Equal([SdlcStageHandlerNames.Execution, SdlcStageHandlerNames.BuildCheck], execution.Handlers);
        Assert.Equal("execution", StageHandlers.StageOf(definition, SdlcStageHandlerNames.Execution)?.Id);
        Assert.Equal([SdlcAgents.CodeGenerator, SdlcAgents.BuildFix], execution.Agents.Select(a => a.Key));
        Assert.Equal("execution", StageHandlers.StageOf(definition, SdlcStageHandlerNames.BuildCheck)?.Id);
        Assert.NotEqual(Version2Hash, V3Hash);
    }

    [Fact]
    public async Task FR319_a_run_pinned_to_version_3_runs_the_execution_job_through_the_engine_handler()
    {
        var run = _pins.Pin(V3Hash);
        var job = Job(SdlcJobTypes.Execution);

        await Dispatch.RunAsync(job, Execution, Ct);

        var call = Assert.Single(Calls.Entries);
        Assert.Equal((SdlcStageHandlerNames.Execution, job, true), (call.Handler, call.Job, call.ViaEngine));
        Assert.Equal((run, ProjectId, "execution"), (call.Context!.RunId, call.Context.ProjectId, call.Context.StageId));
    }

    [Fact]
    public async Task FR319_a_run_pinned_to_version_3_runs_the_build_check_job_through_the_engine_handler()
    {
        _pins.Pin(V3Hash);
        var job = Job(SdlcJobTypes.BuildCheck);

        await Dispatch.RunAsync(job, BuildCheck, Ct);

        var call = Assert.Single(Calls.Entries);
        Assert.Equal((SdlcStageHandlerNames.BuildCheck, job, true, "execution"), (call.Handler, call.Job, call.ViaEngine, call.Context!.StageId));
    }

    [Theory]
    [InlineData(SdlcJobTypes.Execution)]
    [InlineData(SdlcJobTypes.BuildCheck)]
    public async Task FR319_a_run_pinned_to_a_definition_without_handlers_runs_the_same_handler_directly(string jobType)
    {
        _pins.Pin(Version2Hash);
        var job = Job(jobType);

        await Dispatch.RunAsync(job, Handler(jobType), Ct);

        var call = Assert.Single(Calls.Entries);
        Assert.Equal((job, false), (call.Job, call.ViaEngine));
    }

    [Fact]
    public async Task FR319_a_project_without_a_run_or_a_job_without_a_project_runs_directly()
    {
        await Dispatch.RunAsync(Job(SdlcJobTypes.Execution), Execution, Ct);
        _pins.Pin(V3Hash);
        await Dispatch.RunAsync(Job(SdlcJobTypes.Execution) with { ProjectId = null }, Execution, Ct);

        Assert.Equal([false, false], Calls.Entries.Select(c => c.ViaEngine));
        Assert.Equal(1, _pins.Lookups);
    }

    [Fact]
    public async Task FR319_a_definition_this_build_cant_run_runs_directly_so_the_old_code_path_refuses_it_as_before()
    {
        const string unknown = "00000000000000000000000000000000000000000000000000000000000000ff";
        _pins.Pin(unknown);

        await Dispatch.RunAsync(Job(SdlcJobTypes.Execution), Execution, Ct);

        Assert.False(Assert.Single(Calls.Entries).ViaEngine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FR319_the_handlers_job_exceptions_reach_the_job_layer_unchanged_on_both_paths(bool v3)
    {
        _pins.Pin(v3 ? V3Hash : Version2Hash);
        var deferred = new JobDeferredException("Waiting for the preview build and the required checks of the head commit (build check).");
        var failed = new JobFailedException("The step collided with a concurrent change.", transient: true, "CONFLICT");

        BuildCheck.Throw = deferred;
        Assert.Same(deferred, await Assert.ThrowsAsync<JobDeferredException>(() => Dispatch.RunAsync(Job(SdlcJobTypes.BuildCheck), BuildCheck, Ct)));
        Execution.Throw = failed;
        Assert.Same(failed, await Assert.ThrowsAsync<JobFailedException>(() => Dispatch.RunAsync(Job(SdlcJobTypes.Execution), Execution, Ct)));
        Assert.All(Calls.Entries, c => Assert.Equal(v3, c.ViaEngine));
    }

    [Fact]
    public async Task FR319_the_engine_path_without_a_job_is_refused()
    {
        var context = new StageHandlerContext(Guid.CreateVersion7(), ProjectId, "execution", FactBag.Empty);

        var refused = await Assert.ThrowsAsync<JobFailedException>(() => ((IStageHandler)Execution).ExecuteAsync(context, Ct));

        Assert.Equal(("VALIDATION", false), (refused.ErrorCode, refused.Transient));
        Assert.Empty(Calls.Entries);
    }

    [Fact]
    public void FR319_the_sdlc_binds_its_handler_names_to_the_moved_job_code()
    {
        var services = new ServiceCollection().AddSdlcPipeline().AddSdlcJobHandlers();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.Equal(
            [typeof(SdlcExecutionStageHandler), typeof(SdlcBuildCheckStageHandler)],
            SdlcStageHandlerNames.All.Select(name => registry.ImplementationOf(PipelineComponentKind.StageHandler, name)));
        Assert.All(SdlcStageHandlerNames.All, name => Assert.Contains(name, registry.Packs.Single(p => p.PackId == SdlcPack.PackId).Handlers));
        Assert.Contains(services, d => d.ServiceType == typeof(StageHandlerRunner));
        Assert.Contains(services, d => d.ServiceType == typeof(ISdlcRunPins) && d.ImplementationType == typeof(SdlcRunPins));
    }

    private SdlcJobStageHandler Handler(string jobType) => jobType == SdlcJobTypes.Execution ? Execution : BuildCheck;

    private sealed class FakePins : ISdlcRunPins
    {
        private SdlcRunPin? _pin;

        public int Lookups { get; private set; }

        public Guid Pin(string hash)
        {
            _pin = new SdlcRunPin(Guid.CreateVersion7(), hash);
            return _pin.RunId;
        }

        public Task<SdlcRunPin?> FindAsync(Guid projectId, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult(projectId == ProjectId ? _pin : null);
        }
    }

    private sealed record Call(string Handler, JobExecution Job, bool ViaEngine, StageHandlerContext? Context);

    private sealed class CallLog
    {
        public List<Call> Entries { get; } = [];
    }

    /// <summary>Records each run of the job's work, and whether the engine dispatched it (<see cref="IStageHandler.ExecuteAsync"/>).</summary>
    private abstract class FakeStage(CallLog calls) : SdlcJobStageHandler, IStageHandler
    {
        private StageHandlerContext? _context;

        public Exception? Throw { get; set; }

        async Task<StageHandlerResult> IStageHandler.ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
        {
            _context = context;
            try
            {
                return await ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                _context = null;
            }
        }

        public override Task RunJobAsync(JobExecution job, CancellationToken cancellationToken)
        {
            calls.Entries.Add(new Call(Name, job, _context is not null, _context));
            return Throw is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }

    private sealed class FakeExecution(CallLog calls) : FakeStage(calls)
    {
        public override string Name => SdlcStageHandlerNames.Execution;
    }

    private sealed class FakeBuildCheck(CallLog calls) : FakeStage(calls)
    {
        public override string Name => SdlcStageHandlerNames.BuildCheck;
    }
}
