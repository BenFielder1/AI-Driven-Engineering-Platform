using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.UnitTests.Artifacts;

/// <summary>§4.3, FR-16–FR-18, FR-20.</summary>
public class ArtifactRulesTests
{
    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("hello\n", "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03")]
    public void FR16_Content_hash_is_lowercase_hex_sha256_of_utf8(string content, string expected) =>
        Assert.Equal(expected, ContentHash.Compute(content));

    [Theory]
    [InlineData("hello\r\n")]
    [InlineData("hello\r")]
    public void S4_3_Line_endings_are_normalised_before_hashing(string content) =>
        Assert.Equal(ContentHash.Compute("hello\n"), ContentHash.Compute(content));

    [Fact]
    public void S4_3_Hash_uses_utf8_for_non_ascii_content()
    {
        var utf8 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("café")));
        var utf16 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes("café")));

        Assert.Equal(utf8, ContentHash.Compute("café"));
        Assert.NotEqual(utf16, ContentHash.Compute("café"));
    }

    [Fact]
    public void FR20_Integrity_mismatch_marks_corrupted_and_reruns_the_producing_agent()
    {
        var hash = ContentHash.Compute("# Spec v3");

        var intact = ArtifactRules.OnIntegrityCheck(ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, "# Spec v3", hash);
        var altered = ArtifactRules.OnIntegrityCheck(ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, "# Spec v3 (edited in DB)", hash);

        Assert.False(intact.Changed);
        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Corrupted, true, AgentKind.SpecGenerator), altered);
    }

    [Fact]
    public void FR20_An_already_corrupted_artifact_is_not_reported_again()
    {
        var result = ArtifactRules.OnIntegrityCheck(ArtifactType.ImplementationPlan, ArtifactStatus.Corrupted, "x", "bad");

        Assert.False(result.Changed);
    }

    [Fact]
    public void FR16_New_spec_after_v2_is_v3_with_parent_v2_draft()
    {
        var plan = ArtifactRules.NewDraft([new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Locked)]);

        Assert.Equal(3, plan.Version);
        Assert.Equal(2, plan.ParentVersion);
        Assert.Empty(plan.SupersededVersions);
    }

    [Fact]
    public void FR16_First_version_has_no_parent()
    {
        var plan = ArtifactRules.NewDraft([]);

        Assert.Equal(1, plan.Version);
        Assert.Null(plan.ParentVersion);
    }

    [Fact]
    public void FR18_Edit_of_locked_v3_creates_v4_draft_with_parent_v3()
    {
        var plan = ArtifactRules.NewDraft([new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Superseded), new(3, ArtifactStatus.Locked)]);

        Assert.Equal((4, 3), (plan.Version, plan.ParentVersion));
    }

    [Fact]
    public void S4_3_A_new_draft_supersedes_the_previous_draft()
    {
        var plan = ArtifactRules.NewDraft([new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Draft)]);

        Assert.Equal(3, plan.Version);
        Assert.Equal([2], plan.SupersededVersions);
    }

    [Fact]
    public void S4_3_Current_is_the_highest_locked_version()
    {
        Assert.Equal(2, ArtifactRules.Current([new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Locked), new(3, ArtifactStatus.Draft), new(4, ArtifactStatus.Rejected)]));
        Assert.Null(ArtifactRules.Current([new(1, ArtifactStatus.Draft)]));
    }

    [Fact]
    public void FR17_Approving_a_draft_locks_it()
    {
        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Locked, true), ArtifactRules.Approve(ArtifactStatus.Draft).Value);
    }

    [Fact]
    public void FR17_Approving_a_locked_artifact_is_a_no_op()
    {
        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Locked, false), ArtifactRules.Approve(ArtifactStatus.Locked).Value);
    }

    [Theory]
    [InlineData(ArtifactStatus.Rejected)]
    [InlineData(ArtifactStatus.Superseded)]
    [InlineData(ArtifactStatus.Invalidated)]
    [InlineData(ArtifactStatus.Corrupted)]
    public void FR17_Approving_or_rejecting_a_non_draft_is_artifact_not_draft(ArtifactStatus status)
    {
        Assert.Equal(ErrorCodes.ArtifactNotDraft, ArtifactRules.Approve(status).Error!.Code);
        Assert.Equal(ErrorCodes.ArtifactNotDraft, ArtifactRules.Reject(ArtifactType.ProjectRequest, status, true).Error!.Code);
    }

    [Fact]
    public void FR17_Changing_a_locked_artifact_is_artifact_locked()
    {
        Assert.Equal(ErrorCodes.ArtifactLocked, ArtifactRules.Reject(ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, true).Error!.Code);
        Assert.Equal(ErrorCodes.ArtifactLocked, ArtifactRules.EnsureEditable(ArtifactStatus.Locked).Error!.Code);
    }

    [Theory]
    [InlineData(ArtifactType.ProjectRequest, AgentKind.IdeaRefinement)]
    [InlineData(ArtifactType.TechnicalSpecification, AgentKind.SpecGenerator)]
    [InlineData(ArtifactType.ImplementationPlan, AgentKind.PlanGenerator)]
    [InlineData(ArtifactType.OptimizationPlan, AgentKind.CodeReview)]
    public void FR17_Rejecting_an_agent_draft_reruns_its_producing_agent(ArtifactType type, AgentKind agent)
    {
        var result = ArtifactRules.Reject(type, ArtifactStatus.Draft, authoredByAgent: true);

        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Rejected, true, agent), result.Value);
    }

    [Fact]
    public void FR17_Rejecting_a_human_edited_draft_reruns_no_agent()
    {
        var result = ArtifactRules.Reject(ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, authoredByAgent: false);

        Assert.Equal(ArtifactStatus.Rejected, result.Value.NewStatus);
        Assert.Null(result.Value.RerunAgent);
    }

    [Theory]
    [InlineData(ArtifactType.ProjectRequest, Phase.Refinement)]
    [InlineData(ArtifactType.TechnicalSpecification, Phase.Specification)]
    [InlineData(ArtifactType.ImplementationPlan, Phase.Planning)]
    [InlineData(ArtifactType.OptimizationPlan, Phase.Review)]
    public void Producing_phase_per_type(ArtifactType type, Phase phase) =>
        Assert.Equal(phase, ArtifactRules.ProducingPhase(type));

    public static TheoryData<ArtifactStatus, ArtifactStatus> AllStatusPairs()
    {
        var data = new TheoryData<ArtifactStatus, ArtifactStatus>();
        foreach (var from in Enum.GetValues<ArtifactStatus>())
        {
            foreach (var to in Enum.GetValues<ArtifactStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllStatusPairs))]
    public void S4_3_Artifact_status_transition_matrix(ArtifactStatus from, ArtifactStatus to)
    {
        var allowed = (from, to) switch
        {
            (ArtifactStatus.Draft, ArtifactStatus.Locked) => true,
            (ArtifactStatus.Draft, ArtifactStatus.Rejected) => true,
            (ArtifactStatus.Draft, ArtifactStatus.Superseded) => true,
            (ArtifactStatus.Draft, ArtifactStatus.Invalidated) => true,
            (ArtifactStatus.Locked, ArtifactStatus.Invalidated) => true,
            (not ArtifactStatus.Corrupted, ArtifactStatus.Corrupted) => true,
            _ => false,
        };

        Assert.Equal(allowed, ArtifactRules.CanTransition(from, to));
    }

    [Fact]
    public void S4_3_Locked_never_goes_back_to_draft_or_another_state_except_invalidated_or_corrupted()
    {
        var reachable = Enum.GetValues<ArtifactStatus>().Where(to => ArtifactRules.CanTransition(ArtifactStatus.Locked, to));

        Assert.Equal([ArtifactStatus.Invalidated, ArtifactStatus.Corrupted], reachable);
    }
}
