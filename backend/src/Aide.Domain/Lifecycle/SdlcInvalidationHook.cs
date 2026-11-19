using Aide.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Lifecycle;

/// <summary>
/// FR-289, §4.54: the SDLC's <see cref="IInvalidationHook"/>. Approving a project request, technical specification or stack
/// profile resets every execution step, and approving an implementation plan resets the steps of other plans
/// (<see cref="StepResetFor"/>, 0.6.1's step-reset column). The reset counts as produced in <c>execution</c>, so
/// <see cref="DefinitionCascade.Effect"/> with this hook gives 0.6.1's fallback (<see cref="SdlcMachine.CascadeEffect"/>).
/// <para>
/// The artifact approval selects and writes the reset steps through <see cref="InvalidationCascade.Plan"/> in the same unit
/// of work as the invalidation (FR-292 kept it there: the selection needs the tracked step rows and the approved plan's id,
/// which <see cref="InvalidationContext"/> doesn't carry), so <see cref="OnInvalidatedAsync"/> does nothing.
/// </para>
/// </summary>
public sealed class SdlcInvalidationHook : IInvalidationHook
{
    public static SdlcInvalidationHook Instance { get; } = new();

    public string PipelineId => SdlcPack.PipelineId;

    /// <summary>The steps approving <paramref name="approvedArtifactKey"/> resets (§4.54).</summary>
    public static StepReset StepResetFor(string approvedArtifactKey) => approvedArtifactKey switch
    {
        SdlcArtifacts.ProjectRequest or SdlcArtifacts.TechnicalSpecification or SdlcArtifacts.StackProfile => StepReset.AllSteps,
        SdlcArtifacts.ImplementationPlan => StepReset.StepsOfOtherPlans,
        _ => StepReset.None,
    };

    public IReadOnlyList<string> FallbackStages(string approvedArtifactKey, IReadOnlyList<string> invalidatedArtifactKeys) =>
        StepResetFor(approvedArtifactKey) == StepReset.None ? [] : [Phase.Execution.ToWire()];

    public Task OnInvalidatedAsync(InvalidationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
