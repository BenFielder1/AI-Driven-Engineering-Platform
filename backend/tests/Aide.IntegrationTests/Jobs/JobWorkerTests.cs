using System.Collections.Concurrent;
using Aide.Api.Jobs;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.IntegrationTests.Jobs;

/// <summary>NFR-REL, §4.9: the PostgreSQL job worker (claiming, retries, dead letters, leases) and the outbox enqueuer.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class JobWorkerTests(AideApiFactory factory)
{
    /// <summary>Whole seconds, so stored timestamps (microsecond precision) compare exactly.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private readonly string _type = $"test.{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NFR_REL_A_claimed_job_runs_once_and_succeeds_with_job_and_project_ids_in_the_log_scope()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type, projectId: fixture.Project.Id, payload: """{"hello":"world"}""");
        var handler = new RecordingHandler();

        var processed = await NewWorker(clock, handler).RunOnceAsync(Ct);

        Assert.Equal(1, processed);
        var execution = Assert.Single(handler.Executions);
        Assert.Equal((job.Id, _type, (Guid?)fixture.Project.Id, 1), (execution.Id, execution.Type, execution.ProjectId, execution.Attempt));
        Assert.Equal("world", LifecycleSeed.Payload(execution.Payload).GetProperty("hello").GetString());

        var saved = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Succeeded, 1, (string?)null, (DateTimeOffset?)null, (DateTimeOffset?)T0), (saved.Status, saved.Attempts, saved.LockedBy, saved.LockedAt, saved.CompletedAt));

        var log = Assert.Single(factory.Logs.Entries, e => e.Message == "Job succeeded" && e.Scopes.Any(kv => kv.Key == "JobId" && Equals(kv.Value, job.Id)));
        Assert.Contains(log.Scopes, kv => kv.Key == "ProjectId" && Equals(kv.Value, fixture.Project.Id));
    }

    [Fact]
    public async Task NFR_REL_Two_workers_never_run_the_same_job()
    {
        var clock = new TestClock(T0);
        var ids = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            ids.Add((await AddJobAsync(_type)).Id);
        }

        var handler = new RecordingHandler(TimeSpan.FromMilliseconds(20));
        var first = NewWorker(clock, handler, concurrency: 5);
        var second = NewWorker(clock, handler, concurrency: 5);
        while (true)
        {
            var processed = await Task.WhenAll(first.RunOnceAsync(Ct), second.RunOnceAsync(Ct));
            if (processed.Sum() == 0)
            {
                break;
            }
        }

        Assert.Equal(ids.Order(), handler.Executions.Select(e => e.Id).Order());
        var jobs = await JobsOfTypeAsync(_type);
        Assert.All(jobs, j => Assert.Equal((JobStatus.Succeeded, 1), (j.Status, j.Attempts)));
    }

    [Fact]
    public async Task NFR_REL_Claiming_skips_rows_locked_by_another_worker()
    {
        var clock = new TestClock(T0);
        var locked = await AddJobAsync(_type);
        var free = await AddJobAsync(_type);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM jobs WHERE id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", locked.Id);
            await command.ExecuteScalarAsync(Ct);
        }

        var handler = new RecordingHandler();
        var run = NewWorker(clock, handler).RunOnceAsync(Ct);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20), Ct));

        Assert.Same(run, finished); // not blocked by the locked row
        Assert.Equal([free.Id], handler.Executions.Select(e => e.Id));
        await transaction.RollbackAsync(Ct);
        Assert.Equal(JobStatus.Queued, (await LoadAsync(locked.Id)).Status);
    }

    [Fact]
    public async Task S4_14_NFR_REL_A_GitHub_rate_limit_defers_the_job_until_the_reset_without_counting_the_attempt()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var worker = NewWorker(clock, new RecordingHandler(fail: () => new Aide.Infrastructure.GitHub.GitHubRateLimitedException(clock.Now.AddMinutes(7))));

        // Far more attempts than the retry policy allows: none counts, so the job never dead-letters.
        for (var i = 0; i < 5; i++)
        {
            var reset = clock.Now.AddMinutes(7);
            Assert.Equal(1, await worker.RunOnceAsync(Ct));
            var saved = await LoadAsync(job.Id);
            Assert.Equal((JobStatus.Queued, 0, reset), (saved.Status, saved.Attempts, saved.RunAfter));
            Assert.StartsWith("GITHUB_RATE_LIMITED", saved.LastError, StringComparison.Ordinal);
            Assert.Equal(0, await worker.RunOnceAsync(Ct)); // waits for the reset
            clock.Now = reset;
        }
    }

    [Fact]
    public async Task NFR_REL_Transient_failures_retry_after_2_8_and_32_seconds_then_dead_letter()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var worker = NewWorker(clock, new RecordingHandler(fail: () => new InvalidOperationException("provider returned 503")));

        Assert.Equal(1, await worker.RunOnceAsync(Ct));
        var afterFirst = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Queued, 1, T0.AddSeconds(2)), (afterFirst.Status, afterFirst.Attempts, afterFirst.RunAfter));
        Assert.Contains("provider returned 503", afterFirst.LastError, StringComparison.Ordinal);

        clock.Now = T0.AddSeconds(1);
        Assert.Equal(0, await worker.RunOnceAsync(Ct)); // backoff not elapsed

        var expectedRunAfter = T0.AddSeconds(2);
        foreach (var (attempt, delay) in new[] { (2, 8), (3, 32) })
        {
            clock.Now = expectedRunAfter;
            Assert.Equal(1, await worker.RunOnceAsync(Ct));
            expectedRunAfter = clock.Now.AddSeconds(delay);
            var saved = await LoadAsync(job.Id);
            Assert.Equal((JobStatus.Queued, attempt, expectedRunAfter), (saved.Status, saved.Attempts, saved.RunAfter));
        }

        clock.Now = expectedRunAfter;
        Assert.Equal(1, await worker.RunOnceAsync(Ct));
        var dead = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Dead, 4, (DateTimeOffset?)expectedRunAfter), (dead.Status, dead.Attempts, dead.CompletedAt));

        clock.Now = expectedRunAfter.AddHours(1);
        Assert.Equal(0, await worker.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task NFR_REL_A_non_transient_failure_fails_at_once()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var worker = NewWorker(clock, new RecordingHandler(fail: () => new JobFailedException("No approved project request.", transient: false, "MISSING_INPUT")));

        await worker.RunOnceAsync(Ct);

        var saved = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Failed, 1, "MISSING_INPUT: No approved project request."), (saved.Status, saved.Attempts, saved.LastError));
    }

    [Fact]
    public async Task NFR_REL_An_expired_lease_is_requeued_and_counts_as_an_attempt()
    {
        var clock = new TestClock(T0);
        var expired = await AddJobAsync(_type, JobStatus.Running, attempts: 1, lockedAt: T0.AddMinutes(-5));
        var exhausted = await AddJobAsync(_type, JobStatus.Running, attempts: 4, lockedAt: T0.AddMinutes(-30));
        var live = await AddJobAsync(_type, JobStatus.Running, attempts: 1, lockedAt: T0.AddMinutes(-4));
        var handler = new RecordingHandler();

        Assert.Equal(0, await NewWorker(clock, handler).RunOnceAsync(Ct));

        var requeued = await LoadAsync(expired.Id);
        Assert.Equal((JobStatus.Queued, 1, T0.AddSeconds(2), (string?)null), (requeued.Status, requeued.Attempts, requeued.RunAfter, requeued.LockedBy));
        Assert.Contains("lease", requeued.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobStatus.Dead, (await LoadAsync(exhausted.Id)).Status);
        Assert.Equal(JobStatus.Running, (await LoadAsync(live.Id)).Status);

        // After the backoff it runs again as attempt 2.
        clock.Now = T0.AddSeconds(2);
        Assert.Equal(1, await NewWorker(clock, handler).RunOnceAsync(Ct));
        Assert.Equal(2, Assert.Single(handler.Executions).Attempt);
    }

    [Fact]
    public async Task NFR_REL_Job_types_without_a_handler_stay_queued()
    {
        var clock = new TestClock(T0);
        var unregistered = await AddJobAsync($"unregistered.{Guid.NewGuid():N}");
        var mine = await AddJobAsync(_type);
        var handler = new RecordingHandler();

        Assert.Equal(1, await NewWorker(clock, handler).RunOnceAsync(Ct));

        Assert.Equal([mine.Id], handler.Executions.Select(e => e.Id));
        var saved = await LoadAsync(unregistered.Id);
        Assert.Equal((JobStatus.Queued, 0, (string?)null), (saved.Status, saved.Attempts, saved.LockedBy));
    }

    [Fact]
    public async Task NFR_REL_At_most_one_execution_job_per_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var enqueuer = scope.ServiceProvider.GetRequiredService<JobEnqueuer>();
            var first = await enqueuer.EnqueueAsync(JobTypes.Execution, new { projectId = id }, id, Ct);
            var again = await enqueuer.EnqueueAsync(JobTypes.Execution, new { projectId = id }, id, Ct);
            Assert.Same(first, again); // same unit of work
            await scope.ServiceProvider.GetRequiredService<AideDbContext>().SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var enqueuer = scope.ServiceProvider.GetRequiredService<JobEnqueuer>();
            await enqueuer.EnqueueAsync(JobTypes.Execution, new { projectId = id }, id, Ct); // already queued: no new row
            await scope.ServiceProvider.GetRequiredService<AideDbContext>().SaveChangesAsync(Ct);
        }

        Assert.Single(await LifecycleSeed.JobsAsync(factory, id));

        // The database enforces it too (partial unique index), whatever writes the row.
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => AddJobAsync(JobTypes.Execution, projectId: id));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);

        // Once it has finished, the next execution job can be queued.
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Succeeded), Ct));
        await AddJobAsync(JobTypes.Execution, projectId: id);
        Assert.Equal(2, (await LifecycleSeed.JobsAsync(factory, id)).Count);
    }

    [Fact]
    public async Task NFR_REL_A_slow_job_does_not_block_another_job_from_being_claimed_and_completed()
    {
        var clock = new TestClock(T0);
        var slow = await AddJobAsync(_type);
        var handler = new BlockingHandler(slow.Id);
        using var worker = NewWorker(clock, handler, concurrency: 2, pollInterval: TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(Ct);
        try
        {
            await handler.Started.WaitAsync(TimeSpan.FromSeconds(20), Ct);

            // Queued after the slow job was claimed: picked up by the free slot while the slow job still runs.
            var fast = await AddJobAsync(_type);
            await WaitUntilAsync(async () => (await LoadAsync(fast.Id)).Status == JobStatus.Succeeded);
            Assert.Equal(JobStatus.Running, (await LoadAsync(slow.Id)).Status);

            handler.Release();
            await WaitUntilAsync(async () => (await LoadAsync(slow.Id)).Status == JobStatus.Succeeded);
        }
        finally
        {
            handler.Release();
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, handler.Completed.Count);
    }

    [Fact]
    public async Task NFR_REL_Shutdown_hands_a_running_job_back_without_counting_the_attempt()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var handler = new BlockingHandler(job.Id);
        using var worker = NewWorker(clock, handler, pollInterval: TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(Ct);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(handler.Cancelled);
        var saved = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Queued, 0, (string?)null), (saved.Status, saved.Attempts, saved.LockedBy));
    }

    [Fact]
    public async Task NFR_REL_A_long_running_job_renews_its_lease_so_the_reaper_does_not_requeue_it()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var handler = new BlockingHandler(job.Id);
        var lease = TimeSpan.FromMilliseconds(300);
        using var worker = NewWorker(clock, handler, leaseDuration: lease);

        var run = worker.RunOnceAsync(Ct);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Far past the original lease: without renewal the reaper would re-queue it.
        clock.Now = T0.AddMinutes(10);
        await WaitUntilAsync(async () => (await LoadAsync(job.Id)).LockedAt == clock.Now);
        Assert.Equal(0, await NewWorker(clock, new RecordingHandler(), leaseDuration: lease).RunOnceAsync(Ct));

        var running = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Running, worker.WorkerId, 1), (running.Status, running.LockedBy, running.Attempts));

        handler.Release();
        Assert.Equal(1, await run);
        var saved = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Succeeded, 1, (string?)null), (saved.Status, saved.Attempts, saved.LastError));
        Assert.Equal([job.Id], handler.Completed);
    }

    [Fact]
    public async Task NFR_REL_A_job_whose_lease_was_lost_is_cancelled_and_its_outcome_not_recorded()
    {
        var clock = new TestClock(T0);
        var job = await AddJobAsync(_type);
        var handler = new BlockingHandler(job.Id);
        using var worker = NewWorker(clock, handler, leaseDuration: TimeSpan.FromMilliseconds(300));

        var run = worker.RunOnceAsync(Ct);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Reaped and claimed by another worker in the meantime.
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedBy, "other-worker").SetProperty(j => j.Attempts, 2), Ct));

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.True(handler.Cancelled);
        var saved = await LoadAsync(job.Id);
        Assert.Equal((JobStatus.Running, "other-worker", 2), (saved.Status, saved.LockedBy, saved.Attempts));
    }

    private JobWorker NewWorker(
        TimeProvider clock, IJobHandler handler, int concurrency = 4, TimeSpan? pollInterval = null, TimeSpan? leaseDuration = null) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        [new JobHandlerRegistration(_type, _ => handler)],
        Options.Create(new JobOptions
        {
            Enabled = true,
            Concurrency = concurrency,
            PollInterval = pollInterval ?? TimeSpan.FromSeconds(2),
            LeaseDuration = leaseDuration ?? JobPolicy.LeaseDuration,
        }),
        clock,
        factory.Services.GetRequiredService<ILogger<JobWorker>>());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(25, Ct);
        }
    }

    private Task<Job> AddJobAsync(
        string type, JobStatus status = JobStatus.Queued, int attempts = 0, DateTimeOffset? lockedAt = null, Guid? projectId = null, string payload = "{}") =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var job = new Job
            {
                Type = type,
                Payload = payload,
                ProjectId = projectId,
                Status = status,
                Attempts = attempts,
                RunAfter = T0.AddMinutes(-1),
                LockedBy = lockedAt is null ? null : "dead-worker",
                LockedAt = lockedAt,
                CreatedAt = T0.AddMinutes(-1),
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job;
        });

    private Task<Job> LoadAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));

    private Task<List<Job>> JobsOfTypeAsync(string type) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().Where(j => j.Type == type).ToListAsync(Ct));

    /// <summary>A settable clock; its timers are real (the worker's poll and lease-renewal delays).</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = now;

        public DateTimeOffset Now
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }

            set
            {
                lock (_gate)
                {
                    _now = value;
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Holds the job <paramref name="blockedJobId"/> until <see cref="Release"/> (or cancellation); others finish at once.</summary>
    private sealed class BlockingHandler(Guid blockedJobId) : IJobHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Guid> _completed = new();
        private volatile bool _cancelled;

        public Task Started => _started.Task;

        public bool Cancelled => _cancelled;

        public IReadOnlyList<Guid> Completed => [.. _completed];

        public void Release() => _release.TrySetResult();

        public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
        {
            if (job.Id == blockedJobId)
            {
                _started.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _cancelled = true;
                    throw;
                }
            }

            _completed.Enqueue(job.Id);
        }
    }

    private sealed class RecordingHandler(TimeSpan? delay = null, Func<Exception>? fail = null) : IJobHandler
    {
        private readonly ConcurrentQueue<JobExecution> _executions = new();

        public IReadOnlyList<JobExecution> Executions => [.. _executions];

        public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
        {
            _executions.Enqueue(job);
            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }

            if (fail is not null)
            {
                throw fail();
            }
        }
    }
}
