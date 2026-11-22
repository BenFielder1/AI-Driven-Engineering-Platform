using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Stages;
using Tevrin.Engine.Tests.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Stages;

/// <summary>
/// FR-280, FR-319, D-0.7.0-43: the engine's stage handler dispatch. <see cref="StageHandlerRunner"/> resolves the run's
/// pinned definition, finds the stage declaring the requested handler, resolves the handler by name from DI and invokes it
/// with the request's facts; it reports a definition that doesn't declare the handler or can't run it, and leaves the
/// handler's exceptions to the job layer. Only engine code is loaded.
/// </summary>
public sealed class StageHandlerRunnerTests : IDisposable
{
    private const string Run = "fixture.run";
    private const string Poll = "fixture.poll";
    private const string Unbound = "fixture.unbound";

    /// <summary><see cref="DefinitionYaml.Minimal"/> with summarise as a code stage (<c>handler:</c>) and a secondary handler on check.</summary>
    private static readonly string HandlerYaml =
        Edit("    status: working\n    inputs: [report]\n", $"    status: working\n    handler: {Run}\n    inputs: [report]\n")
            .Replace("    agent: { key: checker }\n", $"    agent: {{ key: checker }}\n    handlers: [{Poll}]\n", StringComparison.Ordinal);

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public StageHandlerRunnerTests()
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack(new HandlerPack(HandlerYaml));
        services.AddSingleton(Recorder);
        services.AddPipelineStageHandler<RecordingHandler>(Run);
        services.AddPipelineStageHandler<RecordingHandler>(Poll);
        services.AddScoped<IEngineDbContext, OfflineEngineDbContext>();
        services.AddStageHandlers();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        var registry = _provider.GetRequiredService<PipelineRegistry>();
        Assert.True(registry.IsValid, string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        Assert.True(registry.TryGet("minimal", out var loaded));
        Hash = loaded.Hash!;
        Definition = loaded.Definition!;
    }

    private string Hash { get; }

    private PipelineDefinition Definition { get; }

    private HandlerRecorder Recorder { get; } = new();

