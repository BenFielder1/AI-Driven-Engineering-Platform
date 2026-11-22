using Aide.Api.Features.Lifecycle;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Pipelines;

/// <summary>
/// FR-321, D-0.7.0-41, D-0.7.0-44: at start-up, non-final SDLC runs pinned to <c>sdlc.pipeline.yaml</c> version 1 or 2
/// are re-pinned to the current definition (version 3), which is stored; the re-pin is idempotent and safe on several replicas, and never touches final runs
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
    public void FR321_D_0_7_0_44_the_committed_version_2_fixture_is_the_definition_runs_were_pinned_to() =>
        Assert.Equal(SdlcRunRepin.Version2Hash, Version2().Hash);

    [Fact]
    public async Task FR321_D_0_7_0_44_a_run_in_review_pinned_to_version_2_is_re_pinned_too()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await PinAsync(id, Version2(), "review", "reviewing", GenericRunStatus.Active);

        Assert.True(await RepinAsync() >= 1);

        var run = await RunAsync(id);
        Assert.Equal((Sdlc.Hash, "review", "reviewing"), (run.DefinitionHash, run.CurrentStage, run.PackStatus));
    }

    [Fact]
    public async Task FR321_the_start_up_re_pin_logs_one_entry_and_a_second_start_logs_none()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await PinAsync(fixture.Project.Id, Version1(), "review", "reviewing", GenericRunStatus.Active);
        using var other = await ProjectApi.FixtureAsync(factory);
        await PinAsync(other.Project.Id, Version2(), "review", "reviewing", GenericRunStatus.Active);
        var sink = new LogSink();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(sink)));
        SdlcRunRepinService Service() => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, loggers.CreateLogger<SdlcRunRepinService>());
        IReadOnlyList<CapturedLog> Repinned() => [.. sink.Entries.Where(e => e.Message.Contains("re-pinned", StringComparison.Ordinal))];

        await Service().StartAsync(Ct);

        // One entry for both earlier versions' runs.
        var entry = Assert.Single(Repinned());
        Assert.Contains(SdlcRunRepin.Version1Hash, entry.Message, StringComparison.Ordinal);
        Assert.Contains(SdlcRunRepin.Version2Hash, entry.Message, StringComparison.Ordinal);
        Assert.Equal(Sdlc.Hash, (await RunAsync(other.Project.Id)).DefinitionHash);

        await Service().StartAsync(Ct);

        Assert.Single(Repinned());
    }

    [Fact]
    public async Task FR321_the_next_lifecycle_move_of_a_re_pinned_run_behaves_as_on_an_unpinned_one()
    {
        using var unpinned = await ProjectApi.FixtureAsync(factory);
        using var repinned = await ProjectApi.FixtureAsync(factory);
        await PinAsync(repinned.Project.Id, Version2(), "refinement", "active", GenericRunStatus.Active);
        await RepinAsync();
        foreach (var fixture in new[] { unpinned, repinned })
        {
            await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, SdlcArtifacts.ProjectRequest);
        }

        var expected = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(unpinned.Contributor.Client, unpinned.Project.Id));
        var actual = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(repinned.Contributor.Client, repinned.Project.Id));

        Assert.Equal((Phase.Specification, expected.State.Status, expected.State.Checkpoint, expected.Gate is null), (actual.State.Phase, actual.State.Status, actual.State.Checkpoint, actual.Gate is null));
        Assert.Equal(
            (await LifecycleSeed.JobsAsync(factory, unpinned.Project.Id)).Select(j => j.Type),
            (await LifecycleSeed.JobsAsync(factory, repinned.Project.Id)).Select(j => j.Type));
        var run = await RunAsync(repinned.Project.Id);
        Assert.Equal((Sdlc.Hash, "specification"), (run.DefinitionHash, run.CurrentStage));
        Assert.Equal((await RunAsync(unpinned.Project.Id)).PackStatus, run.PackStatus);
    }

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
    private static LoadedPipelineDefinition Version1(Func<PipelineDefinition, PipelineDefinition>? edit = null) => Fixture("sdlc-v1.pipeline.yaml", edit);

    /// <summary>The committed copy of <c>sdlc.pipeline.yaml</c> version 2 (as of 5b99e27), as a loaded definition.</summary>
    private static LoadedPipelineDefinition Version2() => Fixture("sdlc-v2.pipeline.yaml", null);

    private static LoadedPipelineDefinition Fixture(string name, Func<PipelineDefinition, PipelineDefinition>? edit)
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", name));
        var definition = PipelineDefinitionLoader.Parse(yaml).Definition!;
        definition = edit?.Invoke(definition) ?? definition;
        return new LoadedPipelineDefinition(SdlcPack.PackId, name, definition, DefinitionHash.Compute(definition), DefinitionHash.CanonicalJson(definition), []);
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
