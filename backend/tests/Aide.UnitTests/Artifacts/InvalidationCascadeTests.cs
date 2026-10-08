using Aide.Domain.Artifacts;
using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;

namespace Aide.UnitTests.Artifacts;

/// <summary>§4.4, FR-19.</summary>
public class InvalidationCascadeTests
{
    [Fact]
    public void S4_4_Project_request_invalidates_spec_plan_optimisation_and_all_steps()
    {
        var effect = InvalidationCascade.Effect(ArtifactType.ProjectRequest, Phase.Review);

        Assert.Equal([ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan], effect.InvalidatedTypes);
        Assert.Equal(StepReset.AllSteps, effect.StepReset);
        Assert.Equal(Phase.Specification, effect.FallbackPhase);
    }

    [Fact]
    public void S4_4_Spec_invalidates_plan_optimisation_and_all_steps()
    {
        var effect = InvalidationCascade.Effect(ArtifactType.TechnicalSpecification, Phase.Execution);

        Assert.Equal([ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan], effect.InvalidatedTypes);
        Assert.Equal(StepReset.AllSteps, effect.StepReset);
    }

    [Fact]
    public void S4_4_Plan_invalidates_optimisation_and_steps_of_older_plans()
    {
        var effect = InvalidationCascade.Effect(ArtifactType.ImplementationPlan, Phase.Planning);

        Assert.Equal([ArtifactType.OptimizationPlan], effect.InvalidatedTypes);
        Assert.Equal(StepReset.StepsOfOtherPlans, effect.StepReset);
        Assert.Null(effect.FallbackPhase);
    }

    [Theory]
    [InlineData(Phase.Refinement)]
    [InlineData(Phase.Review)]
    [InlineData(Phase.Release)]
    public void S4_4_Optimisation_plan_invalidates_nothing(Phase phase)
    {
        var effect = InvalidationCascade.Effect(ArtifactType.OptimizationPlan, phase);

        Assert.Empty(effect.InvalidatedTypes);
        Assert.Equal(StepReset.None, effect.StepReset);
        Assert.Null(effect.FallbackPhase);
    }

    [Fact]
    public void S4_4_Example_spec_v4_approved_in_review_falls_back_to_planning_active()
    {
        var effect = InvalidationCascade.Effect(ArtifactType.TechnicalSpecification, Phase.Review);
        var transition = LifecycleStateMachine.Apply(
            new LifecycleState(Phase.Review, ProjectStatus.Reviewing, 6),
            TransitionRequest.Fallback(effect.FallbackPhase!.Value),
            LifecycleSnapshot.Empty);

        Assert.Equal(Phase.Planning, effect.FallbackPhase);
        Assert.Equal(Phase.Planning, transition.Value.To);
        Assert.Equal(ProjectStatus.Active, transition.Value.Status);
        Assert.Equal(6, transition.Value.Version);
    }

    [Theory]
    [InlineData(ArtifactType.ProjectRequest, Phase.Refinement)]
    [InlineData(ArtifactType.ProjectRequest, Phase.Specification)]
    [InlineData(ArtifactType.TechnicalSpecification, Phase.Specification)]
    [InlineData(ArtifactType.TechnicalSpecification, Phase.Planning)]
    [InlineData(ArtifactType.ImplementationPlan, Phase.Planning)]
    [InlineData(ArtifactType.ImplementationPlan, Phase.Execution)]
    public void S4_4_No_fallback_when_the_current_phase_is_not_later(ArtifactType approved, Phase current) =>
        Assert.Null(InvalidationCascade.Effect(approved, current).FallbackPhase);

    [Fact]
    public void S4_4_Plan_approved_in_review_falls_back_to_execution_for_the_reset_steps() =>
        Assert.Equal(Phase.Execution, InvalidationCascade.Effect(ArtifactType.ImplementationPlan, Phase.Review).FallbackPhase);

    [Fact]
    public void FR19_Spec_v4_approved_invalidates_the_plan_and_resets_all_its_steps()
    {
        var spec3 = new ArtifactRef(Guid.NewGuid(), ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, 2);
        var spec4 = new ArtifactRef(Guid.NewGuid(), ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, 6);
        var plan = new ArtifactRef(Guid.NewGuid(), ArtifactType.ImplementationPlan, ArtifactStatus.Locked, 3);
        var oldPlan = new ArtifactRef(Guid.NewGuid(), ArtifactType.ImplementationPlan, ArtifactStatus.Superseded, 3);
        var request = new ArtifactRef(Guid.NewGuid(), ArtifactType.ProjectRequest, ArtifactStatus.Locked, 1);
        var steps = new[]
        {
            new StepRef(Guid.NewGuid(), plan.Id, StepStatus.Completed),
            new StepRef(Guid.NewGuid(), plan.Id, StepStatus.Completed),
            new StepRef(Guid.NewGuid(), plan.Id, StepStatus.Pending),
            new StepRef(Guid.NewGuid(), plan.Id, StepStatus.Invalidated),
        };

        var result = InvalidationCascade.Plan(spec4.Id, ArtifactType.TechnicalSpecification, Phase.Review, [spec3, spec4, plan, oldPlan, request], steps);

        Assert.Equal([plan.Id], result.InvalidatedArtifactIds);
        Assert.Equal(steps.Take(3).Select(s => s.Id), result.ResetStepIds);
        Assert.Equal(Phase.Planning, result.Effect.FallbackPhase);
    }

    [Fact]
    public void S4_4_New_plan_resets_only_steps_of_other_plans()
    {
        var newPlan = Guid.NewGuid();
        var olderPlan = Guid.NewGuid();
        var optimisation = new ArtifactRef(Guid.NewGuid(), ArtifactType.OptimizationPlan, ArtifactStatus.Locked, 7);
        var steps = new[]
        {
            new StepRef(Guid.NewGuid(), olderPlan, StepStatus.Completed),
            new StepRef(Guid.NewGuid(), optimisation.Id, StepStatus.Pending),
            new StepRef(Guid.NewGuid(), newPlan, StepStatus.Pending),
        };

        var result = InvalidationCascade.Plan(newPlan, ArtifactType.ImplementationPlan, Phase.Review, [optimisation], steps);

        Assert.Equal([optimisation.Id], result.InvalidatedArtifactIds);
        Assert.Equal([steps[0].Id, steps[1].Id], result.ResetStepIds);
    }
}
