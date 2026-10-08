using System.Net;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;
using Npgsql;

namespace Aide.IntegrationTests.Lifecycle;

/// <summary>FR-11, FR-12, §4.1, §4.2: <c>POST /api/projects/:id/phase/advance</c>.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AdvancePhaseTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR11_Advance_without_an_approved_project_request_fails_and_nothing_changes()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var response = await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id);

        var body = await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        Assert.Contains("project request", body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The project request is not approved.", body.GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Refinement, ProjectStatus.Active, 1), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id)); // the creation record only
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Empty(await LifecycleSeed.GatesAsync(factory, id));
    }

    [Fact]
    public async Task FR11_Advance_writes_the_transition_the_PHASE_CHANGED_event_and_the_follow_up_job_together()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);

        var result = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id, "Request looks good"));

        Assert.Null(result.Gate);
        Assert.Equal((Phase.Specification, ProjectStatus.Active, 2), (result.State.Phase, result.State.Status, result.State.ProjectVersion));

        var transition = (await LifecycleSeed.TransitionsAsync(factory, id))[^1];
        Assert.Equal(
            ((Phase?)Phase.Refinement, Phase.Specification, fixture.Contributor.User!.Id.ToString(), "Request looks good", 2),
            (transition.FromPhase, transition.ToPhase, transition.Actor, transition.Reason, transition.ProjectVersion));

        var changed = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        var payload = LifecycleSeed.Payload(changed.Payload);
        Assert.Equal(("refinement", "specification", 2), (payload.GetProperty("from").GetString(), payload.GetProperty("to").GetString(), payload.GetProperty("projectVersion").GetInt32()));
        Assert.Equal(transition.CreatedAt, changed.CreatedAt);

        // FR-28: entering specification queues the spec generator in the same transaction (outbox).
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal((JobTypes.SpecGeneration, JobStatus.Queued, 0), (job.Type, job.Status, job.Attempts));
        Assert.Equal(id.ToString(), LifecycleSeed.Payload(job.Payload).GetProperty("projectId").GetString());
        Assert.Equal(transition.CreatedAt, job.CreatedAt);
    }

    [Fact]
    public async Task FR11_Concurrent_advances_give_one_success_and_one_CONFLICT()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);

        // Hold the project row so both requests read the same version, then both try to write it.
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM projects WHERE id = @id FOR UPDATE", connection, transaction))
            {
                lockRow.Parameters.AddWithValue("id", id);
                await lockRow.ExecuteScalarAsync(Ct);
            }

            var first = LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id);
            var second = LifecycleSeed.AdvanceAsync(fixture.Owner.Client, id);
            await WaitForBlockedAsync(factory.ConnectionString, 2);
            await transaction.CommitAsync(Ct);

            var responses = await Task.WhenAll(first, second);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
            await Problems.AssertAsync(loser, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        }

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Specification, 2), (project.CurrentPhase, project.ProjectVersion));
        Assert.Equal(2, (await LifecycleSeed.TransitionsAsync(factory, id)).Count);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR12_Advisory_mode_in_specification_with_an_approved_spec_creates_a_pending_spec_approval_gate()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        var spec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, projectVersionAtCreation: 2);

        var result = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var gate = Assert.IsType<Aide.Api.Features.Gates.GateDto>(result.Gate);
        Assert.Equal((GateType.SpecApproval, GateStatus.Pending, (Guid?)spec.Id, (ArtifactType?)ArtifactType.TechnicalSpecification, (int?)2),
            (gate.Type, gate.Status, gate.ArtifactId, gate.ArtifactType, gate.ArtifactVersion));
        Assert.Equal((Phase.Specification, 2), (result.State.Phase, result.State.ProjectVersion));
        Assert.Equal([gate.Id], result.State.PendingGates.Select(g => g.Id));

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateCreated));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR12_Autonomous_mode_applies_the_transition_directly()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);

        var result = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Owner.Client, id));

        Assert.Null(result.Gate);
        Assert.Equal((Phase.Planning, ProjectStatus.Active, 3), (result.State.Phase, result.State.Status, result.State.ProjectVersion));
        Assert.Empty(await LifecycleSeed.GatesAsync(factory, id));
        Assert.Equal([JobTypes.PlanGeneration], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR12_Advancing_again_while_the_gate_is_pending_is_GATE_PENDING()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Strict);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var again = await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id);

        await Problems.AssertAsync(again, HttpStatusCode.Conflict, ErrorCodes.GatePending);
        Assert.Single(await LifecycleSeed.GatesAsync(factory, id));
    }

    [Fact]
    public async Task FR12_Strict_mode_needs_a_plan_approval_gate_to_enter_execution_advisory_does_not()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedReadyForExecutionAsync(id, ApprovalMode.Strict);

        var strict = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        Assert.Equal(GateType.PlanApproval, strict.Gate?.Type);
        Assert.Equal(Phase.Planning, strict.State.Phase);

        using var other = await ProjectApi.FixtureAsync(factory);
        await SeedReadyForExecutionAsync(other.Project.Id, ApprovalMode.Advisory);
        var advisory = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(other.Contributor.Client, other.Project.Id));
        Assert.Null(advisory.Gate);
        Assert.Equal((Phase.Execution, ProjectStatus.Building, 4), (advisory.State.Phase, advisory.State.Status, advisory.State.ProjectVersion));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, other.Project.Id)); // execution starts with POST /execution/start
    }

    [Fact]
    public async Task FR12_Viewers_get_403_and_non_members_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.AddArtifactAsync(factory, fixture.Project.Id, ArtifactType.ProjectRequest);

        await Problems.AssertAsync(await LifecycleSeed.AdvanceAsync(fixture.Viewer.Client, fixture.Project.Id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await LifecycleSeed.AdvanceAsync(fixture.Outsider.Client, fixture.Project.Id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(Phase.Refinement, (await LifecycleSeed.ProjectAsync(factory, fixture.Project.Id)).CurrentPhase);
    }

    [Fact]
    public async Task FR12_Advancing_from_release_is_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.SetProjectAsync(factory, fixture.Project.Id, Phase.Release, 6, ApprovalMode.Advisory);

        await Problems.AssertAsync(await LifecycleSeed.AdvanceAsync(fixture.Owner.Client, fixture.Project.Id), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task NFR_OBS_Lifecycle_logs_carry_the_project_id_scope()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);

        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var entry = Assert.Single(factory.Logs.Entries, e =>
            e.Category == typeof(LifecycleService).FullName && e.Message.Contains(id.ToString(), StringComparison.Ordinal));
        Assert.Contains(entry.Scopes, kv => kv.Key == "ProjectId" && Equals(kv.Value, id));
    }

    /// <summary>Planning at version 3 with spec and plan approved, a 2-step plan and a prepared working copy.</summary>
    private async Task SeedReadyForExecutionAsync(Guid id, ApprovalMode mode)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, mode);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
    }

    /// <summary>Waits until <paramref name="count"/> backends of this database are waiting for a lock.</summary>
    internal static async Task WaitForBlockedAsync(string connectionString, int count)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
                connection);
            var waiting = (long)(await command.ExecuteScalarAsync(Ct))!;
            if (waiting >= count)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Only {waiting} of {count} requests reached the lock.");
            await Task.Delay(50, Ct);
        }
    }
}
