using Tevrin.Domain.Gates;
using Tevrin.Domain.Jobs;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;
using Tevrin.UnitTests.Lifecycle;

namespace Tevrin.UnitTests.Jobs;

/// <summary>§4.9 follow-up jobs per phase entry (the SDLC definition's <c>enqueue_on_enter</c>, FR-293) (FR-28, FR-29, FR-38, FR-42), gate rejections (FR-13, D16) and gate targets.</summary>
public class JobTypesTests
{
    [Theory]
    [InlineData(Phase.Refinement, null)]
    [InlineData(Phase.Specification, SdlcJobTypes.SpecGeneration)]
    [InlineData(Phase.Planning, SdlcJobTypes.PlanGeneration)]
    [InlineData(Phase.Execution, null)]
    [InlineData(Phase.Review, SdlcJobTypes.Review)]
    [InlineData(Phase.Release, SdlcJobTypes.Release)]
    public void S4_9_Entering_a_phase_queues_its_follow_up_job(Phase entered, string? expected) =>
        Assert.Equal(expected, StageJobs.OnEnter(Sdlc.Definition, entered));

    [Theory]
    [InlineData(SdlcAgents.IdeaRefinement, SdlcJobTypes.IdeaRefinement)]
    [InlineData(SdlcAgents.SpecGenerator, SdlcJobTypes.SpecGeneration)]
    [InlineData(SdlcAgents.PlanGenerator, SdlcJobTypes.PlanGeneration)]
    [InlineData(SdlcAgents.CodeGenerator, SdlcJobTypes.Execution)]
    [InlineData(SdlcAgents.CodeReview, SdlcJobTypes.Review)]
    [InlineData(SdlcAgents.BuildFix, SdlcJobTypes.BuildFix)] // FR-256
    public void FR13_Each_agent_runs_as_a_job_type(string agent, string expected) =>
        Assert.Equal(expected, SdlcJobTypes.ForAgent(agent));

    [Fact]
    public void FR13_Rejected_spec_and_plan_gates_requeue_their_generators_D16()
    {
        Assert.Equal(SdlcJobTypes.SpecGeneration, SdlcJobTypes.ForAgent(GateRules.AgentToRerunOnReject(SdlcGates.SpecApproval)!));
        Assert.Equal(SdlcJobTypes.PlanGeneration, SdlcJobTypes.ForAgent(GateRules.AgentToRerunOnReject(SdlcGates.PlanApproval)!));
    }

    [Theory]
    [InlineData(SdlcGates.SpecApproval, Phase.Planning, SdlcArtifacts.TechnicalSpecification)]
    [InlineData(SdlcGates.PlanApproval, Phase.Execution, SdlcArtifacts.ImplementationPlan)]
    [InlineData(SdlcGates.PreRelease, Phase.Release, null)]
    [InlineData(EngineGates.Escalation, null, null)]
    public void FR12_Gates_guard_one_entry_and_approve_one_artifact(string gate, Phase? entry, string? artifact)
    {
        Assert.Equal(entry, SdlcStages.GuardedEntry(Sdlc.Definition, gate));
        Assert.Equal(artifact, SdlcStages.GatedArtifact(Sdlc.Definition, gate));
    }

    [Fact]
    public void FR12_Every_gate_required_by_a_mode_guards_that_entry()
    {
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            foreach (var phase in Sdlc.All)
            {
                if (Sdlc.RequiredGate(mode, phase) is { } gate)
                {
                    Assert.Equal(phase, SdlcStages.GuardedEntry(Sdlc.Definition, gate));
                }
            }
        }
    }
}
