using Aide.Api.Pipelines;
using Aide.Domain.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// FR-290: the SDLC pipeline as the host registers it (<see cref="SdlcPipelineRegistration.AddSdlcPipeline"/>), without a
/// database: the registry is built from the same DI registrations as the app.
/// </summary>
internal static class SdlcPipelineFixture
{
    private static readonly Lazy<ServiceProvider> LazyServices = new(() => new ServiceCollection().AddSdlcPipeline().BuildServiceProvider());

    public static ServiceProvider Services => LazyServices.Value;

    public static PipelineRegistry Registry => Services.GetRequiredService<PipelineRegistry>();

    /// <summary>The loaded SDLC definition (asserted valid).</summary>
    public static PipelineDefinition Definition
    {
        get
        {
            Assert.True(Registry.TryGet("sdlc", out var loaded), string.Join(Environment.NewLine, Registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    /// <summary>The stage for a phase (stage ids are the phases' wire values).</summary>
    public static StageDefinition StageOf(Phase phase) =>
        Definition.FindStage(phase.ToWire()) ?? throw new InvalidOperationException($"No stage '{phase.ToWire()}'.");
}
