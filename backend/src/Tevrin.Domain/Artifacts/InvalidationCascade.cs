using Tevrin.Domain.Execution;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Artifacts;

namespace Tevrin.Domain.Artifacts;

/// <summary>Which execution steps a cascade resets.</summary>
public enum StepReset
{
    None = 0,
    AllSteps = 1,

    /// <summary>Every step not belonging to the newly approved plan.</summary>
    StepsOfOtherPlans = 2,
}

/// <summary>An artifact row as the cascade sees it.</summary>
public sealed record ArtifactRef(Guid Id, string Type, ArtifactStatus Status, int ProjectVersionAtCreation);

/// <summary>An execution step row as the cascade and rollback see it.</summary>
public sealed record StepRef(Guid Id, Guid PlanArtifactId, StepStatus Status);

/// <summary>The effect of approving a new version of a type (§4.4).</summary>
public sealed record InvalidationEffect(IReadOnlyList<string> InvalidatedTypes, StepReset StepReset, Phase? FallbackPhase);

/// <summary>The concrete rows a cascade touches.</summary>
public sealed record InvalidationPlan(InvalidationEffect Effect, IReadOnlyList<Guid> InvalidatedArtifactIds, IReadOnlyList<Guid> ResetStepIds);

/// <summary>§4.4 / FR-19. Invalidation never deletes rows; it never rewrites history. What a type's approval invalidates is
/// the SDLC definition's cascade (FR-293).</summary>
public static class InvalidationCascade
{
    /// <summary>
    /// Selects the rows to invalidate and reset when <paramref name="approvedArtifactId"/> is approved, given the
    /// <paramref name="effect"/> of approving its type (the SDLC definition's cascade, <see cref="SdlcMachine.CascadeEffect"/>).
    /// </summary>
    public static InvalidationPlan Plan(
        Guid approvedArtifactId,
        InvalidationEffect effect,
        IEnumerable<ArtifactRef> artifacts,
        IEnumerable<StepRef> steps)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(steps);

        var artifactIds = artifacts
            .Where(a => a.Id != approvedArtifactId && effect.InvalidatedTypes.Contains(a.Type) && ArtifactRules.ShouldInvalidate(a.Status))
            .Select(a => a.Id)
            .ToList();

        var stepIds = steps
            .Where(s => s.Status != StepStatus.Invalidated)
            .Where(s => effect.StepReset switch
            {
                StepReset.AllSteps => true,
                StepReset.StepsOfOtherPlans => s.PlanArtifactId != approvedArtifactId,
                _ => false,
            })
            .Select(s => s.Id)
            .ToList();

        return new InvalidationPlan(effect, artifactIds, stepIds);
    }
}
