using System.Net;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Projects;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Gates;

/// <summary>FR-13, FR-14, §4.2, §4.8: listing and deciding gates.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GateDecisionTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR13_Approving_a_spec_approval_gate_applies_the_gated_transition()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await PendingSpecGateAsync(fixture);

        var decided = await ProjectApi.ReadAsync<GateDto>(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { comment = "Ship it" }));

        Assert.Equal((GateStatus.Approved, "Ship it", (EscalationDecision?)null), (decided.Status, decided.Comment, decided.Decision));
        Assert.Equal((fixture.Owner.User!.Id, fixture.Owner.User.Name, false), (decided.DecidedBy!.Id!.Value, decided.DecidedBy.Name, decided.DecidedBy.IsSystem));
        Assert.NotNull(decided.DecidedAt);

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, ProjectStatus.Active, 3), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateApproved));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        Assert.Equal([JobTypes.PlanGeneration], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR12_The_advance_reason_is_kept_with_the_gate_and_recorded_on_the_transition_when_it_is_approved()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await PendingSpecGateAsync(fixture, "  Spec reviewed with the team  ");

        var created = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateCreated));
        Assert.Equal("Spec reviewed with the team", LifecycleSeed.Payload(created.Payload).GetProperty("reason").GetString());

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { comment = "Ship it" }));

        var transition = (await LifecycleSeed.TransitionsAsync(factory, id))[^1];
        Assert.Equal((Phase.Planning, "Spec reviewed with the team (spec_approval gate approved: Ship it)"), (transition.ToPhase, transition.Reason));
        var changed = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        Assert.Equal(transition.Reason, LifecycleSeed.Payload(changed.Payload).GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("Spec reviewed", null, "Spec reviewed (spec_approval gate approved)")]
    [InlineData(null, "Ship it", "spec_approval gate approved: Ship it")]
    [InlineData(null, null, "spec_approval gate approved")]
    public async Task FR12_The_gated_transition_reason_combines_the_advance_reason_and_the_approval(string? reason, string? comment, string expected)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await PendingSpecGateAsync(fixture, reason);

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { comment }));

        Assert.Equal(expected, (await LifecycleSeed.TransitionsAsync(factory, id))[^1].Reason);
    }

    [Fact]
    public async Task FR13_Rejecting_a_plan_approval_gate_requeues_the_plan_generator_with_the_feedback_and_keeps_the_phase()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedReadyForExecutionAsync(id, ApprovalMode.Strict);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        Assert.Equal(GateType.PlanApproval, gate.Type);

        var decided = await ProjectApi.ReadAsync<GateDto>(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "reject", new { comment = "split step 4" }));

        Assert.Equal((GateStatus.Rejected, "split step 4"), (decided.Status, decided.Comment));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, 3), (project.CurrentPhase, project.ProjectVersion));

        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal((JobTypes.PlanGeneration, JobStatus.Queued), (job.Type, job.Status));
        var payload = LifecycleSeed.Payload(job.Payload);
        Assert.Equal("split step 4", payload.GetProperty("feedback").GetString());
        Assert.Equal(gate.Id.ToString(), payload.GetProperty("gateId").GetString());
        Assert.Equal("gate_rejected", payload.GetProperty("trigger").GetString());

        var rejected = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateRejected));
        Assert.Equal("plan_generator", LifecycleSeed.Payload(rejected.Payload).GetProperty("rerunAgent").GetString());
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
    }

    [Fact]
    public async Task FR13_Rejecting_a_spec_approval_gate_reruns_the_spec_generator_D16()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingSpecGateAsync(fixture);

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "reject", new { comment = "Add auth" }));

        Assert.Equal([JobTypes.SpecGeneration], (await LifecycleSeed.JobsAsync(factory, fixture.Project.Id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR13_Approving_a_pre_release_gate_enters_release_and_queues_the_release_job()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.ReleaseEligible);
        var gate = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Equal([JobTypes.Release], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR13_Approval_rechecks_the_preconditions_and_leaves_the_gate_pending_when_they_fail()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await PendingSpecGateAsync(fixture);
        await ProjectApi.WithDbAsync(factory, db => db.Artifacts.Where(a => a.ProjectId == id && a.Type == ArtifactType.TechnicalSpecification)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ArtifactStatus.Invalidated), Ct));

        var response = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve");

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        Assert.Equal(GateStatus.Pending, Assert.Single(await LifecycleSeed.GatesAsync(factory, id)).Status);
        Assert.Equal(Phase.Specification, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR13_Deciding_a_decided_gate_is_GATE_NOT_PENDING()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingSpecGateAsync(fixture);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "approve"));

        await Problems.AssertAsync(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "approve"), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
        await Problems.AssertAsync(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "reject"), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
    }

    [Fact]
    public async Task FR13_Only_owners_decide_gates()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingSpecGateAsync(fixture);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Contributor.Client, id, gate.Id, "approve"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Viewer.Client, id, gate.Id, "reject"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Outsider.Client, id, gate.Id, "approve"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, Guid.NewGuid(), "approve"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(GateStatus.Pending, Assert.Single(await LifecycleSeed.GatesAsync(factory, id)).Status);
    }

    [Fact]
    public async Task FR13_Gates_are_listed_newest_first_and_filtered_by_status()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var old = await LifecycleSeed.AddGateAsync(factory, id, GateType.SpecApproval, createdAt: DateTimeOffset.UtcNow.AddHours(-73));
        await ProjectApi.WithDbAsync(factory, db => db.ApprovalGates.Where(g => g.Id == old.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GateStatus.Rejected), Ct));
        var overdue = await LifecycleSeed.AddGateAsync(factory, id, GateType.PlanApproval, createdAt: DateTimeOffset.UtcNow.AddHours(-73));
        var fresh = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);

        var all = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates", UriKind.Relative), Ct));
        var pending = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates?status=pending", UriKind.Relative), Ct));

        Assert.Equal([fresh.Id, overdue.Id, old.Id], all.Select(g => g.Id));
        Assert.Equal([fresh.Id, overdue.Id], pending.Select(g => g.Id));
        Assert.Equal([false, true], pending.Select(g => g.Overdue));
        Assert.False(all[2].Overdue); // decided gates are never overdue
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates?status=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/gates", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR14_Continue_allows_one_more_review_cycle()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);

        var decided = await ProjectApi.ReadAsync<GateDto>(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue", comment = "One more try" }));

        Assert.Equal((GateStatus.Approved, (EscalationDecision?)EscalationDecision.Continue, "One more try", (EscalationReason?)EscalationReason.MaxCycles),
            (decided.Status, decided.Decision, decided.Comment, decided.EscalationReason));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((1, Phase.Review), (project.ExtraReviewCycles, project.CurrentPhase)); // no optimisation plan approved yet
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(4, state.Review.MaxCycles);
    }

    [Fact]
    public async Task FR14_Continue_with_an_approved_optimisation_plan_re_enters_execution()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);
        var optimisation = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, projectVersionAtCreation: 5);
        await LifecycleSeed.AddStepsAsync(factory, id, optimisation.Id, 2);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);

        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "continue" }));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Execution, ProjectStatus.Building, 5, 1), (project.CurrentPhase, project.Status, project.ProjectVersion, project.ExtraReviewCycles));
    }

    [Fact]
    public async Task FR14_Accept_records_the_comment_and_makes_the_project_release_eligible()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);

        var decided = await ProjectApi.ReadAsync<GateDto>(
            await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "accept", comment = "Good enough for v1" }));

        Assert.Equal(((EscalationDecision?)EscalationDecision.Accept, "Good enough for v1"), (decided.Decision, decided.Comment));
        var preRelease = Assert.Single(await LifecycleSeed.GatesAsync(factory, id), g => g.Status == GateStatus.Pending);
        Assert.Equal(GateType.PreRelease, preRelease.Type);

        // The accepted escalation satisfies the release entry condition (§4.1).
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, preRelease.Id, "approve"));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6), (project.CurrentPhase, project.Status, project.ProjectVersion));
    }

    [Fact]
    public async Task FR14_Rollback_restores_the_chosen_version_in_one_transaction()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Refinement, Phase.Specification, 2, start.AddMinutes(10));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Specification, Phase.Planning, 3, start.AddMinutes(20));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Planning, Phase.Execution, 4, start.AddMinutes(30));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Execution, Phase.Review, 5, start.AddMinutes(40));
        var spec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        var planSteps = await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2, StepStatus.Completed);
        var optimisation = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, projectVersionAtCreation: 5);
        var optimisationSteps = await LifecycleSeed.AddStepsAsync(factory, id, optimisation.Id, 2);
        var gate = await EscalatedReviewAsync(id);
        var otherGate = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.Jobs.Add(new Aide.Domain.Entities.Job { Type = JobTypes.Review, ProjectId = id, RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        var decided = await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(
            fixture.Owner.Client, id, gate.Id, "approve", new { decision = "rollback", targetVersion = 3, comment = "Plan was wrong" }));

        Assert.Equal(((EscalationDecision?)EscalationDecision.Rollback, (int?)3), (decided.Decision, decided.TargetVersion));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, ProjectStatus.RolledBack, 3, 0), (project.CurrentPhase, project.Status, project.ProjectVersion, project.ReviewCycle));
        Assert.NotNull(project.LastRollbackAt);

        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var statuses = await db.Artifacts.Where(a => a.ProjectId == id).ToDictionaryAsync(a => a.Id, a => a.Status, Ct);
            Assert.Equal(ArtifactStatus.Locked, statuses[spec.Id]);
            Assert.Equal(ArtifactStatus.Locked, statuses[plan.Id]);
            Assert.Equal(ArtifactStatus.Invalidated, statuses[optimisation.Id]);

            var steps = await db.ExecutionSteps.Where(s => s.ProjectId == id).ToDictionaryAsync(s => s.Id, s => s.Status, Ct);
            Assert.All(planSteps, s => Assert.Equal(StepStatus.Completed, steps[s.Id]));
            Assert.All(optimisationSteps, s => Assert.Equal(StepStatus.Invalidated, steps[s.Id]));

            var record = await db.RollbackEvents.SingleAsync(r => r.ProjectId == id, Ct);
            Assert.Equal((5, 3, "Plan was wrong"), (record.FromVersion, record.TargetVersion, record.Reason));
            Assert.Equal([optimisation.Id], record.InvalidatedArtifactIds);
            Assert.Equal(optimisationSteps.Select(s => s.Id).Order(), record.ResetStepIds.Order());

            Assert.Equal(GateStatus.Cancelled, (await db.ApprovalGates.SingleAsync(g => g.Id == otherGate.Id, Ct)).Status);
            Assert.Equal(GateStatus.Approved, (await db.ApprovalGates.SingleAsync(g => g.Id == gate.Id, Ct)).Status);
            Assert.All(await db.Jobs.Where(j => j.ProjectId == id).ToListAsync(Ct), j => Assert.Equal(JobStatus.Cancelled, j.Status));
        });
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.RollbackCompleted));
        Assert.Equal(Phase.Planning, (await LifecycleSeed.TransitionsAsync(factory, id))[^1].ToPhase);
    }

    [Fact]
    public async Task FR14_A_rollback_to_a_version_that_is_not_earlier_is_INVALID_TARGET_VERSION_and_changes_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);

        var response = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "rollback", targetVersion = 5 });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidTargetVersion);
        Assert.Equal(GateStatus.Pending, (await LifecycleSeed.GatesAsync(factory, id)).Single(g => g.Id == gate.Id).Status);
        Assert.Equal((Phase.Review, 5), ((await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(factory, id)).ProjectVersion));
    }

    [Fact]
    public async Task FR14_An_escalation_rollback_within_a_minute_of_the_last_is_RATE_LIMITED_with_Retry_After()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastRollbackAt, DateTimeOffset.UtcNow.AddSeconds(-30)), Ct));

        var response = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve", new { decision = "rollback", targetVersion = 1 });

        await Problems.AssertAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        var retryAfter = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 31);
        Assert.Equal(GateStatus.Pending, Assert.Single(await LifecycleSeed.GatesAsync(factory, id)).Status);
    }

    [Theory]
    [InlineData("approve", null, null)]
    [InlineData("approve", "rollback", null)]
    [InlineData("approve", "continue", 2)]
    [InlineData("reject", null, null)]
    public async Task FR14_Invalid_escalation_decisions_are_INVALID_DECISION(string action, string? decision, int? targetVersion)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var gate = await EscalatedReviewAsync(id);

        var response = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, action, new { decision, targetVersion });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidDecision);
        Assert.Equal(GateStatus.Pending, Assert.Single(await LifecycleSeed.GatesAsync(factory, id)).Status);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task FR14_A_decision_on_a_non_escalation_gate_is_INVALID_DECISION(string action)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingSpecGateAsync(fixture);

        var response = await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, action, new { decision = "accept" });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidDecision);
    }

    /// <summary>Specification at version 2 (advisory) with an approved spec, and the pending spec_approval gate from advancing.</summary>
    private async Task<GateDto> PendingSpecGateAsync(ProjectFixture fixture, string? reason = null)
    {
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var result = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id, reason));
        return result.Gate!;
    }

    /// <summary>Review at version 5, cycle 3 of 3, an escalating (max cycles) report and its pending escalation gate.</summary>
    private async Task<Aide.Domain.Entities.ApprovalGate> EscalatedReviewAsync(Guid id)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 3);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.EscalationRequired, EscalationReason.MaxCycles, DateTimeOffset.UtcNow.AddMinutes(-10));
        return await LifecycleSeed.AddGateAsync(factory, id, GateType.Escalation, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private async Task SeedReadyForExecutionAsync(Guid id, ApprovalMode mode)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, mode);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
    }
}
