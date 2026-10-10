using System.Collections.Concurrent;
using System.Diagnostics;
using Aide.Api.Agents;
using Aide.Api.Jobs;
using Aide.Api.Logging;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Aide.IntegrationTests.Logging;

/// <summary>NFR-OBS: structured logs (and spans) carry the project id, job id and run id they were written for.</summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class ObservabilityTests(AideApiFactory factory)
{
    private const string HandlerMessage = "Observability test handler ran";
    private const string SecretMessage = "secret-ish detail sk-do-not-trace";

    private readonly string _type = $"test.obs.{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NFR_OBS_Log_entries_written_by_a_job_handler_carry_job_id_type_attempt_and_project_id()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var job = await AddJobAsync(fixture.Project.Id);

        Assert.Equal(1, await NewWorker().RunOnceAsync(Ct));

        var entry = Assert.Single(factory.Logs.Entries, e => e.Message == $"{HandlerMessage} {job.Id}");
        Assert.Contains(entry.Scopes, kv => kv.Key == JobScopeKeys.JobId && Equals(kv.Value, job.Id));
        Assert.Contains(entry.Scopes, kv => kv.Key == JobScopeKeys.JobType && Equals(kv.Value, _type));
        Assert.Contains(entry.Scopes, kv => kv.Key == JobScopeKeys.Attempt && Equals(kv.Value, 1));
        Assert.Contains(entry.Scopes, kv => kv.Key == ProjectLogScope.ProjectIdKey && Equals(kv.Value, fixture.Project.Id));
    }

    [Fact]
    public async Task NFR_OBS_A_job_without_a_project_has_no_ProjectId_in_its_log_scope()
    {
        var job = await AddJobAsync(projectId: null);

        Assert.Equal(1, await NewWorker().RunOnceAsync(Ct));

        var entry = Assert.Single(factory.Logs.Entries, e => e.Message == $"{HandlerMessage} {job.Id}");
        Assert.Contains(entry.Scopes, kv => kv.Key == JobScopeKeys.JobId && Equals(kv.Value, job.Id));
        Assert.DoesNotContain(entry.Scopes, kv => kv.Key == ProjectLogScope.ProjectIdKey);
    }

    [Fact]
    public async Task NFR_OBS_A_job_span_is_tagged_with_job_and_project_ids()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var job = await AddJobAsync(fixture.Project.Id);
        var jobId = job.Id.ToString();
        using var spans = new SpanRecorder(AideTelemetry.SourceName);

        Assert.Equal(1, await NewWorker().RunOnceAsync(Ct));

        var span = await spans.WaitForSingleAsync(a => Equals(a.GetTagItem(AideTelemetry.JobIdTag), jobId));
        Assert.Equal(fixture.Project.Id.ToString(), span.GetTagItem(AideTelemetry.ProjectIdTag));
        Assert.Equal(_type, span.GetTagItem(AideTelemetry.JobTypeTag));
        Assert.Equal(1, span.GetTagItem(AideTelemetry.JobAttemptTag));
    }

    [Fact]
    public async Task NFR_OBS_A_succeeded_job_span_has_outcome_succeeded_and_is_not_an_error()
    {
        var job = await AddJobAsync(projectId: null);
        using var spans = new SpanRecorder(AideTelemetry.SourceName);

        Assert.Equal(1, await NewWorker().RunOnceAsync(Ct));

        var span = await JobSpanAsync(spans, job);
        Assert.Equal(JobOutcomes.Succeeded, span.GetTagItem(AideTelemetry.JobOutcomeTag));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Theory]
    [InlineData(false, JobOutcomes.Failed)]
    [InlineData(true, JobOutcomes.Retry)]
    public async Task NFR_OBS_A_failing_job_span_is_an_error_described_by_its_error_code_not_its_message(bool transient, string outcome)
    {
        var job = await AddJobAsync(projectId: null);
        using var spans = new SpanRecorder(AideTelemetry.SourceName);

        Assert.Equal(1, await NewWorker(new ThrowingHandler(new JobFailedException(SecretMessage, transient, ErrorCodes.ProviderError))).RunOnceAsync(Ct));

        var span = await JobSpanAsync(spans, job);
        Assert.Equal(outcome, span.GetTagItem(AideTelemetry.JobOutcomeTag));
        Assert.Equal((ActivityStatusCode.Error, ErrorCodes.ProviderError), (span.Status, span.StatusDescription));
        Assert.Equal(ErrorCodes.ProviderError, span.GetTagItem(AideTelemetry.ErrorCodeTag));
        AssertNoSecret(span);
    }

    [Fact]
    public async Task NFR_OBS_A_job_dead_lettered_by_a_handler_bug_is_an_error_span_named_by_the_exception_type()
    {
        var job = await AddJobAsync(projectId: null, attempts: RetryPolicy.Default.MaxAttempts - 1);
        using var spans = new SpanRecorder(AideTelemetry.SourceName);

        Assert.Equal(1, await NewWorker(new ThrowingHandler(new InvalidOperationException(SecretMessage))).RunOnceAsync(Ct));

        var span = await JobSpanAsync(spans, job);
        Assert.Equal(JobOutcomes.Dead, span.GetTagItem(AideTelemetry.JobOutcomeTag));
        Assert.Equal((ActivityStatusCode.Error, nameof(InvalidOperationException)), (span.Status, span.StatusDescription));
        AssertNoSecret(span);
    }

    [Fact]
    public async Task NFR_OBS_A_failed_agent_run_span_is_an_error_with_its_error_code_and_run_status()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var job = new JobExecution(Guid.CreateVersion7(), JobTypes.SpecGeneration, "{}", fixture.Project.Id, 1);
        using var spans = new SpanRecorder(AideTelemetry.SourceName);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
            var request = new AgentRunRequest { ProjectId = fixture.Project.Id, Agent = AgentKind.SpecGenerator, Job = job };
            var ex = await Assert.ThrowsAsync<JobFailedException>(() => runner.RunAsync(request, Ct));
            Assert.Equal(ErrorCodes.MissingInput, ex.ErrorCode);
        }

        var runId = AgentRunRules.RunId(job.Id, null).ToString();
        var span = await spans.WaitForSingleAsync(a => Equals(a.GetTagItem(AideTelemetry.RunIdTag), runId));
        Assert.Equal((ActivityStatusCode.Error, ErrorCodes.MissingInput), (span.Status, span.StatusDescription));
        Assert.Equal(AgentRunStatus.Failed.ToWire(), span.GetTagItem(AideTelemetry.RunStatusTag));
        Assert.Equal(ErrorCodes.MissingInput, span.GetTagItem(AideTelemetry.ErrorCodeTag));
    }

    [Fact]
    public async Task NFR_OBS_Log_entries_of_a_request_under_a_project_carry_the_project_id_and_user_id()
    {
        var app = WithRoutingLogs();
        using var fixture = await ProjectApi.FixtureAsync(app);

        using var response = await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();

        var path = $"{fixture.Path}/state";
        var entries = factory.Logs.Entries
            .Where(e => e.Scopes.Any(kv => kv.Key == RequestLogScope.RequestPathKey && Equals(kv.Value, path))
                && e.Category.StartsWith("Microsoft.AspNetCore.Routing.EndpointMiddleware", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Contains(e.Scopes, kv => kv.Key == ProjectLogScope.ProjectIdKey && Equals(kv.Value, fixture.Project.Id)));
        Assert.All(entries, e => Assert.Contains(e.Scopes, kv => kv.Key == "UserId" && Equals(kv.Value, fixture.Owner.User!.Id)));
    }

    [Theory]
    [InlineData("0198c2a4-6f00-7a00-8000-000000000001", true)]
    [InlineData("not-a-guid", false)]
    public async Task NFR_OBS_The_project_scope_reads_the_route_id_and_ignores_a_non_guid(string routeId, bool scoped)
    {
        var sink = new LogSink();
        var services = new ServiceCollection()
            .AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_ => new CapturingLoggerProvider(sink)))
            .BuildServiceProvider();
        await using (services)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<ObservabilityTests>();
            var pipeline = new ApplicationBuilder(services);
            pipeline.UseProjectLogScope();
            pipeline.Run(_ =>
            {
                LogInside(logger);
                return Task.CompletedTask;
            });
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = $"/api/projects/{routeId}/state";
            context.Request.RouteValues["id"] = routeId;

            await pipeline.Build()(context);
        }

        var entry = Assert.Single(sink.Entries, e => e.Message == "inside");
        if (scoped)
        {
            Assert.Contains(entry.Scopes, kv => kv.Key == ProjectLogScope.ProjectIdKey && Equals(kv.Value, Guid.Parse(routeId)));
        }
        else
        {
            Assert.DoesNotContain(entry.Scopes, kv => kv.Key == ProjectLogScope.ProjectIdKey);
        }
    }

    [Fact]
    public async Task NFR_OBS_The_request_span_under_a_project_is_tagged_with_the_project_id()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var spans = new SpanRecorder("Microsoft.AspNetCore");

        using var response = await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();

        // The server stops its request activity after the client has the response, and other tests in the
        // collection produce request spans concurrently: match this request's span by its project id and wait for it.
        var projectId = fixture.Project.Id.ToString();
        var span = await spans.WaitForSingleAsync(a => Equals(a.GetTagItem(AideTelemetry.ProjectIdTag), projectId));
        Assert.Equal(fixture.Owner.User!.Id.ToString(), span.GetTagItem(AideTelemetry.UserIdTag));
    }

    [Fact]
    public void NFR_OBS_The_JSON_console_formatter_includes_scopes()
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;

        Assert.True(options.IncludeScopes);
        Assert.True(options.UseUtcTimestamp);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Turns on the endpoint middleware's "Executing endpoint" logs, so a plain GET writes log entries.</summary>
    private WebApplicationFactory<Program> WithRoutingLogs() =>
        factory.WithWebHostBuilder(b => b.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Routing", "Information"));

    private static Task<System.Diagnostics.Activity> JobSpanAsync(SpanRecorder spans, Job job)
    {
        var jobId = job.Id.ToString();
        return spans.WaitForSingleAsync(a => Equals(a.GetTagItem(AideTelemetry.JobIdTag), jobId));
    }

    private static void AssertNoSecret(System.Diagnostics.Activity span)
    {
        Assert.DoesNotContain("sk-do-not-trace", span.StatusDescription ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("sk-do-not-trace", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(span.Events, e => e.Tags.Any(t => t.Value?.ToString()?.Contains("sk-do-not-trace", StringComparison.Ordinal) == true));
    }

    private JobWorker NewWorker(IJobHandler? handler = null)
    {
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();
        handler ??= new LoggingHandler(loggers.CreateLogger<ObservabilityTests>());
        return new JobWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            [new JobHandlerRegistration(_type, _ => handler)],
            Options.Create(new JobOptions { Enabled = true }),
            TimeProvider.System,
            loggers.CreateLogger<JobWorker>());
    }

    private Task<Job> AddJobAsync(Guid? projectId, int attempts = 0) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var job = new Job
            {
                Type = _type,
                Payload = "{}",
                ProjectId = projectId,
                Status = JobStatus.Queued,
                Attempts = attempts,
                RunAfter = now.AddMinutes(-1),
                CreatedAt = now.AddMinutes(-1),
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "inside")]
    private static partial void LogInside(ILogger logger);

    /// <summary>A handler that only writes a log entry with its own logger (scopes flow from the worker).</summary>
    private sealed partial class LoggingHandler(ILogger logger) : IJobHandler
    {
        public Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
        {
            LogRan(logger, job.Id);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = HandlerMessage + " {JobId}")]
        private static partial void LogRan(ILogger logger, Guid jobId);
    }

    /// <summary>A handler that always throws <paramref name="error"/>.</summary>
    private sealed class ThrowingHandler(Exception error) : IJobHandler
    {
        public Task HandleAsync(JobExecution job, CancellationToken cancellationToken) => Task.FromException(error);
    }

    /// <summary>Records the spans of one activity source that stop while it is alive.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<System.Diagnostics.Activity> _stopped = new();

        public SpanRecorder(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<System.Diagnostics.Activity> Stopped => [.. _stopped];

        /// <summary>
        /// Waits (bounded) until a stopped span matches <paramref name="match"/> and returns the only one. Spans can stop
        /// after the awaited call returns (the server ends its request activity after the client has the response).
        /// </summary>
        public async Task<System.Diagnostics.Activity> WaitForSingleAsync(Func<System.Diagnostics.Activity, bool> match)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (!Stopped.Any(match) && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            return Assert.Single(Stopped, a => match(a));
        }

        public void Dispose() => _listener.Dispose();
    }
}
