using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Tokens;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.IntegrationTests.Admin;

/// <summary>FR-51 (and FR-23's admin side): the admin event log, run log and dead letters, and dead-letter retry (§6 /api/admin).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AdminOperationsTests(AideApiFactory factory)
{
    private readonly string _jobType = $"test.{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Dead-letter retry ────────────────────────────────────────────────

    [Fact]
    public async Task FR51_Retrying_a_dead_letter_runs_the_job_again_and_marks_the_dead_letter_retried()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, _) = await NewProjectAsync();
        var dead = await AddJobAsync(_jobType, JobStatus.Dead, projectId, payload: """{"hello":"again"}""", attempts: 4, lastError: "provider returned 503");

        var response = await admin.Client.PostAsync(RetryUri(dead.Id), null, Ct);

        var job = await ProjectApi.ReadAsync<JobDto>(response);
        Assert.NotEqual(dead.Id, job.Id);
        Assert.Equal((_jobType, (Guid?)projectId, JobStatus.Queued, 0), (job.Type, job.ProjectId, job.Status, job.Attempts));
        var deadAfter = await LoadJobAsync(dead.Id);
        Assert.Equal((JobStatus.Retried, 4, "provider returned 503"), (deadAfter.Status, deadAfter.Attempts, deadAfter.LastError));
        var audit = await ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking()
            .SingleAsync(e => e.ProjectId == projectId && e.Type == EventTypes.DeadLetterRetried, Ct));
        Assert.Equal(admin.User!.Id.ToString(), audit.Actor);
        Assert.Contains(job.Id.ToString(), audit.Payload, StringComparison.Ordinal);
        Assert.Contains(dead.Id.ToString(), audit.Payload, StringComparison.Ordinal);

        // The job runs again with the original payload.
        var handler = new RecordingHandler();
        Assert.Equal(1, await NewWorker(handler).RunOnceAsync(Ct));
        var execution = Assert.Single(handler.Executions);
        Assert.Equal((job.Id, 1), (execution.Id, execution.Attempt));
        Assert.Equal("again", LifecycleSeed.Payload(execution.Payload).GetProperty("hello").GetString());
        Assert.Equal(JobStatus.Succeeded, (await LoadJobAsync(job.Id)).Status);
        Assert.Equal(JobStatus.Retried, (await LoadJobAsync(dead.Id)).Status);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Retried)]
    public async Task FR51_Retrying_a_job_that_is_not_a_dead_letter_returns_404_NOT_FOUND(JobStatus status)
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var job = await AddJobAsync(_jobType, status);

        var response = await admin.Client.PostAsync(RetryUri(job.Id), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(status, (await LoadJobAsync(job.Id)).Status);
        Assert.Single(await JobsOfTypeAsync(_jobType));
    }

    [Fact]
    public async Task FR51_Retrying_an_unknown_job_returns_404_NOT_FOUND()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.PostAsync(RetryUri(Guid.NewGuid()), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR51_A_dead_letter_is_retried_only_once_even_by_concurrent_requests()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        using var otherAdmin = await ApiSession.AdminAsync(factory);
        var dead = await AddJobAsync(_jobType, JobStatus.Dead);

        var responses = await Task.WhenAll(
            admin.Client.PostAsync(RetryUri(dead.Id), null, Ct),
            otherAdmin.Client.PostAsync(RetryUri(dead.Id), null, Ct));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NotFound);
        var jobs = await JobsOfTypeAsync(_jobType);
        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs, j => j.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task FR51_Retrying_a_dead_execution_job_while_another_is_active_returns_409_CONFLICT()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, _) = await NewProjectAsync();
        var dead = await AddJobAsync(JobTypes.Execution, JobStatus.Dead, projectId);
        await AddJobAsync(JobTypes.Execution, JobStatus.Queued, projectId);

        var response = await admin.Client.PostAsync(RetryUri(dead.Id), null, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Contains("execution job is already queued or running", body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(JobStatus.Dead, (await LoadJobAsync(dead.Id)).Status);
    }

    [Fact]
    public async Task FR51_A_dead_execution_job_is_retried_when_no_other_is_active()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, _) = await NewProjectAsync();
        var dead = await AddJobAsync(JobTypes.Execution, JobStatus.Dead, projectId);

        var job = await ProjectApi.ReadAsync<JobDto>(await admin.Client.PostAsync(RetryUri(dead.Id), null, Ct));

        Assert.Equal((JobTypes.Execution, JobStatus.Queued), (job.Type, job.Status));
        Assert.Equal(JobStatus.Retried, (await LoadJobAsync(dead.Id)).Status);
    }

    [Fact]
    public async Task FR51_An_admin_api_token_can_read_admin_lists_but_cannot_retry()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var dead = await AddJobAsync(_jobType, JobStatus.Dead);
        var created = await admin.Client.PostAsJsonAsync("/api/me/tokens", new { name = "admin-cli" }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<CreatedApiTokenDto>(ApiSession.Json, Ct))!;
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.PostAsync(RetryUri(dead.Id), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal(JobStatus.Dead, (await LoadJobAsync(dead.Id)).Status);
        foreach (var path in ListPaths)
        {
            Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode);
        }
    }

    // ── Authorization ────────────────────────────────────────────────────

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "GET", "/api/admin/events" },
        { "GET", "/api/admin/runs" },
        { "GET", "/api/admin/dead-letters" },
        { "POST", $"/api/admin/dead-letters/{Guid.NewGuid()}/retry" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task FR51_Non_admin_gets_403_FORBIDDEN(string method, string path)
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task FR51_Anonymous_gets_401_UNAUTHENTICATED(string method, string path)
    {
        using var anonymous = new ApiSession(factory);

        var response = await anonymous.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    // ── Event log ────────────────────────────────────────────────────────

    [Fact]
    public async Task FR51_Event_log_lists_events_of_every_project_with_project_name_actor_and_summary()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectA, nameA) = await NewProjectAsync();
        var (projectB, _) = await NewProjectAsync();

        var all = await GetAsync<PagedResult<AdminEventDto>>(admin, "/api/admin/events?limit=100");
        var ofA = await GetAsync<PagedResult<AdminEventDto>>(admin, $"/api/admin/events?projectId={projectA}");

        Assert.True(all.Total >= 2);
        var created = Assert.Single(ofA.Items, e => e.Type == EventTypes.ProjectCreated);
        Assert.All(ofA.Items, e => Assert.Equal(projectA, e.ProjectId));
        Assert.Equal((nameA, (EventCategory?)EventCategory.Project, EventOutcome.Ok, "Project created"), (created.ProjectName, created.Category, created.Outcome, created.Summary));
        Assert.False(created.Actor.IsSystem);
        Assert.NotNull(created.Actor.Id);
        Assert.DoesNotContain(ofA.Items, e => e.ProjectId == projectB);
    }

    [Fact]
    public async Task FR51_Event_log_filters_by_type_category_outcome_and_time_range()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, _) = await NewProjectAsync();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(-30);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.Events.Add(new AuditEvent { ProjectId = projectId, Type = EventTypes.RunFailed, Payload = """{"agent":"spec_generator","errorCode":"PROVIDER_ERROR"}""", Actor = "aide-system", CreatedAt = t0 });
            db.Events.Add(new AuditEvent { ProjectId = projectId, Type = EventTypes.IntegrityAlert, Payload = "{}", Actor = "aide-system", CreatedAt = t0.AddHours(1) });
            db.Events.Add(new AuditEvent { ProjectId = projectId, Type = EventTypes.PhaseChanged, Payload = """{"from":"refinement","to":"specification","projectVersion":2}""", Actor = "aide-system", CreatedAt = t0.AddHours(2) });
            await db.SaveChangesAsync(Ct);
        });
        var basePath = $"/api/admin/events?projectId={projectId}";

        var failures = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{basePath}&outcome=failure");
        var ok = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{basePath}&outcome=ok");
        var byType = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{basePath}&type=PHASE_CHANGED");
        var byCategory = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{basePath}&category=run");
        var inRange = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{basePath}&from={Uri.EscapeDataString(t0.AddMinutes(30).ToString("O"))}&to={Uri.EscapeDataString(t0.AddHours(2).ToString("O"))}");

        Assert.Equal([EventTypes.IntegrityAlert, EventTypes.RunFailed], failures.Items.Select(e => e.Type));
        Assert.All(failures.Items, e => Assert.Equal(EventOutcome.Failure, e.Outcome));
        Assert.DoesNotContain(ok.Items, e => e.Type is EventTypes.RunFailed or EventTypes.IntegrityAlert);
        Assert.Contains(ok.Items, e => e.Type == EventTypes.ProjectCreated);
        var phase = Assert.Single(byType.Items);
        Assert.Equal("Phase changed from refinement to specification (version 2)", phase.Summary);
        Assert.True(phase.Actor.IsSystem);
        Assert.Equal(2, phase.Payload["projectVersion"]!.GetValue<int>());
        Assert.Equal([EventTypes.RunFailed], byCategory.Items.Select(e => e.Type));
        Assert.Equal([EventTypes.PhaseChanged, EventTypes.IntegrityAlert], inRange.Items.Select(e => e.Type));
    }

    [Fact]
    public async Task FR51_Event_log_pages_newest_first()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, _) = await NewProjectAsync();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            for (var i = 0; i < 5; i++)
            {
                db.Events.Add(new AuditEvent { ProjectId = projectId, Type = EventTypes.StepCompleted, Payload = $$"""{"stepNumber":{{i}}}""", Actor = "aide-system", CreatedAt = t0.AddMinutes(i) });
            }

            await db.SaveChangesAsync(Ct);
        });
        var path = $"/api/admin/events?projectId={projectId}&type=STEP_COMPLETED&limit=2";

        var page1 = await GetAsync<PagedResult<AdminEventDto>>(admin, path);
        var page3 = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{path}&page=3");
        var page4 = await GetAsync<PagedResult<AdminEventDto>>(admin, $"{path}&page=4");

        Assert.Equal((5, 1, 2), (page1.Total, page1.Page, page1.Limit));
        Assert.Equal(["Step 4 completed", "Step 3 completed"], page1.Items.Select(e => e.Summary));
        Assert.Equal(["Step 0 completed"], page3.Items.Select(e => e.Summary));
        Assert.Empty(page4.Items);
        Assert.Equal(5, page4.Total);
    }

    [Theory]
    [InlineData("from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("type=run_failed")]
    [InlineData("category=nope")]
    [InlineData("outcome=bad")]
    [InlineData("limit=101")]
    [InlineData("page=0")]
    public async Task FR51_Event_log_with_invalid_filters_returns_400_VALIDATION(string query)
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.GetAsync(new Uri($"/api/admin/events?{query}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    // ── Run log ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FR23_FR51_Run_log_lists_runs_of_every_project_with_filters_and_paging()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectA, nameA) = await NewProjectAsync();
        var (projectB, nameB) = await NewProjectAsync();
        var model = $"model-{Guid.NewGuid():N}";
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.AgentRuns.Add(NewRun(projectA, AgentKind.SpecGenerator, AgentRunStatus.Succeeded, LlmProvider.Fake, model, t0, error: null));
            db.AgentRuns.Add(NewRun(projectA, AgentKind.PlanGenerator, AgentRunStatus.Failed, LlmProvider.Anthropic, model, t0.AddMinutes(1), error: "PROVIDER_ERROR: 503"));
            db.AgentRuns.Add(NewRun(projectB, AgentKind.CodeReview, AgentRunStatus.Succeeded, LlmProvider.Openai, model, t0.AddMinutes(2), error: null));
            await db.SaveChangesAsync(Ct);
        });
        var path = $"/api/admin/runs?model={model}";

        var all = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, path);
        var ofA = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, $"{path}&projectId={projectA}");
        var failed = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, $"{path}&status=failed");
        var openai = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, $"{path}&provider=openai");
        var spec = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, $"{path}&agent=spec_generator");
        var page2 = await GetAsync<PagedResult<AdminAgentRunDto>>(admin, $"{path}&limit=2&page=2");

        Assert.Equal(3, all.Total);
        Assert.Equal([nameB, nameA, nameA], all.Items.Select(r => r.ProjectName));
        Assert.Equal([projectA, projectA], ofA.Items.Select(r => r.ProjectId));
        var failedRun = Assert.Single(failed.Items);
        Assert.Equal((AgentKind.PlanGenerator, "PROVIDER_ERROR: 503", 120, 80, 0.0123m, 4567L), (failedRun.Agent, failedRun.Error, failedRun.InputTokens, failedRun.OutputTokens, failedRun.CostEstimate, failedRun.DurationMs));
        Assert.Equal(projectB, Assert.Single(openai.Items).ProjectId);
        Assert.Equal(AgentKind.SpecGenerator, Assert.Single(spec.Items).Agent);
        Assert.Equal((3, 1), (page2.Total, page2.Items.Count));
        Assert.Equal(AgentKind.SpecGenerator, page2.Items[0].Agent);
    }

    [Theory]
    [InlineData("agent=nope")]
    [InlineData("status=dead")]
    [InlineData("provider=gemini")]
    [InlineData("limit=0")]
    public async Task FR51_Run_log_with_invalid_filters_returns_400_VALIDATION(string query)
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.GetAsync(new Uri($"/api/admin/runs?{query}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    // ── Dead letters ─────────────────────────────────────────────────────

    [Fact]
    public async Task FR51_Dead_letters_list_shows_dead_jobs_with_their_error_and_filters()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var (projectId, projectName) = await NewProjectAsync();
        // Distinct dead-lettered times so the expected order doesn't hinge on the id tiebreak.
        var dead = await AddJobAsync(_jobType, JobStatus.Dead, projectId, payload: """{"x":1}""", attempts: 4, lastError: "boom", completedAgo: TimeSpan.FromMinutes(2));
        var retried = await AddJobAsync(_jobType, JobStatus.Retried, projectId, completedAgo: TimeSpan.FromMinutes(1));
        await AddJobAsync(_jobType, JobStatus.Failed, projectId);
        await AddJobAsync(_jobType, JobStatus.Dead);
        var path = $"/api/admin/dead-letters?type={_jobType}";

        var byType = await GetAsync<PagedResult<DeadLetterDto>>(admin, path);
        var byProject = await GetAsync<PagedResult<DeadLetterDto>>(admin, $"{path}&projectId={projectId}");
        var withRetried = await GetAsync<PagedResult<DeadLetterDto>>(admin, $"{path}&projectId={projectId}&includeRetried=true");
        var paged = await GetAsync<PagedResult<DeadLetterDto>>(admin, $"{path}&limit=1&page=2");

        Assert.Equal(2, byType.Total);
        Assert.All(byType.Items, d => Assert.Equal(JobStatus.Dead, d.Status));
        var item = Assert.Single(byProject.Items);
        Assert.Equal((dead.Id, _jobType, (Guid?)projectId, projectName, 4, "boom"), (item.Id, item.Type, item.ProjectId, item.ProjectName, item.Attempts, item.LastError));
        Assert.Equal(1, item.Payload["x"]!.GetValue<int>());
        Assert.NotNull(item.DeadLetteredAt);
        Assert.Equal([retried.Id, dead.Id], withRetried.Items.Select(d => d.Id)); // newest dead-lettered first
        Assert.Equal((2, 1), (paged.Total, paged.Items.Count));
        Assert.Null(byType.Items.Single(d => d.ProjectId is null).ProjectName);
    }

    [Fact]
    public async Task FR51_A_retried_dead_letter_leaves_the_dead_letter_list()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var dead = await AddJobAsync(_jobType, JobStatus.Dead);
        var path = $"/api/admin/dead-letters?type={_jobType}";
        Assert.Equal(1, (await GetAsync<PagedResult<DeadLetterDto>>(admin, path)).Total);

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync(RetryUri(dead.Id), null, Ct)).StatusCode);

        Assert.Equal(0, (await GetAsync<PagedResult<DeadLetterDto>>(admin, path)).Total);
        var withRetried = await GetAsync<PagedResult<DeadLetterDto>>(admin, $"{path}&includeRetried=true");
        Assert.Equal(JobStatus.Retried, Assert.Single(withRetried.Items).Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static readonly string[] ListPaths = ["/api/admin/events", "/api/admin/runs", "/api/admin/dead-letters"];

    private static Uri RetryUri(Guid jobId) => new($"/api/admin/dead-letters/{jobId}/retry", UriKind.Relative);

    private static async Task<T> GetAsync<T>(ApiSession session, string path) =>
        await ProjectApi.ReadAsync<T>(await session.Client.GetAsync(new Uri(path, UriKind.Relative), Ct));

    private async Task<(Guid Id, string Name)> NewProjectAsync()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);
        return (project.Id, project.Name);
    }

    private Task<Job> AddJobAsync(
        string type, JobStatus status, Guid? projectId = null, string payload = "{}", int attempts = 0, string? lastError = null,
        TimeSpan? completedAgo = null) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var job = new Job
            {
                Type = type,
                Payload = payload,
                ProjectId = projectId,
                Status = status,
                Attempts = attempts,
                LastError = lastError,
                RunAfter = now.AddMinutes(-1),
                CreatedAt = now.AddMinutes(-5),
                LockedBy = status == JobStatus.Running ? "worker" : null,
                LockedAt = status == JobStatus.Running ? now : null,
                CompletedAt = status is JobStatus.Queued or JobStatus.Running ? null : now - (completedAgo ?? TimeSpan.Zero),
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job;
        });

    private Task<Job> LoadJobAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));

    private Task<List<Job>> JobsOfTypeAsync(string type) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().Where(j => j.Type == type).ToListAsync(Ct));

    private static AgentRun NewRun(Guid projectId, AgentKind agent, AgentRunStatus status, LlmProvider provider, string model, DateTimeOffset createdAt, string? error) => new()
    {
        ProjectId = projectId,
        Agent = agent,
        Phase = Phase.Specification,
        Provider = provider,
        Model = model,
        PromptVersion = "1",
        Status = status,
        Attempts = 1,
        InputTokens = 120,
        OutputTokens = 80,
        CostEstimate = 0.0123m,
        DurationMs = 4567,
        Error = error,
        CreatedAt = createdAt,
        CompletedAt = createdAt.AddSeconds(5),
    };

    private JobWorker NewWorker(IJobHandler handler) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        [new JobHandlerRegistration(_jobType, _ => handler)],
        Options.Create(new JobOptions { Enabled = true }),
        TimeProvider.System,
        factory.Services.GetRequiredService<ILogger<JobWorker>>());

    private sealed class RecordingHandler : IJobHandler
    {
        private readonly ConcurrentQueue<JobExecution> _executions = new();

        public IReadOnlyList<JobExecution> Executions => [.. _executions];

        public Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
        {
            _executions.Enqueue(job);
            return Task.CompletedTask;
        }
    }
}