    private StageHandlerRunner Runner => _scope.ServiceProvider.GetRequiredService<StageHandlerRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _provider.Dispose();
    }

    private StageHandlerRequest Request(string handler, string? hash = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), hash ?? Hash, handler) { Facts = FactBag.Empty.With(new StageJobFact(Job)) };

    private static readonly JobExecution Job = new(Guid.CreateVersion7(), "stage.work", "{}", null, 1);

    /// <summary>Stores <see cref="Definition"/>, edited, under a hash no registered definition has (a run pinned to it).</summary>
    private string Stored(Func<PipelineDefinition, PipelineDefinition> edit)
    {
        const string hash = "0000000000000000000000000000000000000000000000000000000000000043";
        _provider.GetRequiredService<PipelineDefinitionCache>().Add(hash, new StoredPipelineDefinition("fixture", edit(Definition)));
        return hash;
    }

    [Fact]
    public async Task D_0_7_0_43_the_handler_is_resolved_by_name_and_runs_on_the_stage_that_declares_it()
    {
        var request = Request(Run);

        var dispatch = await Runner.RunAsync(request, Ct);

        var ran = Assert.IsType<StageHandlerDispatch.Ran>(dispatch);
        Assert.Equal("summarise", ran.StageId);
        Assert.Equal(StageHandlerOutcome.Completed, ran.Result.Outcome);
        var context = Assert.Single(Recorder.Contexts);
        Assert.Equal((request.RunId, request.ProjectId, "summarise"), (context.RunId, context.ProjectId, context.StageId));
        Assert.Same(Job, context.Facts.Get<StageJobFact>()!.Job);
    }

    [Fact]
    public async Task D_0_7_0_43_a_secondary_handler_runs_on_its_stage()
    {
        var ran = Assert.IsType<StageHandlerDispatch.Ran>(await Runner.RunAsync(Request(Poll), Ct));

        Assert.Equal("check", ran.StageId);
        Assert.Equal("check", Assert.Single(Recorder.Contexts).StageId);
    }

    [Fact]
    public async Task D_0_7_0_43_a_definition_without_the_handler_is_not_declared_and_nothing_runs()
    {
        var hash = Stored(d => d with { Stages = [.. d.Stages.Select(s => s with { Handler = null, Handlers = [] })] });

        Assert.IsType<StageHandlerDispatch.NotDeclared>(await Runner.RunAsync(Request(Run, hash), Ct));
        Assert.Empty(Recorder.Contexts);
    }

    [Fact]
    public async Task D_0_7_0_43_a_definition_naming_an_unregistered_handler_is_unavailable()
    {
        var hash = Stored(d => d with { Stages = [.. d.Stages.Select(s => s.Id == "summarise" ? s with { Handlers = ["fixture.gone"] } : s)] });

        var unavailable = Assert.IsType<StageHandlerDispatch.Unavailable>(await Runner.RunAsync(Request(Run, hash), Ct));

        Assert.Equal("handler:fixture.gone", unavailable.Missing);
        Assert.Empty(Recorder.Contexts);
        var refused = Assert.Throws<JobFailedException>(() => unavailable.RequireRan(Run));
        Assert.Equal((EngineErrorCodes.DefinitionUnavailable, false), (refused.ErrorCode, refused.Transient));
    }

    [Fact]
    public async Task D_0_7_0_43_a_handler_the_pack_declares_but_DI_doesnt_bind_is_unavailable()
    {
        var hash = Stored(d => d with { Stages = [.. d.Stages.Select(s => s.Id == "draft" ? s with { Handlers = [Unbound] } : s)] });

        var unavailable = Assert.IsType<StageHandlerDispatch.Unavailable>(await Runner.RunAsync(Request(Unbound, hash), Ct));

        Assert.Contains(Unbound, unavailable.Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void D_0_7_0_43_a_required_handler_that_isnt_declared_fails_with_DEFINITION_UNAVAILABLE()
    {
        var refused = Assert.Throws<JobFailedException>(() => new StageHandlerDispatch.NotDeclared().RequireRan(Run));

        Assert.Equal((EngineErrorCodes.DefinitionUnavailable, false), (refused.ErrorCode, refused.Transient));
    }

    [Fact]
    public void D_0_7_0_43_result_mapping_failed_fails_the_job_with_its_code_and_completed_or_waiting_succeed()
    {
        new StageHandlerResult(StageHandlerOutcome.Completed).ThrowIfFailed(Run);
        new StageHandlerResult(StageHandlerOutcome.Waiting).ThrowIfFailed(Run);

        var coded = Assert.Throws<JobFailedException>(() => new StageHandlerResult(StageHandlerOutcome.Failed, "No.") { ErrorCode = "MISSING_INPUT" }.ThrowIfFailed(Run));
        var plain = Assert.Throws<JobFailedException>(() => new StageHandlerResult(StageHandlerOutcome.Failed).ThrowIfFailed(Run));

        Assert.Equal(("No.", "MISSING_INPUT", false), (coded.Message, coded.ErrorCode, coded.Transient));
        Assert.Equal((EngineErrorCodes.Validation, false), (plain.ErrorCode, plain.Transient));
    }

    [Fact]
    public async Task D_0_7_0_43_the_handlers_result_and_exceptions_reach_the_caller_unchanged()
    {
        Recorder.Next = new StageHandlerResult(StageHandlerOutcome.Waiting, "polling");
        var ran = Assert.IsType<StageHandlerDispatch.Ran>(await Runner.RunAsync(Request(Run), Ct));
        Assert.Same(Recorder.Next, ran.Result);

        var deferred = new JobDeferredException("later");
        Recorder.Throw = deferred;
        Assert.Same(deferred, await Assert.ThrowsAsync<JobDeferredException>(() => Runner.RunAsync(Request(Run), Ct)));
    }

    /// <summary>A pack with <see cref="DefinitionYaml.Minimal"/>'s names, no prompts, and one handler name it doesn't bind.</summary>
    private sealed class HandlerPack(string yaml) : IPipelinePack
    {
        public string Id => "fixture";

        public void Register(IPackRegistry registry)
        {
            registry.AddDefinition("minimal.pipeline.yaml", yaml);
            registry.AddGuard(FixturePack.ReportConsistentGuard);
            registry.AddTool(FixturePack.LookupTool);
            registry.AddSetting(FixturePack.RedraftCycles);
            registry.AddStageHandler(Unbound);
        }
    }

    private sealed class HandlerRecorder
    {
        public List<StageHandlerContext> Contexts { get; } = [];

        public StageHandlerResult Next { get; set; } = new(StageHandlerOutcome.Completed);

        public Exception? Throw { get; set; }
    }

    private sealed class RecordingHandler(HandlerRecorder recorder) : IStageHandler
    {
        public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
        {
            recorder.Contexts.Add(context);
            return recorder.Throw is { } ex ? Task.FromException<StageHandlerResult>(ex) : Task.FromResult(recorder.Next);
        }
    }
}
