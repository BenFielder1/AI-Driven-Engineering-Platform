using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Artifacts;

/// <summary>FR-18 (when a draft can be saved), FR-20 (integrity on read), FR-17/FR-19 with §4.2 (stale gates).</summary>
public class ArtifactDraftAndIntegrityRulesTests
{
    [Theory]
    [InlineData(SdlcArtifacts.ProjectRequest, Phase.Refinement)]
    [InlineData(SdlcArtifacts.ProjectRequest, Phase.Review)]
    [InlineData(SdlcArtifacts.TechnicalSpecification, Phase.Specification)]
    [InlineData(SdlcArtifacts.TechnicalSpecification, Phase.Planning)]
    [InlineData(SdlcArtifacts.TechnicalSpecification, Phase.Review)]
    [InlineData(SdlcArtifacts.ImplementationPlan, Phase.Planning)]
    [InlineData(SdlcArtifacts.ImplementationPlan, Phase.Execution)]
    [InlineData(SdlcArtifacts.OptimizationPlan, Phase.Review)]
    public void FR18_A_type_can_be_drafted_from_its_producing_phase_onwards(string type, Phase phase) =>
        Assert.True(ArtifactRules.CanCreateDraft(type, phase, PhaseOrder.StatusFor(phase)).IsSuccess);

    [Theory]
    [InlineData(SdlcArtifacts.TechnicalSpecification, Phase.Refinement)]
    [InlineData(SdlcArtifacts.ImplementationPlan, Phase.Refinement)]
    [InlineData(SdlcArtifacts.ImplementationPlan, Phase.Specification)]
    [InlineData(SdlcArtifacts.OptimizationPlan, Phase.Planning)]
    [InlineData(SdlcArtifacts.OptimizationPlan, Phase.Execution)]
    [InlineData(SdlcArtifacts.OptimizationPlan, Phase.Release)]
    public void FR18_A_type_can_not_be_drafted_before_its_producing_phase_and_the_optimisation_plan_only_in_review(string type, Phase phase)
    {
        var result = ArtifactRules.CanCreateDraft(type, phase, ProjectStatus.Active);

        Assert.Equal(ErrorCodes.WrongPhase, result.Error?.Code);
    }

    [Fact]
    public void FR18_Nothing_can_be_drafted_while_a_release_is_in_progress()
    {
        Assert.Equal(ErrorCodes.WrongPhase, ArtifactRules.CanCreateDraft(SdlcArtifacts.TechnicalSpecification, Phase.Release, ProjectStatus.Releasing).Error?.Code);
        Assert.True(ArtifactRules.CanCreateDraft(SdlcArtifacts.TechnicalSpecification, Phase.Release, ProjectStatus.Released).IsSuccess);
    }

    [Fact]
    public void FR18_A_rolled_back_project_can_be_edited_in_its_phase() =>
        Assert.True(ArtifactRules.CanCreateDraft(SdlcArtifacts.ImplementationPlan, Phase.Planning, ProjectStatus.RolledBack).IsSuccess);

    [Fact]
    public void FR20_Integrity_is_ok_when_the_hash_matches_and_corrupted_otherwise()
    {
        var hash = ContentHash.Compute("# Plan\r\n");

        Assert.Equal(ArtifactIntegrity.Ok, ArtifactRules.IntegrityOf(ArtifactStatus.Locked, "# Plan\n", hash));
        Assert.Equal(ArtifactIntegrity.Corrupted, ArtifactRules.IntegrityOf(ArtifactStatus.Locked, "# Plan (edited)\n", hash));
    }

    [Fact]
    public void FR20_A_row_already_marked_corrupted_stays_corrupted_even_if_the_hash_matches()
    {
        var hash = ContentHash.Compute("x");

        Assert.Equal(ArtifactIntegrity.Corrupted, ArtifactRules.IntegrityOf(ArtifactStatus.Corrupted, "x", hash));
    }

