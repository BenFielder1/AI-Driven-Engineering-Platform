using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.UnitTests.Artifacts;

/// <summary>FR-58, D-V2-15, §4.12 "Producing phase per artifact type".</summary>
public class RestoreRulesTests
{
    private static readonly Guid Development = Guid.CreateVersion7();
    private static readonly Guid Released = Guid.CreateVersion7();

    private static RestoreFacts Spec(
        int chosen = 2,
        int latest = 3,
        Phase phase = Phase.Specification,
        ProjectStatus status = ProjectStatus.Active,
        Guid? chosenIn = null,
        ArtifactIntegrity integrity = ArtifactIntegrity.Ok,
        bool inDevelopment = true,
        ArtifactType type = ArtifactType.TechnicalSpecification) =>
        new(type, phase, status, inDevelopment ? Development : null, chosenIn ?? Development, chosen, integrity, latest);

    [Fact]
    public void FR58_Restoring_spec_v2_with_v3_current_in_specification_plans_v4_with_parent_3_restored_from_2()
    {
        var plan = RestoreRules.Check(Spec());

        Assert.True(plan.IsSuccess);
        Assert.Equal(new RestorePlan(4, 3, 2), plan.Value);
    }

    [Fact]
    public void FR58_After_advancing_to_planning_restoring_spec_v2_is_stage_completed()
    {
        var result = RestoreRules.Check(Spec(phase: Phase.Planning));

        AssertNotAllowed(result, RestoreReasons.StageCompleted);
    }

    [Theory]
    [InlineData(ArtifactType.ProjectRequest, Phase.Refinement)]
    [InlineData(ArtifactType.TechnicalSpecification, Phase.Specification)]
    [InlineData(ArtifactType.ImplementationPlan, Phase.Planning)]
    [InlineData(ArtifactType.OptimizationPlan, Phase.Review)]
    public void FR58_Each_type_is_restorable_only_in_its_producing_phase(ArtifactType type, Phase producing)
    {
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var result = RestoreRules.Check(Spec(type: type, phase: phase));
            if (phase == producing)
            {
                Assert.True(result.IsSuccess, $"{type} in {phase}");
            }
            else
            {
                AssertNotAllowed(result, phase < producing ? RestoreReasons.StageNotReached : RestoreReasons.StageCompleted);
            }
        }
    }

    [Fact]
    public void FR58_After_a_rollback_to_specification_restoring_implementation_plan_v1_is_stage_not_reached()
    {
        var result = RestoreRules.Check(Spec(type: ArtifactType.ImplementationPlan, phase: Phase.Specification, chosen: 1, latest: 2));

        AssertNotAllowed(result, RestoreReasons.StageNotReached);
    }

    [Fact]
    public void FR58_After_planning_restoring_the_request_is_stage_completed()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(type: ArtifactType.ProjectRequest, phase: Phase.Planning)), RestoreReasons.StageCompleted);
    }

    [Fact]
    public void FR58_The_codebase_summary_is_automatic_and_never_restorable()
    {
        Assert.Null(RestoreRules.RestorablePhase(ArtifactType.CodebaseSummary));
        foreach (var phase in Enum.GetValues<Phase>())
        {
            AssertNotAllowed(RestoreRules.Check(Spec(type: ArtifactType.CodebaseSummary, phase: phase)), RestoreReasons.StageCompleted);
        }
    }

    [Fact]
    public void FR58_While_releasing_restore_is_refused_with_releasing()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(status: ProjectStatus.Releasing)), RestoreReasons.Releasing);
    }

    [Fact]
    public void FR58_A_version_of_another_release_version_is_other_release_version()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(chosen: 1, chosenIn: Released)), RestoreReasons.OtherReleaseVersion);
    }

    [Fact]
    public void FR58_With_nothing_in_development_restore_is_other_release_version()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(inDevelopment: false)), RestoreReasons.OtherReleaseVersion);
    }

    [Fact]
    public void FR58_A_corrupted_version_is_refused_with_corrupted()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(integrity: ArtifactIntegrity.Corrupted)), RestoreReasons.Corrupted);
    }

    [Fact]
    public void FR58_Restoring_the_latest_version_is_VALIDATION()
    {
        var result = RestoreRules.Check(Spec(chosen: 3, latest: 3));

        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Null(result.Error.Reason);
        Assert.Contains(result.Error.Details, d => d.StartsWith("version: ", StringComparison.Ordinal));
    }

    [Fact]
    public void FR58_Releasing_is_checked_before_the_phase_and_the_phase_before_the_release_version()
    {
        AssertNotAllowed(RestoreRules.Check(Spec(status: ProjectStatus.Releasing, phase: Phase.Release, chosenIn: Released)), RestoreReasons.Releasing);
        AssertNotAllowed(RestoreRules.Check(Spec(phase: Phase.Planning, chosenIn: Released, integrity: ArtifactIntegrity.Corrupted)), RestoreReasons.StageCompleted);
        AssertNotAllowed(RestoreRules.Check(Spec(chosenIn: Released, integrity: ArtifactIntegrity.Corrupted)), RestoreReasons.OtherReleaseVersion);
    }

    private static void AssertNotAllowed(Result<RestorePlan> result, string reason)
    {
        Assert.True(result.IsFailure);
        Assert.Equal((ErrorCodes.RestoreNotAllowed, reason), (result.Error!.Code, result.Error.Reason));
    }
}
