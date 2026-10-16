using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Runs;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.IntegrationTests.Agents;

/// <summary>
/// FR-22–FR-26, NFR-OBS: the agent runner against PostgreSQL, with the real providers pointed at fake HTTP handlers
/// and the Fake provider's responders counted — no test reaches a real LLM API.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AgentRunnerTests(AideApiFactory factory) : IDisposable
{
    private const string ProjectRequest = "# Todo API\n\n## Summary\nA small todo API.";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private readonly ScriptedHandler _ollama = new();
    private readonly ScriptedHandler _anthropic = new();
    private readonly CountingResponder _spec = new(AgentKind.SpecGenerator, "## 1. Overview\nA fake spec.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR22_A_project_set_to_Ollama_with_qwen_calls_the_Ollama_provider_with_that_model()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        _ollama.Enqueue(HttpStatusCode.OK, """{"message":{"role":"assistant","content":"spec text"},"done_reason":"stop","prompt_eval_count":50,"eval_count":20}""");

        var outcome = await RunAsync(app, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));

        var sent = Assert.Single(_ollama.Requests);
        Assert.Equal("http://ollama.test:11434/api/chat", sent.Uri);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(("qwen2.5-coder", false), (body.GetProperty("model").GetString(), body.GetProperty("stream").GetBoolean()));
        Assert.Contains(ProjectRequest, body.GetProperty("messages")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal((LlmProvider.Ollama, "qwen2.5-coder", "spec text"), (outcome.Provider, outcome.Model, outcome.Text));
        Assert.Equal(0, _spec.Calls);
    }

    [Fact]
    public async Task FR23_A_completed_spec_generation_records_model_tokens_duration_and_the_project_request_version()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var job = NewJob(fixture.Project.Id);
        var request = Spec(fixture.Project.Id, job) with { UserFeedback = "Add rate limits." };

        var outcome = await RunAsync(app, request);

        Assert.Equal(1, _spec.Calls);
        Assert.Contains("Add rate limits.", _spec.LastRequest!.System + _spec.LastRequest.Messages[0].Content, StringComparison.Ordinal);
        var run = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/runs/{outcome.RunId}", UriKind.Relative), Ct));
        Assert.Equal(job.Id, run.Id); // one run row per job
        Assert.Equal((AgentKind.SpecGenerator, Aide.Domain.Lifecycle.Phase.Refinement, LlmProvider.Fake, "fake-1", "2"), (run.Agent, run.Phase, run.Provider, run.Model, run.PromptVersion));
        Assert.Equal([new RunInputArtifactDto(ArtifactType.ProjectRequest, 2)], run.InputArtifacts);
        Assert.Equal((AgentRunStatus.Succeeded, 1, (string?)null), (run.Status, run.Attempts, run.Error));
        Assert.True(run.InputTokens > 0 && run.OutputTokens > 0);
        Assert.True(run.DurationMs >= 0);
        Assert.Equal(0m, run.CostEstimate); // fake-1 is priced at 0
        Assert.Equal("Add rate limits.", run.UserFeedback);
        Assert.Equal("## 1. Overview\nA fake spec.", run.Output);
        Assert.NotNull(run.CompletedAt);

        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(run.InputTokens + run.OutputTokens, state.TokensUsed);
        Assert.Equal([EventTypes.RunStarted, EventTypes.RunCompleted], await RunEventsAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR23_Cost_is_estimated_from_the_configured_model_price()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await SetLlmInDbAsync(fixture.Project.Id, LlmProvider.Anthropic, "claude-opus-5-5", apiKey: "sk-project-key");
        _anthropic.Enqueue(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":100000,"output_tokens":50000}}""");

        var outcome = await RunAsync(app, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));

        Assert.Equal("sk-project-key", Assert.Single(_anthropic.Requests).Headers["x-api-key"]);
        var run = await LoadRunAsync(outcome.RunId);
        Assert.Equal(1.4m, run.CostEstimate); // 100k × $4/MTok + 50k × $20/MTok
        Assert.Equal(150_000, await TokensUsedAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR24_Spec_generation_without_an_approved_project_request_fails_with_MISSING_INPUT_and_no_provider_call()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var job = NewJob(fixture.Project.Id);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, job) with { Inputs = new Dictionary<string, string?>() }));

        Assert.Equal((false, ErrorCodes.MissingInput), (ex.Transient, ex.ErrorCode));
        Assert.Equal(0, _spec.Calls);
        var run = await LoadRunAsync(job.Id);
        Assert.Equal((AgentRunStatus.Failed, 0), (run.Status, run.Attempts));
        Assert.StartsWith("MISSING_INPUT: ", run.Error, StringComparison.Ordinal);
        Assert.Contains("PROJECT_REQUEST", run.Error, StringComparison.Ordinal);
        Assert.Equal([EventTypes.RunStarted, EventTypes.RunFailed], await RunEventsAsync(fixture.Project.Id));
        Assert.Equal(0, await TokensUsedAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR22_Anthropic_without_any_key_fails_as_PROVIDER_NOT_CONFIGURED_without_calling()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await SetLlmInDbAsync(fixture.Project.Id, LlmProvider.Anthropic, "claude-opus-5-5", apiKey: null);
        var job = NewJob(fixture.Project.Id);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, job)));

        Assert.Equal((false, ErrorCodes.ProviderNotConfigured), (ex.Transient, ex.ErrorCode));
        Assert.Empty(_anthropic.Requests);
        Assert.Equal((AgentRunStatus.Failed, 0), ((await LoadRunAsync(job.Id)).Status, (await LoadRunAsync(job.Id)).Attempts));
    }

    [Fact]
    public async Task FR25_A_provider_that_returns_503_four_times_logs_4_attempts_fails_the_run_and_dead_letters_the_job()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        for (var i = 0; i < 4; i++)
        {
            _ollama.Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":"overloaded"}""");
        }

        var job = await AddJobAsync(fixture.Project.Id);
        var clock = new Clock(T0);
        var worker = Worker(app, clock);

        Assert.Equal(1, await worker.RunOnceAsync(Ct));
        foreach (var delay in new[] { 2, 8, 32 })
        {
            var running = await LoadRunAsync(job.Id);
            Assert.Equal(AgentRunStatus.Running, running.Status);
            clock.Now = clock.Now.AddSeconds(delay);
            Assert.Equal(1, await worker.RunOnceAsync(Ct));
        }

        Assert.Equal(4, _ollama.Requests.Count);
        var run = await LoadRunAsync(job.Id);
        Assert.Equal((AgentRunStatus.Failed, 4), (run.Status, run.Attempts));
        Assert.Contains("503", run.Error, StringComparison.Ordinal);
        Assert.Equal(1, await ProjectApi.WithDbAsync(factory, db => db.AgentRuns.CountAsync(r => r.ProjectId == fixture.Project.Id, Ct)));
        var dead = await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal((JobStatus.Dead, 4), (dead.Status, dead.Attempts));

        var failed = await ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking().SingleAsync(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.RunFailed, Ct));
        var payload = JsonDocument.Parse(failed.Payload).RootElement;
        Assert.True(payload.GetProperty("deadLettered").GetBoolean());
        Assert.Equal(4, payload.GetProperty("attempts").GetInt32());

        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(0, await worker.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task FR25_A_transient_failure_then_success_completes_the_same_run_on_the_retry()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        _ollama.Enqueue(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""");
        _ollama.Enqueue(HttpStatusCode.OK, """{"message":{"content":"ok"},"prompt_eval_count":3,"eval_count":4}""");
        var job = await AddJobAsync(fixture.Project.Id);
        var clock = new Clock(T0);
        var worker = Worker(app, clock);

        await worker.RunOnceAsync(Ct);
        Assert.Contains("429", (await LoadRunAsync(job.Id)).Error, StringComparison.Ordinal);
        clock.Now = clock.Now.AddSeconds(2);
        await worker.RunOnceAsync(Ct);

        var run = await LoadRunAsync(job.Id);
        Assert.Equal((AgentRunStatus.Succeeded, 2, 3, 4, (string?)null), (run.Status, run.Attempts, run.InputTokens, run.OutputTokens, run.Error));
        Assert.Equal(JobStatus.Succeeded, (await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct))).Status);
        Assert.Equal(7, await TokensUsedAsync(fixture.Project.Id));
        Assert.Equal([EventTypes.RunStarted, EventTypes.RunCompleted], await RunEventsAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR25_A_permanent_provider_failure_fails_the_run_and_the_job_at_once()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        _ollama.Enqueue(HttpStatusCode.NotFound, """{"error":"model 'qwen2.5-coder' not found"}""");
        var job = await AddJobAsync(fixture.Project.Id);

        await Worker(app, new Clock(T0)).RunOnceAsync(Ct);

        var run = await LoadRunAsync(job.Id);
        Assert.Equal((AgentRunStatus.Failed, 1), (run.Status, run.Attempts));
        Assert.Contains("not found", run.Error, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Failed, (await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct))).Status);
    }

    [Fact]
    public async Task FR25_A_job_dead_lettered_by_the_lease_reaper_fails_every_run_it_left_running()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        var job = await AddJobAsync(fixture.Project.Id);
        var execution = new JobExecution(job.Id, TestJobType, "{}", fixture.Project.Id, 1);

        // Attempt 1 of two runs in the job (the job's own run and a keyed one) fails transiently: both stay running.
        await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, execution)));
        await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, execution) with { RunKey = "corrective" }));
        var keyedId = AgentRunRules.RunId(job.Id, "corrective");
        Assert.Equal(AgentRunStatus.Running, (await LoadRunAsync(job.Id)).Status);
        Assert.Equal(AgentRunStatus.Running, (await LoadRunAsync(keyedId)).Status);

        // A running run of another job in the same project is left alone.
        var other = await AddJobAsync(fixture.Project.Id);
        await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, new JobExecution(other.Id, TestJobType, "{}", fixture.Project.Id, 1))));

        // The worker running the job's last attempt dies: its lease expires and the reaper dead-letters the job.
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, JobStatus.Running).SetProperty(j => j.Attempts, 4)
                    .SetProperty(j => j.LockedBy, "dead-worker").SetProperty(j => j.LockedAt, (DateTimeOffset?)T0),
                Ct));
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed), Ct));
        await Worker(app, new Clock(T0.AddMinutes(10))).RunOnceAsync(Ct);

        Assert.Equal(JobStatus.Dead, (await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct))).Status);
        foreach (var id in new[] { job.Id, keyedId })
        {
            var run = await LoadRunAsync(id);
            Assert.Equal(AgentRunStatus.Failed, run.Status);
            Assert.StartsWith("PROVIDER_ERROR: The job was dead-lettered", run.Error, StringComparison.Ordinal);
            Assert.NotNull(run.CompletedAt);
        }

        Assert.Equal(AgentRunStatus.Running, (await LoadRunAsync(other.Id)).Status);
        var failed = await ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.RunFailed).Select(e => e.Payload).ToListAsync(Ct));
        Assert.Equal(2, failed.Count);
        Assert.All(failed, p => Assert.True(JsonDocument.Parse(p).RootElement.GetProperty("deadLettered").GetBoolean()));
        Assert.Equal(
            new[] { job.Id, keyedId }.Order(),
            failed.Select(p => JsonDocument.Parse(p).RootElement.GetProperty("runId").GetGuid()).Order());
    }

    [Fact]
    public async Task FR25_An_unexpected_exception_from_the_provider_call_fails_the_run_as_PROVIDER_ERROR()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await UseOllamaAsync(fixture);
        _ollama.EnqueueException(new InvalidOperationException("Handler bug quoting sk-ant-leaked-in-message-123"));
        var job = await AddJobAsync(fixture.Project.Id);

        await Worker(app, new Clock(T0)).RunOnceAsync(Ct);

        var run = await LoadRunAsync(job.Id);
        Assert.Equal((AgentRunStatus.Failed, 1), (run.Status, run.Attempts));
        Assert.Equal("PROVIDER_ERROR: ollama call failed unexpectedly (InvalidOperationException).", run.Error);
        var dbJob = await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal(JobStatus.Failed, dbJob.Status);
        Assert.StartsWith("PROVIDER_ERROR: ", dbJob.LastError, StringComparison.Ordinal);
        Assert.Equal([EventTypes.RunStarted, EventTypes.RunFailed], await RunEventsAsync(fixture.Project.Id));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.ToString().Contains("sk-ant-leaked-in-message-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR25_A_stored_key_with_a_newline_fails_the_run_instead_of_leaving_it_running()
    {
        const string key = "sk-ant-newline-key-first\nsecond-part-of-key";
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await SetLlmInDbAsync(fixture.Project.Id, LlmProvider.Anthropic, "claude-opus-5-5", apiKey: key);
        var job = await AddJobAsync(fixture.Project.Id);

        await Worker(app, new Clock(T0)).RunOnceAsync(Ct);

        var run = await LoadRunAsync(job.Id);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.StartsWith("PROVIDER_ERROR: ", run.Error, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Failed, (await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct))).Status);
        await AssertKeyNowhereAsync(fixture.Project.Id, job.Id, "sk-ant-newline-key-first", "second-part-of-key");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-ant-project-secret-key-123. Check it.","type":"authentication_error"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Bad header x-api-key: sk-ant-project-secret-key-123 (Authorization: Bearer sk-ant-project-secret-key-123)"}}""")]
    public async Task NFR_SEC_A_project_key_never_reaches_the_run_error_events_job_or_logs(HttpStatusCode status, string body)
    {
        const string key = "sk-ant-project-secret-key-123";
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await SetLlmInDbAsync(fixture.Project.Id, LlmProvider.Anthropic, "claude-opus-5-5", apiKey: key);
        _anthropic.Enqueue(status, body);
        var job = await AddJobAsync(fixture.Project.Id);

        await Worker(app, new Clock(T0)).RunOnceAsync(Ct);

        Assert.Equal(key, Assert.Single(_anthropic.Requests).Headers["x-api-key"]);
        var run = await LoadRunAsync(job.Id);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        if (status == HttpStatusCode.Unauthorized)
        {
            // FR-78: a bound key the provider rejects fails the run as CREDENTIAL_INVALID (LlmBindingTests covers the rest).
            Assert.Equal("CREDENTIAL_INVALID: The bound anthropic connection's key was rejected (HTTP 401); its owner must replace it.", run.Error);
        }

        await AssertKeyNowhereAsync(fixture.Project.Id, job.Id, key, "project-secret-key");
    }

    private async Task AssertKeyNowhereAsync(Guid projectId, Guid jobId, params string[] fragments)
    {
        var run = await LoadRunAsync(jobId);
        var dbJob = await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct));
        var payloads = await ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking().Where(e => e.ProjectId == projectId).Select(e => e.Payload).ToListAsync(Ct));
        var logs = factory.Logs.Entries.Select(e => e.ToString()).ToList();
        foreach (var fragment in fragments)
        {
            Assert.DoesNotContain(fragment, run.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(fragment, dbJob.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.All(payloads, p => Assert.DoesNotContain(fragment, p, StringComparison.Ordinal));
            Assert.All(logs, l => Assert.DoesNotContain(fragment, l, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FR25_A_redelivered_job_whose_run_succeeded_returns_the_stored_output_without_calling_again()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var job = NewJob(fixture.Project.Id);

        var first = await RunAsync(app, Spec(fixture.Project.Id, job));
        var second = await RunAsync(app, Spec(fixture.Project.Id, job with { Attempt = 2 }));

        Assert.Equal(1, _spec.Calls);
        Assert.Equal(first, second);
        var tokens = await TokensUsedAsync(fixture.Project.Id);
        Assert.Equal(first.InputTokens + first.OutputTokens, tokens);
    }

    [Fact]
    public async Task FR25_Several_runs_in_one_job_are_told_apart_by_run_key()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var job = NewJob(fixture.Project.Id);

        var first = await RunAsync(app, Spec(fixture.Project.Id, job));
        var retry = await RunAsync(app, Spec(fixture.Project.Id, job) with { RunKey = "missing-sections", UserFeedback = "Missing: Testing Strategy" });

        Assert.NotEqual(first.RunId, retry.RunId);
        Assert.Equal(2, _spec.Calls);
    }

    [Fact]
    public async Task FR26_A_run_after_the_budget_is_used_up_is_refused_with_BUDGET_EXCEEDED_until_an_Owner_raises_it()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == fixture.Project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokenBudget, (long?)100_000L).SetProperty(p => p.TokensUsed, 100_000L), Ct));
        var job = NewJob(fixture.Project.Id);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, job)));

        Assert.Equal((false, ErrorCodes.BudgetExceeded), (ex.Transient, ex.ErrorCode));
        Assert.Equal(0, _spec.Calls);
        Assert.Equal(0, await ProjectApi.WithDbAsync(factory, db => db.AgentRuns.CountAsync(r => r.ProjectId == fixture.Project.Id, Ct)));
        Assert.Equal([EventTypes.BudgetExceeded], await RunEventsAsync(fixture.Project.Id));

        var forbidden = await ProjectApi.PatchAsync(fixture.Contributor.Client, fixture.Path, new { tokenBudget = 200_000 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var raised = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { tokenBudget = 200_000 }));
        Assert.Equal(200_000L, raised.TokenBudget);

        await RunAsync(app, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));
        Assert.Equal(1, _spec.Calls);
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(200_000L, state.TokenBudget);
        Assert.True(state.TokensUsed > 100_000);
    }

    [Fact]
    public async Task NFR_OBS_Run_log_entries_carry_the_run_id()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var job = NewJob(fixture.Project.Id);

        await RunAsync(app, Spec(fixture.Project.Id, job));

        var entry = Assert.Single(factory.Logs.Entries, e => e.Message.StartsWith("Agent run succeeded", StringComparison.Ordinal)
            && e.Scopes.Any(kv => kv.Key == "RunId" && Equals(kv.Value, job.Id)));
        Assert.Contains(entry.Scopes, kv => kv.Key == "ProjectId" && Equals(kv.Value, fixture.Project.Id));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.ToString().Contains(ProjectRequest, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _ollama.Dispose();
        _anthropic.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private WebApplicationFactory<Program> App() =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Llm:Providers:Ollama:BaseUrl", "http://ollama.test:11434");
            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient<OllamaLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => _ollama);
                services.AddHttpClient<AnthropicLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => _anthropic);
                services.AddHttpClient<OpenAiLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => new ScriptedHandler());
                services.AddSingleton<IFakeAgentResponder>(_spec);
            });
        });

    private static JobExecution NewJob(Guid projectId) => new(Guid.CreateVersion7(), JobTypes.SpecGeneration, "{}", projectId, 1);

    private static AgentRunRequest Spec(Guid projectId, JobExecution job) => new()
    {
        ProjectId = projectId,
        Agent = AgentKind.SpecGenerator,
        Job = job,
        Inputs = new Dictionary<string, string?> { ["PROJECT_REQUEST"] = ProjectRequest },
        InputArtifacts = [new InputArtifactRef(Guid.CreateVersion7(), ArtifactType.ProjectRequest, 2)],
    };

    private static async Task<AgentRunOutcome> RunAsync(WebApplicationFactory<Program> app, AgentRunRequest request)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentRunner>().RunAsync(request, Ct);
    }

    private static async Task UseOllamaAsync(ProjectFixture fixture)
    {
        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "ollama", defaultModel = "qwen2.5-coder" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task SetLlmInDbAsync(Guid projectId, LlmProvider provider, string model, string? apiKey) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var settings = await db.ProjectLlmSettings.SingleAsync(s => s.ProjectId == projectId, Ct);
            settings.Provider = provider;
            settings.DefaultModel = model;
            settings.ModelsByAgent = new Dictionary<string, string>();
            // FR-79: the project's key is a connection of its earliest Owner, bound to the project.
            if (apiKey is null)
            {
                settings.CredentialId = null;
                settings.BoundByUserId = null;
            }
            else
            {
                var ownerId = await db.ProjectMembers
                    .Where(m => m.ProjectId == projectId && m.Role == Aide.Domain.Access.ProjectRole.Owner)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.UserId)
                    .FirstAsync(Ct);
                var credential = new LlmCredential
                {
                    UserId = ownerId,
                    Provider = provider,
                    Label = "test",
                    SecretEncrypted = secrets.Protect(apiKey),
                    Last4 = LlmCredential.Last4Of(apiKey),
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.LlmCredentials.Add(credential);
                settings.CredentialId = credential.Id;
                settings.BoundByUserId = ownerId;
            }

            await db.SaveChangesAsync(Ct);
        });

    private Task<Job> AddJobAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var job = new Job { Type = TestJobType, Payload = "{}", ProjectId = projectId, Status = JobStatus.Queued, RunAfter = T0, CreatedAt = T0 };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job;
        });

    private const string TestJobType = "test.agent_run";

    /// <summary>A worker that runs the spec generator for <see cref="TestJobType"/> jobs (phase 7 registers the real handlers).</summary>
    private static JobWorker Worker(WebApplicationFactory<Program> app, TimeProvider clock) => new(
        app.Services.GetRequiredService<IServiceScopeFactory>(),
        [new JobHandlerRegistration(TestJobType, sp => new SpecJobHandler(sp.GetRequiredService<AgentRunner>()))],
        Options.Create(new JobOptions { Enabled = true, Concurrency = 1 }),
        clock,
        app.Services.GetRequiredService<ILogger<JobWorker>>());

    private Task<AgentRun> LoadRunAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == id, Ct));

    private Task<long> TokensUsedAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == projectId).Select(p => p.TokensUsed).SingleAsync(Ct));

    private Task<List<string>> RunEventsAsync(Guid projectId)
    {
        var types = EventTypes.InCategory(EventCategory.Run).ToArray();
        return ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && types.Contains(e.Type))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Type)
            .ToListAsync(Ct));
    }

    private sealed class SpecJobHandler(AgentRunner runner) : IJobHandler
    {
        public Task HandleAsync(JobExecution job, CancellationToken cancellationToken) =>
            runner.RunAsync(Spec(job.ProjectId!.Value, job), cancellationToken);
    }

    private sealed class CountingResponder(AgentKind agent, string text) : IFakeAgentResponder
    {
        private int _calls;

        public AgentKind Agent => agent;

        public int Calls => _calls;

        public LlmRequest? LastRequest { get; private set; }

        public string Respond(LlmRequest request)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return text;
        }
    }

    private sealed record SentRequest(string Uri, Dictionary<string, string> Headers, string Body);

    /// <summary>Answers queued responses in order (503 when the queue is empty) and records every request.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(HttpStatusCode Status, string Body, Exception? Error)> _responses = new();

        public ConcurrentQueue<SentRequest> RequestLog { get; } = new();

        public IReadOnlyList<SentRequest> Requests => [.. RequestLog];

        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body, null));

        /// <summary>The next request throws <paramref name="error"/> (an exception the provider doesn't classify).</summary>
        public void EnqueueException(Exception error) => _responses.Enqueue((HttpStatusCode.InternalServerError, string.Empty, error));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestLog.Enqueue(new SentRequest(request.RequestUri!.ToString(), headers, body));
            var (status, text, error) = _responses.TryDequeue(out var next) ? next : (HttpStatusCode.ServiceUnavailable, "{}", null);
            if (error is not null)
            {
                throw error;
            }

            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;

        public DateTimeOffset Now
        {
            get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