    [Fact]
    public void FR20_A_corrupted_human_authored_artifact_is_marked_but_no_agent_reruns()
    {
        var change = ArtifactRules.OnIntegrityCheck(
            SdlcArtifacts.TechnicalSpecification, ArtifactStatus.Locked, "altered", ContentHash.Compute("original"), authoredByAgent: false);

        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Corrupted, true, null), change);
    }

    [Fact]
    public void FR20_A_corrupted_version_that_is_not_current_is_marked_but_no_agent_reruns()
    {
        var change = ArtifactRules.OnIntegrityCheck(
            SdlcArtifacts.TechnicalSpecification, ArtifactStatus.Superseded, "altered", ContentHash.Compute("original"), authoredByAgent: true, isCurrentVersion: false);

        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Corrupted, true, null), change);
    }

    [Fact]
    public void FR20_A_corrupted_current_agent_version_reruns_its_producing_agent()
    {
        var change = ArtifactRules.OnIntegrityCheck(
            SdlcArtifacts.ImplementationPlan, ArtifactStatus.Draft, "altered", ContentHash.Compute("original"), authoredByAgent: true, isCurrentVersion: true);

        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Corrupted, true, SdlcAgents.PlanGenerator), change);
    }

    [Theory]
    [InlineData(3, ArtifactStatus.Draft, true)] // the open draft
    [InlineData(2, ArtifactStatus.Locked, true)] // the highest locked version, even with a newer draft
    [InlineData(1, ArtifactStatus.Locked, false)] // an older locked version
    [InlineData(1, ArtifactStatus.Superseded, false)]
    [InlineData(1, ArtifactStatus.Rejected, false)]
    [InlineData(1, ArtifactStatus.Invalidated, false)]
    public void FR20_Only_the_open_draft_or_the_highest_locked_version_is_current(int version, ArtifactStatus status, bool expected)
    {
        ArtifactVersion[] all = [new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Locked), new(3, ArtifactStatus.Draft)];
        var others = all.Where(v => v.Version != version);

        Assert.Equal(expected, ArtifactRules.IsCurrentVersion(new ArtifactVersion(version, status), others));
    }

    [Fact]
    public void FR20_An_older_locked_version_is_current_when_every_newer_one_is_superseded_or_invalidated() =>
        Assert.True(ArtifactRules.IsCurrentVersion(
            new ArtifactVersion(1, ArtifactStatus.Locked),
            [new(2, ArtifactStatus.Invalidated), new(3, ArtifactStatus.Superseded)]));

    [Fact]
    public void FR18_A_draft_without_a_base_version_is_always_allowed()
    {
        Assert.True(ArtifactRules.CheckBaseVersion(null, []).IsSuccess);
        Assert.True(ArtifactRules.CheckBaseVersion(null, [new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Draft)]).IsSuccess);
    }

    [Fact]
    public void FR18_A_draft_based_on_the_latest_version_is_allowed() =>
        Assert.True(ArtifactRules.CheckBaseVersion(3, [new(1, ArtifactStatus.Superseded), new(2, ArtifactStatus.Locked), new(3, ArtifactStatus.Locked)]).IsSuccess);

    [Fact]
    public void FR18_A_draft_based_on_an_older_version_is_CONFLICT_with_the_latest_version()
    {
        var result = ArtifactRules.CheckBaseVersion(2, [new(1, ArtifactStatus.Locked), new(2, ArtifactStatus.Locked), new(3, ArtifactStatus.Draft)]);

        Assert.Equal(ErrorCodes.Conflict, result.Error?.Code);
        Assert.Equal(["latestVersion: 3"], result.Error!.Details);
    }

    [Fact]
    public void FR18_Editing_a_locked_version_while_a_newer_draft_exists_is_CONFLICT()
    {
        var result = ArtifactRules.CheckBaseVersion(3, [new(3, ArtifactStatus.Locked), new(4, ArtifactStatus.Draft)]);

        Assert.Equal(ErrorCodes.Conflict, result.Error?.Code);
        Assert.Equal(["latestVersion: 4"], result.Error!.Details);
    }

    [Fact]
    public void FR18_A_base_version_when_none_exists_is_CONFLICT()
    {
        var result = ArtifactRules.CheckBaseVersion(1, []);

        Assert.Equal(ErrorCodes.Conflict, result.Error?.Code);
        Assert.Equal(["latestVersion: none"], result.Error!.Details);
    }

    [Fact]
    public void FR16_A_version_without_a_user_author_is_agent_produced()
    {
        Assert.True(ArtifactRules.IsAuthoredByAgent(null));
        Assert.False(ArtifactRules.IsAuthoredByAgent(Guid.NewGuid()));
    }

    [Fact]
    public void FR19_A_pending_gate_for_an_older_version_of_the_approved_type_is_stale()
    {
        var approved = Guid.NewGuid();
        var older = new PendingGateRef(Guid.NewGuid(), SdlcGates.SpecApproval, Guid.NewGuid());
        var current = new PendingGateRef(Guid.NewGuid(), SdlcGates.SpecApproval, approved);
        var preRelease = new PendingGateRef(Guid.NewGuid(), SdlcGates.PreRelease, null);

        var stale = GateRules.StaleAfterArtifactApproval(approved, SdlcArtifacts.TechnicalSpecification, [], phaseFellBack: false, [older, current, preRelease]);

        Assert.Equal([older.Id], stale);
    }

    [Fact]
    public void FR19_A_pending_gate_whose_artifact_was_invalidated_is_stale()
    {
        var plan = Guid.NewGuid();
        var planGate = new PendingGateRef(Guid.NewGuid(), SdlcGates.PlanApproval, plan);

        var stale = GateRules.StaleAfterArtifactApproval(Guid.NewGuid(), SdlcArtifacts.TechnicalSpecification, [plan], phaseFellBack: false, [planGate]);

        Assert.Equal([planGate.Id], stale);
    }

    [Fact]
    public void FR19_A_phase_fallback_makes_every_pending_gate_stale()
    {
        var escalation = new PendingGateRef(Guid.NewGuid(), EngineGates.Escalation, null);
        var preRelease = new PendingGateRef(Guid.NewGuid(), SdlcGates.PreRelease, null);

        var stale = GateRules.StaleAfterArtifactApproval(Guid.NewGuid(), SdlcArtifacts.TechnicalSpecification, [], phaseFellBack: true, [escalation, preRelease]);

        Assert.Equal([escalation.Id, preRelease.Id], stale);
    }

    [Fact]
    public void FR19_Gates_unrelated_to_the_approval_stay_pending()
    {
        var specGate = new PendingGateRef(Guid.NewGuid(), SdlcGates.SpecApproval, Guid.NewGuid());
        var escalation = new PendingGateRef(Guid.NewGuid(), EngineGates.Escalation, null);

        var stale = GateRules.StaleAfterArtifactApproval(Guid.NewGuid(), SdlcArtifacts.OptimizationPlan, [], phaseFellBack: false, [specGate, escalation]);

        Assert.Empty(stale);
    }
}
