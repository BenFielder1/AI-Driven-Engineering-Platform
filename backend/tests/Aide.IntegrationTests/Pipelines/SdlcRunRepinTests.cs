using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Aide.IntegrationTests.Pipelines;

/// <summary>
/// FR-321, D-0.7.0-41: at start-up, non-final SDLC runs pinned to <c>sdlc.pipeline.yaml</c> version 1 are re-pinned to the
/// current definition, which is stored; the re-pin is idempotent and safe on several replicas, and never touches final runs
/// or runs pinned to any other hash.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SdlcRunRepinTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LoadedPipelineDefinition Sdlc => SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>());

    [Fact]
    public async Task FR321_a_run_in_review_pinned_to_version_1_is_re_pinned_and_the_new_definition_is_stored()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await PinAsync(id, SdlcRunRepin.Version1Hash, "review", "reviewing", GenericRunStatus.Active);

        var moved = await ProjectApi.WithDbAsync(factory, db => SdlcRunRepin.RunAsync(db, Sdlc, DateTimeOffset.UtcNow, Ct));

        Assert.True(moved >= 1);
        var run = await RunAsync(id);
        Assert.Equal((Sdlc.Hash, "review", "reviewing"), (run.DefinitionHash, run.CurrentStage, run.PackStatus));
        var hash = Sdlc.Hash!;
        Assert.True(await ProjectApi.WithDbAsync(factory, db => db.PipelineDefinitions.AsNoTracking().AnyAsync(d => d.Hash == hash, Ct)));
    }

    [Fact]
    public async Task FR321_a_second_start_changes_nothing_and_two_replicas_re_pin_each_run_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await PinAsync(id, SdlcRunRepin.Version1Hash, "review", "reviewing", GenericRunStatus.Active);

        // Two replicas starting at once: one UPDATE moves the run, the other finds nothing left.
        await Task.WhenAll(RepinAsync(), RepinAsync());

        await new SdlcRunRepinService(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SdlcRunRepinService>.Instance)
            .StartAsync(Ct);

        var after = await RunAsync(id);
        Assert.Equal(Sdlc.Hash, after.DefinitionHash);
        Assert.Equal(0, await ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.CountAsync(r => r.ProjectId == id && r.DefinitionHash == SdlcRunRepin.Version1Hash, Ct)));
    }

    [Fact]
    public async Task FR321_runs_pinned_to_another_hash_and_final_runs_are_untouched()
    {
        using var other = await ProjectApi.FixtureAsync(factory);
        using var released = await ProjectApi.FixtureAsync(factory);
        await PinAsync(other.Project.Id, "0000000000000000000000000000000000000000000000000000000000000000", "review", "reviewing", GenericRunStatus.Active);
        await PinAsync(released.Project.Id, SdlcRunRepin.Version1Hash, "release", "released", GenericRunStatus.Completed);

        await RepinAsync();

        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000", (await RunAsync(other.Project.Id)).DefinitionHash);
        Assert.Equal(SdlcRunRepin.Version1Hash, (await RunAsync(released.Project.Id)).DefinitionHash);
    }

    private Task<int> RepinAsync() => ProjectApi.WithDbAsync(factory, db => SdlcRunRepin.RunAsync(db, Sdlc, DateTimeOffset.UtcNow, Ct));

    /// <summary>The project's run as a 0.7.0 build before FR-321 left it (raw update: no sync, no xmin from the app).</summary>
    private Task<int> PinAsync(Guid projectId, string hash, string stage, string packStatus, GenericRunStatus status) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns
            .Where(r => r.ProjectId == projectId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.DefinitionHash, hash).SetProperty(r => r.CurrentStage, stage).SetProperty(r => r.PackStatus, packStatus).SetProperty(r => r.Status, status),
                Ct));

    private Task<Tevrin.Engine.Domain.Entities.PipelineRun> RunAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == projectId, Ct));
}
