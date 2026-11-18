using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Tests.Jobs;

/// <summary>
/// FR-272: job types are the engine's constants plus the types a pack registers, and only a registered type can be
/// enqueued. No database: the enqueuer only adds rows to the unit of work (the context is never connected). The
/// one-per-project query against saved jobs is covered by the integration tests (NFR-REL, §4.9).
/// </summary>
public sealed class JobEnqueuerTests : IDisposable
{
    private const string Known = "pack_job";
    private const string OnePerProject = "pack_exclusive";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly OfflineEngineDbContext _db = new();
    private readonly CapturingLogger _logger = new();
    private readonly JobEnqueuer _enqueuer;

    public JobEnqueuerTests()
    {
        var types = new JobTypeRegistry(
        [
            new JobHandlerRegistration(Known, _ => throw new NotSupportedException()),
            new JobHandlerRegistration(OnePerProject, _ => throw new NotSupportedException(), OnePerProject: true),
        ]);
        _enqueuer = new JobEnqueuer(_db, TimeProvider.System, new RawSerializer(), types, _logger);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void FR272_engine_job_types_are_the_four_engine_constants()
    {
        Assert.Equal(["stage.run", "agent.run", "gate.timer", "trigger.fire"], EngineJobTypes.All);
        Assert.Equal(
            (EngineJobTypes.StageRun, EngineJobTypes.AgentRun, EngineJobTypes.GateTimer, EngineJobTypes.TriggerFire),
            ("stage.run", "agent.run", "gate.timer", "trigger.fire"));
    }

    [Fact]
    public void FR272_a_registered_type_enqueues_a_queued_job_in_the_unit_of_work()
    {
        var projectId = Guid.NewGuid();

        var job = _enqueuer.Enqueue(Known, new { projectId }, projectId, Now);

        Assert.Equal((Known, JobStatus.Queued, 0, projectId, Now, Now), (job.Type, job.Status, job.Attempts, job.ProjectId, job.RunAfter, job.CreatedAt));
        Assert.Equal(EntityState.Added, _db.Entry(job).State);
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public async Task FR272_a_registered_type_enqueues_through_EnqueueAsync_too()
    {
        var job = await _enqueuer.EnqueueAsync(Known, new { }, null, TestContext.Current.CancellationToken, Now);

        Assert.Equal((Known, EntityState.Added), (job.Type, _db.Entry(job).State));
    }

    [Theory]
    [InlineData("unknown_job")]
    [InlineData(EngineJobTypes.GateTimer)] // reserved, with no handler in 0.7.0 (D-0.7.0-15)
    public async Task FR272_an_unregistered_type_logs_an_error_naming_it_and_throws_at_enqueue(string type)
    {
        var sync = Assert.Throws<InvalidOperationException>(() => _enqueuer.Enqueue(type, new { }, null, Now));
        var async = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _enqueuer.EnqueueAsync(type, new { }, Guid.NewGuid(), TestContext.Current.CancellationToken, Now));

        Assert.All([sync.Message, async.Message], m => Assert.Contains($"'{type}'", m, StringComparison.Ordinal));
        Assert.Equal(2, _logger.Errors.Count);
        Assert.All(_logger.Errors, e => Assert.Contains(type, e, StringComparison.Ordinal));
        Assert.Empty(_db.ChangeTracker.Entries<Job>());
    }

    [Fact]
    public void FR272_a_one_per_project_type_must_go_through_EnqueueAsync()
    {
        Assert.Throws<InvalidOperationException>(() => _enqueuer.Enqueue(OnePerProject, new { }, Guid.NewGuid(), Now));
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public void FR272_the_registry_knows_the_registered_types_and_which_are_one_per_project()
    {
        var registry = new JobTypeRegistry(
        [
            new JobHandlerRegistration("a", _ => throw new NotSupportedException()),
            new JobHandlerRegistration("b", _ => throw new NotSupportedException(), OnePerProject: true),
        ]);

        Assert.Equal(["a", "b"], registry.Types.Order(StringComparer.Ordinal));
        Assert.Equal((true, true, false), (registry.IsRegistered("a"), registry.IsRegistered("b"), registry.IsRegistered("c")));
        Assert.Equal((false, true, false), (registry.IsOnePerProject("a"), registry.IsOnePerProject("b"), registry.IsOnePerProject("c")));
    }

    private sealed class RawSerializer : IJobPayloadSerializer
    {
        public string Serialize(object payload) => System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private sealed class CapturingLogger : ILogger<JobEnqueuer>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
