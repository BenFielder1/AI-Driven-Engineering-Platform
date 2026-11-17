using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Jobs;

/// <summary>§4.9 follow-up jobs per phase entry (FR-28, FR-29, FR-38, FR-42), gate rejections (FR-13, D16) and gate targets.</summary>
public class JobTypesTests
{
    [Theory]
    [InlineData(Phase.Refinement, null)]
    [InlineData(Phase.Specification, JobTypes.SpecGeneration)]
    [InlineData(Phase.Planning, JobTypes.PlanGeneration)]
    [InlineData(Phase.Execution, null)]
    [InlineData(Phase.Review, JobTypes.Review)]
    [InlineData(Phase.Release, JobTypes.Release)]
    public void S4_9_Entering_a_phase_queues_its_follow_up_job(Phase entered, string? expected) =>
        Assert.Equal(expected, JobTypes.OnEnter(entered));

    [Theory]
    [InlineData(SdlcAgents.IdeaRefinement, JobTypes.IdeaRefinement)]
    [InlineData(SdlcAgents.SpecGenerator, JobTypes.SpecGeneration)]
    [InlineData(SdlcAgents.PlanGenerator, JobTypes.PlanGeneration)]
    [InlineData(SdlcAgents.CodeGenerator, JobTypes.Execution)]
    [InlineData(SdlcAgents.CodeReview, JobTypes.Review)]
    [InlineData(SdlcAgents.BuildFix, JobTypes.BuildFix)] // FR-256
    public void FR13_Each_agent_runs_as_a_job_type(string agent, string expected) =>
        Assert.Equal(expected, JobTypes.ForAgent(agent));

    [Fact]
    public void FR13_Rejected_spec_and_plan_gates_requeue_their_generators_D16()
    {
        Assert.Equal(JobTypes.SpecGeneration, JobTypes.ForAgent(GateRules.AgentToRerunOnReject(SdlcGates.SpecApproval)!));
        Assert.Equal(JobTypes.PlanGeneration, JobTypes.ForAgent(GateRules.AgentToRerunOnReject(SdlcGates.PlanApproval)!));
    }

    [Theory]
    [InlineData(SdlcGates.SpecApproval, Phase.Planning, SdlcArtifacts.TechnicalSpecification)]
    [InlineData(SdlcGates.PlanApproval, Phase.Execution, SdlcArtifacts.ImplementationPlan)]
    [InlineData(SdlcGates.PreRelease, Phase.Release, null)]
    [InlineData(EngineGates.Escalation, null, null)]
    public void FR12_Gates_guard_one_entry_and_approve_one_artifact(string gate, Phase? entry, string? artifact)
    {
        Assert.Equal(entry, GateRules.GuardedEntry(gate));
        Assert.Equal(artifact, GateRules.GatedArtifact(gate));
    }

    [Fact]
    public void FR12_Every_gate_required_by_a_mode_guards_that_entry()
    {
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            foreach (var phase in PhaseOrder.All)
            {
                if (GateRules.RequiredGate(mode, phase) is { } gate)
                {
                    Assert.Equal(phase, GateRules.GuardedEntry(gate));
                }
            }
        }
    }
}
