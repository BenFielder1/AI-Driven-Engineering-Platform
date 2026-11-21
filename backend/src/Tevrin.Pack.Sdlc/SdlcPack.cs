using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-262, FR-278, FR-290: the SDLC pack, today's software lifecycle on the engine. It registers
/// <c>Definitions/sdlc.pipeline.yaml</c> (an embedded resource), the guard names of §4.52 (<see cref="SdlcGuardNames"/>),
/// the tool and context names (FR-317: <see cref="SdlcToolNames"/>, <see cref="SdlcContextNames"/>), the
/// <see cref="ReviewCycles"/> setting, every SDLC job type (<see cref="SdlcJobTypes"/>) and the artifacts its jobs
/// rather than its stages produce. It references only the engine (D-0.7.0-23): the guard implementations, the settings
/// provider and the start selector are composed by the host from <c>Aide.Domain</c> rules.
/// </summary>
public sealed class SdlcPack : IPipelinePack
{
    /// <summary>The pack id, and the namespace of its guard names.</summary>
    public const string PackId = "sdlc";

    /// <summary>The pipeline id of <c>sdlc.pipeline.yaml</c>.</summary>
    public const string PipelineId = "sdlc";

    /// <summary>The definition's embedded resource (and source) name.</summary>
    public const string DefinitionName = "sdlc.pipeline.yaml";

    /// <summary>FR-14, §4.51: the review loop's cap, <c>maxReviewCycles + extraReviewCycles</c> per project.</summary>
    public const string ReviewCycles = "review_cycles";

    public string Id => PackId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using (var yaml = OpenDefinition())
        {
            registry.AddDefinition(DefinitionName, yaml);
        }

        foreach (var guard in SdlcGuardNames.All)
        {
            registry.AddGuard(guard);
        }

        // FR-317: the repo tools (agent.tools) and the context sections the host's providers supply.
        foreach (var tool in SdlcToolNames.All)
        {
            registry.AddTool(tool);
        }

        foreach (var name in SdlcContextNames.All)
        {
            registry.AddContextName(name);
        }

        registry.AddSetting(ReviewCycles);
        foreach (var jobType in SdlcJobTypes.All)
        {
            registry.AddJobType(jobType);
        }

        // Read by agent stages, written by jobs: the tracker import (FR-170) and the Repository Analyst (FR-70).
        registry.AddArtifactProducer(SdlcArtifacts.SourceItems);
        registry.AddArtifactProducer(SdlcArtifacts.CodebaseSummary);
    }

    /// <summary>The embedded <c>sdlc.pipeline.yaml</c>.</summary>
    public static Stream OpenDefinition() =>
        typeof(SdlcPack).Assembly.GetManifestResourceStream("Definitions/" + DefinitionName)
        ?? throw new InvalidOperationException($"'{DefinitionName}' is not embedded in {typeof(SdlcPack).Assembly.GetName().Name}.");
}
