using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

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
    public void FR321_the_committed_version_1_fixture_is_the_definition_runs_were_pinned_to() =>
        Assert.Equal(SdlcRunRepin.Version1Hash, Version1().Hash);

    [Fact]
    public async Task FR321_a_run_in_review_pinned_to_version_1_is_re_pinned_and_the_new_definition_is_stored()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await PinAsync(id, Version1(), "review", "reviewing", GenericRunStatus.Active);

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
        await PinAsync(id, Version1(), "review", "reviewing", GenericRunStatus.Active);

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
        var another = Version1(d => d with { Title = "Another definition" });
        await PinAsync(other.Project.Id, another, "review", "reviewing", GenericRunStatus.Active);
        await PinAsync(released.Project.Id, Version1(), "release", "released", GenericRunStatus.Completed);

        await RepinAsync();

        Assert.Equal(another.Hash, (await RunAsync(other.Project.Id)).DefinitionHash);
        Assert.Equal(SdlcRunRepin.Version1Hash, (await RunAsync(released.Project.Id)).DefinitionHash);
    }

    private Task<int> RepinAsync() => ProjectApi.WithDbAsync(factory, db => SdlcRunRepin.RunAsync(db, Sdlc, DateTimeOffset.UtcNow, Ct));

    /// <summary>
    /// The committed copy of <c>sdlc.pipeline.yaml</c> version 1 (as of 414c540), optionally edited, as a loaded definition:
    /// its stored JSON is real, so the resolver can read it.
    /// </summary>
    private static LoadedPipelineDefinition Version1(Func<PipelineDefinition, PipelineDefinition>? edit = null)
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", "sdlc-v1.pipeline.yaml"));
        var definition = PipelineDefinitionLoader.Parse(yaml).Definition!;
        definition = edit?.Invoke(definition) ?? definition;
        return new LoadedPipelineDefinition(SdlcPack.PackId, "sdlc-v1.pipeline.yaml", definition, DefinitionHash.Compute(definition), DefinitionHash.CanonicalJson(definition), []);
    }

    /// <summary>
    /// The project's run as a 0.7.0 build before FR-321 left it (raw update: no sync, no xmin from the app), after storing
    /// the definition it pins (pipeline_runs.definition_hash references pipeline_definitions, which real data always has).
    /// </summary>
    private async Task<int> PinAsync(Guid projectId, LoadedPipelineDefinition definition, string stage, string packStatus, GenericRunStatus status)
    {
        await ProjectApi.WithDbAsync(factory, db => StoredDefinitions.StoreAsync(db, definition, DateTimeOffset.UtcNow, Ct));
        var hash = definition.Hash!;
        return await ProjectApi.WithDbAsync(factory, db => db.PipelineRuns
            .Where(r => r.ProjectId == projectId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.DefinitionHash, hash).SetProperty(r => r.CurrentStage, stage).SetProperty(r => r.PackStatus, packStatus).SetProperty(r => r.Status, status),
                Ct));
    }

    private Task<Tevrin.Engine.Domain.Entities.PipelineRun> RunAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == projectId, Ct));
}
