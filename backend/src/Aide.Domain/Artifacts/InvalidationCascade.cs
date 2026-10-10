using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Artifacts;

/// <summary>Which execution steps a cascade resets.</summary>
public enum StepReset
{
    None = 0,
    AllSteps = 1,

    /// <summary>Every step not belonging to the newly approved plan.</summary>
    StepsOfOtherPlans = 2,
}

/// <summary>An artifact row as the cascade sees it.</summary>
public sealed record ArtifactRef(Guid Id, ArtifactType Type, ArtifactStatus Status, int ProjectVersionAtCreation);

/// <summary>An execution step row as the cascade and rollback see it.</summary>
public sealed record StepRef(Guid Id, Guid PlanArtifactId, StepStatus Status);

/// <summary>The effect of approving a new version of a type (§4.4).</summary>
public sealed record InvalidationEffect(IReadOnlyList<ArtifactType> InvalidatedTypes, StepReset StepReset, Phase? FallbackPhase);

/// <summary>The concrete rows a cascade touches.</summary>
public sealed record InvalidationPlan(InvalidationEffect Effect, IReadOnlyList<Guid> InvalidatedArtifactIds, IReadOnlyList<Guid> ResetStepIds);

/// <summary>§4.4 / FR-19. Invalidation never deletes rows; it never rewrites history.</summary>
public static class InvalidationCascade
{
    /// <summary>
    /// What approving <paramref name="approved"/> invalidates, and the phase to fall back to: the phase
    /// producing the first invalidated thing (steps count as produced in execution), only when the project
    /// is currently in a later phase.
    /// </summary>
    public static InvalidationEffect Effect(ArtifactType approved, Phase currentPhase)
    {
        var (types, steps) = approved switch
        {
            ArtifactType.ProjectRequest => (
                new[] { ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan },
                StepReset.AllSteps),
            ArtifactType.TechnicalSpecification => (
                new[] { ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan },
                StepReset.AllSteps),
            ArtifactType.ImplementationPlan => (
                new[] { ArtifactType.OptimizationPlan },
                StepReset.StepsOfOtherPlans),
            ArtifactType.OptimizationPlan => (Array.Empty<ArtifactType>(), StepReset.None),
            _ => throw new ArgumentOutOfRangeException(nameof(approved), approved, null),
        };

        var producingPhases = types.Select(ArtifactRules.ProducingPhase).ToList();
        if (steps != StepReset.None)
        {
            producingPhases.Add(Phase.Execution);
        }

        Phase? fallback = producingPhases.Count == 0 ? null : producingPhases.Min();
        if (fallback is { } f && !f.IsBefore(currentPhase))
        {
            fallback = null;
        }

        return new InvalidationEffect(types, steps, fallback);
    }

    /// <summary>Selects the rows to invalidate and reset when <paramref name="approvedArtifactId"/> is approved.</summary>
    public static InvalidationPlan Plan(
        Guid approvedArtifactId,
        ArtifactType approvedType,
        Phase currentPhase,
        IEnumerable<ArtifactRef> artifacts,
        IEnumerable<StepRef> steps)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(steps);

        var effect = Effect(approvedType, currentPhase);
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
