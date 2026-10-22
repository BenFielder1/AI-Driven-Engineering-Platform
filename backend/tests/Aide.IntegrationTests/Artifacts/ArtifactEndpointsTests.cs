using System.Net;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Artifacts;

/// <summary>FR-16–FR-19, FR-21: artifact versions, drafts, approval, rejection, cascade and diff over HTTP.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ArtifactEndpointsTests(AideApiFactory factory)
{
    private const string Spec = "technical_specification";
    private const string Plan = "implementation_plan";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-16 / FR-18: versions and drafts ----------

    [Fact]
    public async Task FR18_A_contributor_edit_of_locked_spec_v3_creates_v4_draft_with_parent_v3()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        for (var v = 1; v <= 3; v++)
        {
            await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: v, projectVersionAtCreation: 2);
        }

        const string content = "# Spec v4\r\nEdited in the browser.\r\n";
        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, content), HttpStatusCode.Created);

        Assert.Equal((4, (int?)3, ArtifactStatus.Draft, ArtifactIntegrity.Ok), (draft.Version, draft.ParentVersion, draft.Status, draft.Integrity));
        Assert.Equal((content, ContentHash.Compute(content), 3), (draft.Content, draft.ContentHash, draft.CheckpointAtCreation));
        Assert.Equal(fixture.Contributor.User!.Id, draft.Author.User!.Id);
        Assert.Equal(fixture.Contributor.User.Name, draft.Author.User.Name);
        Assert.Null(draft.Author.AgentRunId);

        var row = await ArtifactApi.RowAsync(factory, draft.Id);
        Assert.Equal(fixture.Contributor.User.Id, row.CreatedByUserId);
        var created = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactCreated));
        Assert.Equal(4, LifecycleSeed.Payload(created.Payload).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task FR18_An_edit_based_on_the_latest_version_is_saved()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 1, projectVersionAtCreation: 2);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, projectVersionAtCreation: 2);

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Spec v3", baseVersion: 2), HttpStatusCode.Created);

        Assert.Equal((3, (int?)2), (draft.Version, draft.ParentVersion));
    }

    [Fact]
    public async Task FR18_A_stale_edit_is_CONFLICT_with_the_latest_version_and_saves_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 1, projectVersionAtCreation: 2);
        // Both editors opened v1; the first save wins.
        await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, Spec, "# First edit", baseVersion: 1), HttpStatusCode.Created);

        var stale = await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Second edit", baseVersion: 1);

        var problem = await Problems.AssertAsync(stale, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal("latestVersion: 2", Assert.Single(problem.GetProperty("details").EnumerateArray()).GetString());
        var versions = await ProjectApi.WithDbAsync(factory, db => db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == id && a.Type == ArtifactType.TechnicalSpecification)
            .OrderBy(a => a.Version)
            .Select(a => new { a.Version, a.Status, a.Content })
            .ToListAsync(Ct));
        Assert.Equal([(1, ArtifactStatus.Locked), (2, ArtifactStatus.Draft)], versions.Select(v => (v.Version, v.Status)));
        Assert.Equal("# First edit", versions[1].Content);
    }

    [Fact]
    public async Task FR18_Editing_a_locked_version_while_a_newer_draft_exists_is_CONFLICT()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 1, projectVersionAtCreation: 2);
        var agentDraft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, version: 2, projectVersionAtCreation: 2);

        var stale = await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Edit of v1", baseVersion: 1);

        await Problems.AssertAsync(stale, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal(ArtifactStatus.Draft, (await ArtifactApi.RowAsync(factory, agentDraft.Id)).Status);
    }

    [Fact]
    public async Task FR18_A_draft_without_a_base_version_is_saved_as_before()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 1, projectVersionAtCreation: 2);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, version: 2, projectVersionAtCreation: 2);

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# CLI upload"), HttpStatusCode.Created);

        Assert.Equal(3, draft.Version);
    }

    [Fact]
    public async Task FR18_A_base_version_below_1_is_VALIDATION()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, "project_request", "# Idea", baseVersion: 0);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR16_A_new_draft_supersedes_the_open_draft_of_its_type()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        var first = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, Spec, "# v1"), HttpStatusCode.Created);

        var second = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, Spec, "# v2"), HttpStatusCode.Created);

        Assert.Equal((2, (int?)1, (int?)null), (second.Version, second.ParentVersion, first.ParentVersion));
        Assert.Equal(ArtifactStatus.Superseded, (await ArtifactApi.RowAsync(factory, first.Id)).Status);
        var superseded = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactSuperseded));
        Assert.Equal(first.Id.ToString(), LifecycleSeed.Payload(superseded.Payload).GetProperty("artifactId").GetString());
    }

    [Fact]
    public async Task FR18_A_draft_of_a_type_not_yet_produced_is_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, Plan, "# Plan");

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, fixture.Project.Id, EventTypes.ArtifactCreated));
    }

    [Fact]
    public async Task FR18_Viewers_can_not_create_drafts_and_outsiders_do_not_see_the_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await ArtifactApi.CreateDraftAsync(fixture.Viewer.Client, id, "project_request", "# Idea"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ArtifactApi.CreateDraftAsync(fixture.Outsider.Client, id, "project_request", "# Idea"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData("not_a_type", "# x")]
    [InlineData("project_request", "   ")]
    [InlineData("project_request", "")]
    public async Task FR18_Unknown_types_and_empty_content_are_VALIDATION(string type, string content)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, type, content);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR16_Reads_return_latest_per_type_history_newest_first_and_single_versions()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, version: 1);
        var v2 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, version: 2, createdByUserId: fixture.Owner.User!.Id);

        var list = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/"));
        Assert.Equal(
            [(ArtifactType.ProjectRequest, 1, ArtifactStatus.Locked), (ArtifactType.TechnicalSpecification, 2, ArtifactStatus.Draft)],
            list.Select(a => (a.Type, a.Version, a.Status)));
        Assert.All(list, a => Assert.Equal(ArtifactIntegrity.Ok, a.Integrity));

        var history = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/history"));
        Assert.Equal([2, 1], history.Select(a => a.Version));
        Assert.Equal(fixture.Owner.User.Id, history[0].Author.User!.Id);
        Assert.Null(history[1].Author.User);

        var latest = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/latest"));
        Assert.Equal((v2.Id, 2, (int?)1), (latest.Id, latest.Version, latest.ParentVersion));

        var v1 = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/versions/1"));
        Assert.Equal(("# TechnicalSpecification v1", ArtifactStatus.Locked), (v1.Content, v1.Status));
    }

    [Fact]
    public async Task FR16_Missing_versions_and_types_without_versions_are_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await ArtifactApi.GetAsync(fixture.Owner.Client, id, $"/{Spec}/latest"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await ArtifactApi.GetAsync(fixture.Owner.Client, id, $"/{Spec}/versions/7"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await ArtifactApi.GetAsync(fixture.Outsider.Client, id, "/"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, $"/{Spec}/history")));
    }

    // ---------- FR-17: approve and reject ----------

    [Fact]
    public async Task FR17_Approving_a_draft_locks_it_with_approver_and_time_and_approving_again_is_a_no_op()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft);

        var approved = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve", "LGTM"));

        Assert.Equal(ArtifactStatus.Locked, approved.Status);
        Assert.Equal((fixture.Owner.User!.Id, false), (approved.ApprovedBy!.Id!.Value, approved.ApprovedBy.IsSystem));
        Assert.NotNull(approved.ApprovedAt);
        var again = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        Assert.Equal((ArtifactStatus.Locked, approved.ApprovedAt), (again.Status, again.ApprovedAt));

        var events = await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactApproved);
        Assert.Equal("LGTM", LifecycleSeed.Payload(Assert.Single(events).Payload).GetProperty("comment").GetString());
    }

    [Fact]
    public async Task FR17_Only_owners_approve_or_reject()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft);

        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Contributor.Client, id, draft.Id, "approve"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Viewer.Client, id, draft.Id, "reject"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Outsider.Client, id, draft.Id, "approve"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(ArtifactStatus.Draft, (await ArtifactApi.RowAsync(factory, draft.Id)).Status);
    }

    [Fact]
    public async Task FR17_Unknown_artifacts_and_artifacts_of_other_projects_are_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var other = await ProjectApi.FixtureAsync(factory);
        var foreign = await LifecycleSeed.AddArtifactAsync(factory, other.Project.Id, ArtifactType.ProjectRequest, ArtifactStatus.Draft);

        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, fixture.Project.Id, Guid.NewGuid(), "approve"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, fixture.Project.Id, foreign.Id, "reject"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(ArtifactStatus.Rejected, "approve")]
    [InlineData(ArtifactStatus.Superseded, "approve")]
    [InlineData(ArtifactStatus.Invalidated, "reject")]
    public async Task FR17_Deciding_a_non_draft_is_ARTIFACT_NOT_DRAFT(ArtifactStatus status, string action)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var artifact = await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, ArtifactType.ProjectRequest, status);

        var response = await ArtifactApi.DecideAsync(fixture.Owner.Client, fixture.Project.Id, artifact.Id, action);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.ArtifactNotDraft);
    }

    [Fact]
    public async Task FR17_Changing_a_locked_artifact_is_ARTIFACT_LOCKED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var locked = await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, ArtifactType.ProjectRequest);

        var response = await ArtifactApi.DecideAsync(fixture.Owner.Client, fixture.Project.Id, locked.Id, "reject", "No");

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.ArtifactLocked);
        var row = await ArtifactApi.RowAsync(factory, locked.Id);
        Assert.Equal((ArtifactStatus.Locked, (string?)null), (row.Status, row.RejectionComment));
    }

    [Fact]
    public async Task FR17_Rejecting_an_agent_draft_records_the_comment_and_requeues_its_producer_with_the_feedback()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft);

        var rejected = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "reject", "  Add auth  "));

        Assert.Equal((ArtifactStatus.Rejected, "Add auth"), (rejected.Status, rejected.RejectionComment));
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal((JobTypes.SpecGeneration, JobStatus.Queued), (job.Type, job.Status));
        var payload = LifecycleSeed.Payload(job.Payload);
        Assert.Equal(("Add auth", "artifact_rejected", draft.Id.ToString(), 2),
            (payload.GetProperty("feedback").GetString(), payload.GetProperty("trigger").GetString(), payload.GetProperty("artifactId").GetString(), payload.GetProperty("projectVersion").GetInt32()));
        var rejectedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactRejected)).Payload);
        Assert.Equal((job.Id.ToString(), "spec_generator"), (rejectedEvent.GetProperty("jobId").GetString(), rejectedEvent.GetProperty("rerunAgent").GetString()));
    }

    [Fact]
    public async Task FR17_Rejecting_a_human_edited_draft_records_the_comment_and_reruns_no_agent()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Hand-written"), HttpStatusCode.Created);

        var rejected = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "reject", "Too vague"));

        Assert.Equal((ArtifactStatus.Rejected, "Too vague"), (rejected.Status, rejected.RejectionComment));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    // ---------- FR-19: cascade, and artifact approval vs. gates ----------

    [Fact]
    public async Task FR19_Approving_spec_v4_in_review_invalidates_the_plan_resets_its_steps_and_falls_back_to_planning()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        var spec3 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 3, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        var optimisation = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, ArtifactStatus.Draft, projectVersionAtCreation: 5);
        var steps = await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 3, StepStatus.Completed);
        var escalation = await LifecycleSeed.AddGateAsync(factory, id, GateType.Escalation);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.Jobs.Add(new Aide.Domain.Entities.Job { Type = JobTypes.Execution, Payload = "{}", ProjectId = id, Status = JobStatus.Queued, RunAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        var spec4 = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Spec v4"), HttpStatusCode.Created);
        var approved = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, spec4.Id, "approve"));

        Assert.Equal((4, (int?)3, ArtifactStatus.Locked), (approved.Version, approved.ParentVersion, approved.Status));
        Assert.Equal(ArtifactStatus.Locked, (await ArtifactApi.RowAsync(factory, spec3.Id)).Status);
        Assert.Equal(ArtifactStatus.Invalidated, (await ArtifactApi.RowAsync(factory, plan.Id)).Status);
        Assert.Equal(ArtifactStatus.Invalidated, (await ArtifactApi.RowAsync(factory, optimisation.Id)).Status);
        var stepRows = await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id).ToListAsync(Ct));
        Assert.Equal(steps.Count, stepRows.Count);
        Assert.All(stepRows, s => Assert.Equal(StepStatus.Invalidated, s.Status));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, ProjectStatus.Active, 5), (project.CurrentPhase, project.Status, project.Checkpoint));
        var transition = (await LifecycleSeed.TransitionsAsync(factory, id))[^1];
        Assert.Equal((Phase.Review, Phase.Planning, 5), (transition.FromPhase, transition.ToPhase, transition.Checkpoint));
        var changed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged)).Payload);
        Assert.Equal("invalidation_fallback", changed.GetProperty("kind").GetString());

        var invalidated = await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactInvalidated);
        Assert.Equal(
            new[] { plan.Id, optimisation.Id }.Order(),
            invalidated.Select(e => Guid.Parse(LifecycleSeed.Payload(e.Payload).GetProperty("artifactId").GetString()!)).Order());
        Assert.All(invalidated, e => Assert.Equal("cascade", LifecycleSeed.Payload(e.Payload).GetProperty("cause").GetString()));

        var gates = await LifecycleSeed.GatesAsync(factory, id);
        Assert.Equal(GateStatus.Cancelled, gates.Single(g => g.Id == escalation.Id).Status);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateCancelled));
        Assert.All(await LifecycleSeed.JobsAsync(factory, id), j => Assert.Equal(JobStatus.Cancelled, j.Status));
    }

    [Fact]
    public async Task S4_8_A_cascade_fallback_out_of_review_clears_the_review_cycle_and_the_extra_cycles()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 2);
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ExtraReviewCycles, 1), Ct));
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 3, projectVersionAtCreation: 2);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);

        var spec4 = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Spec v4"), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, spec4.Id, "approve"));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, 0, 0), (project.CurrentPhase, project.ReviewCycle, project.ExtraReviewCycles));
    }

    [Fact]
    public async Task FR19_Approving_a_plan_in_planning_invalidates_only_the_optimisation_plan_and_older_plan_steps_without_fallback()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var spec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var oldPlan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        var newPlan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, version: 2, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, oldPlan.Id, 1);
        var newSteps = await LifecycleSeed.AddStepsAsync(factory, id, newPlan.Id, 2);

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, newPlan.Id, "approve"));

        Assert.Equal(ArtifactStatus.Locked, (await ArtifactApi.RowAsync(factory, spec.Id)).Status);
        Assert.Equal(ArtifactStatus.Locked, (await ArtifactApi.RowAsync(factory, oldPlan.Id)).Status);
        var stepRows = await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id).ToListAsync(Ct));
        Assert.All(stepRows, s => Assert.Equal(s.PlanArtifactId == oldPlan.Id ? StepStatus.Invalidated : StepStatus.Pending, s.Status));
        Assert.Equal(newSteps.Count, stepRows.Count(s => s.Status == StepStatus.Pending));
        Assert.Equal(Phase.Planning, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
    }

    [Fact]
    public async Task FR19_Approving_a_newer_spec_cancels_the_pending_spec_gate_for_the_older_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        var v1 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        Assert.Equal(v1.Id, gate.ArtifactId);

        var v2 = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Spec, "# Spec v2"), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, v2.Id, "approve"));

        var cancelled = Assert.Single(await LifecycleSeed.GatesAsync(factory, id));
        Assert.Equal(GateStatus.Cancelled, cancelled.Status);
        var payload = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateCancelled)).Payload);
        Assert.Equal(("artifact_approved", v2.Id.ToString()), (payload.GetProperty("cause").GetString(), payload.GetProperty("artifactId").GetString()));
        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);

        // Advancing again gates the new version.
        var regated = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        Assert.Equal(v2.Id, regated.ArtifactId);
    }

    [Fact]
    public async Task FR19_Approving_the_optimisation_plan_during_review_starts_the_review_cycle()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.ReviewRequired);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
        var optimisation = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, ArtifactStatus.Draft, projectVersionAtCreation: 5);
        await LifecycleSeed.AddStepsAsync(factory, id, optimisation.Id, 2);

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, optimisation.Id, "approve"));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Execution, ProjectStatus.Building, 5), (project.CurrentPhase, project.Status, project.Checkpoint));
        var changed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged)).Payload);
        Assert.Equal("review_cycle", changed.GetProperty("kind").GetString());
    }

    // ---------- FR-21: diff ----------

    [Fact]
    public async Task FR21_Diff_of_spec_v1_and_v3_shows_the_changed_lines()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Invalidated, version: 1, content: "# Spec\nGET /todos\n");
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Superseded, version: 2, content: "# Spec\n");
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 3, content: "# Spec\nGET /todos\nPOST /todos\n");

        var diff = await ProjectApi.ReadAsync<ArtifactDiffDto>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/diff?from=1&to=3"));

        Assert.Equal((1, 3), (diff.From, diff.To));
        Assert.Equal(
            [(DiffLineKind.Context, 1, 1, "# Spec"), (DiffLineKind.Context, 2, 2, "GET /todos"), (DiffLineKind.Added, null, 3, "POST /todos")],
            diff.Lines.Select(l => (l.Kind, l.OldLine, l.NewLine, l.Text)));
        Assert.Equal(
            "--- technical_specification v1\n+++ technical_specification v3\n@@ -1,2 +1,3 @@\n # Spec\n GET /todos\n+POST /todos\n",
            diff.UnifiedDiff);
    }

    [Theory]
    [InlineData("?from=1&to=9", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("?from=9&to=1", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("?from=0&to=1", HttpStatusCode.BadRequest, "VALIDATION")]
    [InlineData("?to=1", HttpStatusCode.BadRequest, "VALIDATION")]
    public async Task FR21_Diff_of_a_missing_version_is_NOT_FOUND_and_bad_input_is_VALIDATION(string query, HttpStatusCode status, string code)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, ArtifactType.TechnicalSpecification);

        var response = await ArtifactApi.GetAsync(fixture.Owner.Client, fixture.Project.Id, $"/{Spec}/diff{query}");

        await Problems.AssertAsync(response, status, code);
    }

    [Fact]
    public async Task FR21_A_version_diffed_with_itself_has_no_changes()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, ArtifactType.ProjectRequest);

        var diff = await ProjectApi.ReadAsync<ArtifactDiffDto>(await ArtifactApi.GetAsync(fixture.Owner.Client, fixture.Project.Id, "/project_request/diff?from=1&to=1"));

        Assert.Equal(string.Empty, diff.UnifiedDiff);
        var skipped = Assert.Single(diff.Lines);
        Assert.Equal((DiffLineKind.Skipped, (int?)1, (int?)1, string.Empty, (int?)1), (skipped.Kind, skipped.OldLine, skipped.NewLine, skipped.Text, skipped.Skipped));
    }

    [Fact]
    public async Task FR21_Long_unchanged_runs_are_collapsed_into_skipped_entries()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var v1 = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n";
        var v2 = v1.Replace("line 20\n", "line twenty\n", StringComparison.Ordinal);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Superseded, version: 1, content: v1);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, version: 2, content: v2);

        var diff = await ProjectApi.ReadAsync<ArtifactDiffDto>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/project_request/diff?from=1&to=2"));

        Assert.Equal(10, diff.Lines.Count);
        Assert.Equal((DiffLineKind.Skipped, (int?)1, (int?)16), (diff.Lines[0].Kind, diff.Lines[0].OldLine, diff.Lines[0].Skipped));
        Assert.Equal((DiffLineKind.Skipped, (int?)24, (int?)17), (diff.Lines[^1].Kind, diff.Lines[^1].OldLine, diff.Lines[^1].Skipped));
        Assert.All(diff.Lines.Where(l => l.Kind != DiffLineKind.Skipped), l => Assert.Null(l.Skipped));
        Assert.StartsWith("--- project_request v1\n+++ project_request v2\n@@ -17,7 +17,7 @@\n", diff.UnifiedDiff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR21_Versions_too_large_to_diff_are_VALIDATION()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var huge = string.Concat(Enumerable.Repeat("x\n", (LineDiff.MaxTotalLines / 2) + 1));
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Superseded, version: 1, content: huge);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, version: 2, content: huge + "y\n");

        var response = await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/project_request/diff?from=1&to=2");

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR17_Agent_kind_for_a_rejected_plan_is_the_plan_generator()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft);

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "reject", "split step 4"));

        Assert.Equal(JobTypes.ForAgent(AgentKind.PlanGenerator), Assert.Single(await LifecycleSeed.JobsAsync(factory, id)).Type);
    }
}
