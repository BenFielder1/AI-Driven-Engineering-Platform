using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Domain.Jobs;

/// <summary>
/// §4.9, FR-293: the SDLC job a phase entry queues is the definition's <c>enqueue_on_enter</c> (D-0.7.0-27): spec generation
/// on entering specification (FR-28), plan generation on entering planning (FR-29), a review cycle on entering review
/// (FR-38) and the release job on entering release (FR-42). The engine sets it on an applied advance
/// (<see cref="PhaseTransition.EnqueueOnEnter"/>), which the lifecycle writer queues.
/// </summary>
public static class StageJobs
{
    public static string? OnEnter(PipelineDefinition definition, Phase entered) => SdlcStages.Stage(definition, entered).EnqueueOnEnter;
}
