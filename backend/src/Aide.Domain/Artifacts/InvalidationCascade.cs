using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Pack.Sdlc;

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
public sealed record ArtifactRef(Guid Id, string Type, ArtifactStatus Status, int ProjectVersionAtCreation);

/// <summary>An execution step row as the cascade and rollback see it.</summary>
public sealed record StepRef(Guid Id, Guid PlanArtifactId, StepStatus Status);

/// <summary>The effect of approving a new version of a type (§4.4).</summary>
public sealed record InvalidationEffect(IReadOnlyList<string> InvalidatedTypes, StepReset StepReset, Phase? FallbackPhase);

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
    public static InvalidationEffect Effect(string approved, Phase currentPhase)
    {
        var (types, steps) = approved switch
        {
            SdlcArtifacts.ProjectRequest => (
                new[] { SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan },
                StepReset.AllSteps),
            SdlcArtifacts.TechnicalSpecification => (
                new[] { SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan },
                StepReset.AllSteps),
            SdlcArtifacts.ImplementationPlan => (
                new[] { SdlcArtifacts.OptimizationPlan },
                StepReset.StepsOfOtherPlans),
            SdlcArtifacts.OptimizationPlan => (Array.Empty<string>(), StepReset.None),

            // FR-70: an input to every stage, auto-locked without approval; a new summary invalidates nothing.
            SdlcArtifacts.CodebaseSummary => (Array.Empty<string>(), StepReset.None),

            // FR-173: a new snapshot invalidates nothing by itself; the cascade follows when a new project request is approved.
            SdlcArtifacts.SourceItems => (Array.Empty<string>(), StepReset.None),

            // §4.27: a new profile (only reachable after a rollback) invalidates everything built on the old stack.
            SdlcArtifacts.StackProfile => (
                new[] { SdlcArtifacts.ProjectRequest, SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan },
                StepReset.AllSteps),
            _ => throw new ArgumentOutOfRangeException(nameof(approved), approved, null),
        };

        var producingPhases = types.Select(SdlcArtifactRules.ProducingPhase).ToList();

        // §4.27: the stack_profile cascade falls back to analysis, where the profile itself is produced.
        if (approved == SdlcArtifacts.StackProfile)
        {
            producingPhases.Add(Phase.Analysis);
        }

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

    /// <summary>
    /// Selects the rows to invalidate and reset when <paramref name="approvedArtifactId"/> is approved. <paramref name="effectOf"/>
    /// computes the effect (default: <see cref="Effect"/>); the host passes its parity machine (FR-291).
    /// </summary>
    public static InvalidationPlan Plan(
        Guid approvedArtifactId,
        string approvedType,
        Phase currentPhase,
        IEnumerable<ArtifactRef> artifacts,
        IEnumerable<StepRef> steps,
        Func<string, Phase, InvalidationEffect>? effectOf = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(steps);

        var effect = effectOf is null ? Effect(approvedType, currentPhase) : effectOf(approvedType, currentPhase);
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
