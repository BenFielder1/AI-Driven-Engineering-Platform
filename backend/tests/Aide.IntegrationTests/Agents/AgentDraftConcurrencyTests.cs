using Aide.Api.Features.Lifecycle;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Agents;

/// <summary>
/// FR-26, FR-29, §4.9: an agent draft is saved under the project's <c>xmin</c>. A project change committed between the
/// phase check and the save (a rollback or fallback, or any concurrent write) fails the save transiently; the retry
/// re-uses the succeeded run (no second provider call, tokens counted once) and re-checks the phase.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AgentDraftConcurrencyTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Todo API\n\n## Summary\nA todo list API.";

    [Fact]
    public async Task FR26_A_draft_save_collision_retries_reusing_the_succeeded_run_with_tokens_counted_once()
    {
        var responder = new CountingResponder(new FakeSpecGeneratorResponder());
        var rival = new DraftSaveRival(factory.ConnectionString, "UPDATE projects SET updated_at = now() WHERE id = @id");
        await using var app = WithRival(AgentJobs.WithResponders(factory, responder), rival);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        rival.ProjectId = id;
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var first = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.True(rival.Fired);
        Assert.Equal(JobStatus.Dead, first.Status); // the test harness records a transient failure as dead; the worker retries
        Assert.StartsWith("CONFLICT: ", first.LastError, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification));

        await AgentJobs.RunAsync(app, first);

        Assert.Equal(1, responder.Calls);
        var run = Assert.Single(await AgentJobs.RunsAsync(app, id));
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        var spec = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification));
        Assert.Equal(((Guid?)run.Id, ArtifactStatus.Draft), (spec.AgentRunId, spec.Status));
        Assert.Equal((long)run.InputTokens + run.OutputTokens, (await LifecycleSeed.ProjectAsync(app, id)).TokensUsed);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, Aide.Domain.Common.EventTypes.ArtifactCreated));
    }

    [Fact]
    public async Task FR29_A_redelivered_plan_job_reuses_its_corrective_run_and_draft()
    {
        var responder = new ScriptedResponder(
            AgentKind.PlanGenerator,
            AgentJobs.PlanReply([], [2]),
            AgentJobs.PlanReply([], [1]));
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var tokens = (await LifecycleSeed.ProjectAsync(app, id)).TokensUsed;

        await AgentJobs.RunAsync(app, job);

        Assert.Equal(2, responder.Requests.Count);
        var runs = await AgentJobs.RunsAsync(app, id);
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Succeeded], runs.Select(r => r.Status));
        var plan = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));
        Assert.Equal((Guid?)AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey), plan.AgentRunId);
        Assert.Equal(tokens, (await LifecycleSeed.ProjectAsync(app, id)).TokensUsed);
        Assert.Equal(runs.Sum(r => (long)r.InputTokens + r.OutputTokens), tokens);
    }

    [Fact]
    public async Task FR29_A_phase_change_committed_while_the_plan_draft_is_saved_leaves_no_stale_draft()
    {
        var responder = new CountingResponder(new FakePlanGeneratorResponder());
        var rival = new DraftSaveRival(factory.ConnectionString, "UPDATE projects SET current_phase = 'specification' WHERE id = @id");
        await using var app = WithRival(AgentJobs.WithResponders(factory, responder), rival);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        rival.ProjectId = id;
        await PlanningReadyAsync(app, id);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var first = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.True(rival.Fired);
        Assert.StartsWith("CONFLICT: ", first.LastError, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));

        // The retry finds the project back in specification and drafts nothing.
        await AgentJobs.RunAsync(app, first);

        Assert.Equal(1, responder.Calls);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));
        Assert.Equal(JobStatus.Succeeded, (await LifecycleSeed.JobsAsync(app, id)).Single(j => j.Id == first.Id).Status);
    }

    /// <summary>Autonomous mode, in specification with a locked project request and spec: advancing enters planning without a gate.</summary>
    private static async Task PlanningReadyAsync(WebApplicationFactory<Program> app, Guid id)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Specification, 2, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(
            app,
            id,
            ArtifactType.TechnicalSpecification,
            content: string.Join("\n\n", SpecSections.Required.Select((name, i) => $"## {i + 1}. {name}\nDetails.")),
            projectVersionAtCreation: 2);
    }

    private static WebApplicationFactory<Program> WithRival(WebApplicationFactory<Program> app, DraftSaveRival rival) =>
        app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AideDbContext>(o => o.AddInterceptors(rival))));

    /// <summary>
    /// Runs <paramref name="sql"/> against the project from another connection once, just before the first agent draft is
    /// saved (after the writer's phase check), so the save meets a project row that changed underneath it.
    /// </summary>
    private sealed class DraftSaveRival(string connectionString, string sql) : SaveChangesInterceptor
    {
        private int _fired;

        public Guid ProjectId { get; set; }

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Artifact>().Any(e => e.State == EntityState.Added && e.Entity.AgentRunId is not null && e.Entity.ProjectId == ProjectId)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("id", ProjectId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }
}
