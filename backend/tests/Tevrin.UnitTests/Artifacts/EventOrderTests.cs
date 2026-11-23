using Tevrin.Api.Features.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Artifacts;

/// <summary>D-0.7.0-28, FR-260: per-row events in one save come out in an explicit order, whatever order the rows were
/// read in (the <c>new_project_request_approved_mid_execution</c> snapshot: the plan's invalidation before the spec's).</summary>
public class EventOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void D_0_7_0_28_invalidated_artifacts_are_newest_first()
    {
        var spec = new Artifact { Type = SdlcArtifacts.TechnicalSpecification, Version = 1, CreatedAt = T0 };
        var plan = new Artifact { Type = SdlcArtifacts.ImplementationPlan, Version = 1, CreatedAt = T0.AddMinutes(5) };
        var plan2 = new Artifact { Type = SdlcArtifacts.ImplementationPlan, Version = 2, CreatedAt = T0.AddMinutes(5) };

        Assert.Equal([plan2, plan, spec], EventOrder.Invalidated([spec, plan, plan2]));
        Assert.Equal([plan2, plan, spec], EventOrder.Invalidated([plan, spec, plan2]));
    }

    [Fact]
    public void D_0_7_0_28_cancelled_gates_are_oldest_first_and_superseded_drafts_lowest_version_first()
    {
        var older = new ApprovalGate { Type = SdlcGates.PreRelease, CreatedAt = T0 };
        var newer = new ApprovalGate { Type = EngineGates.Escalation, CreatedAt = T0.AddMinutes(1) };
        var v1 = new Artifact { Version = 1 };
        var v2 = new Artifact { Version = 2 };

        Assert.Equal([older, newer], EventOrder.Cancelled([newer, older]));
        Assert.Equal([v1, v2], EventOrder.Superseded([v2, v1]));
    }
}
