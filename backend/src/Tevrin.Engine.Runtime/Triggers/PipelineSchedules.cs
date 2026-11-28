using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Domain.Triggers;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;

namespace Tevrin.Engine.Runtime.Triggers;

/// <summary>FR-336, FR-337: the host's projects, as triggers need them (a fire or a call has no signed-in user).</summary>
public interface IProjectApprovalModes
{
    /// <summary>The project's approval mode (a triggered run's mode, FR-331); <c>null</c> when the project doesn't exist.</summary>
    Task<ApprovalMode?> FindAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>A new schedule (FR-336).</summary>
public sealed record CreateSchedule(Guid ProjectId, string PipelineId, string Cron, string Timezone, IReadOnlyDictionary<string, JsonElement>? Inputs, Guid? UserId);

/// <summary>A schedule change (FR-336): each field left <c>null</c> keeps its value.</summary>
public sealed record UpdateSchedule(bool? Paused, string? Cron, string? Timezone, IReadOnlyDictionary<string, JsonElement>? Inputs);

/// <summary>The payload of a <c>trigger.fire</c> job: the schedule and the due time it fires for.</summary>
public sealed record ScheduleFirePayload(Guid ScheduleId, DateTimeOffset DueAt)
{
    /// <summary>The schedule's pipeline when queued (for the event of a fire whose schedule was deleted since).</summary>
    public string? PipelineId { get; init; }

    public static ScheduleFirePayload? Parse(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ScheduleFirePayload>(json, JsonSerializerOptions.Web);
            return payload is { ScheduleId: var id } && id != Guid.Empty ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// FR-336, §4.71: project schedules. Creating or changing one checks the pipeline declares <c>trigger: { kind: schedule }</c>
/// (409 <c>TRIGGER_NOT_SUPPORTED</c>), the cron and timezone (400 <c>SCHEDULE_INVALID</c>) and the inputs as a run start
/// does. <see cref="QueueDueFiresAsync"/> claims due schedules and queues <c>trigger.fire</c>; <see cref="FireAsync"/> runs
/// one fire.
/// </summary>
public sealed partial class PipelineSchedules(
    IEngineDbContext db,
    PipelineRegistry registry,
    ArtifactSchemas schemas,
    PipelineRunStarter starter,
    EngineRunControl control,
    IProjectApprovalModes projects,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<PipelineSchedules> logger)
{
    /// <summary>The actor of trigger events (no user).</summary>
    public const string SystemActor = "tevrin-system";

    private const int ClaimBatch = 100;

    public Task<List<PipelineSchedule>> ListAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.PipelineSchedules.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Id).ToListAsync(cancellationToken);

    public Task<PipelineSchedule?> FindAsync(Guid projectId, Guid scheduleId, CancellationToken cancellationToken) =>
        db.PipelineSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId, cancellationToken);

    public async Task<Result<PipelineSchedule>> CreateAsync(CreateSchedule request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pipeline = TriggerChecks.Pipeline(registry, request.PipelineId, TriggerKind.Schedule);
        if (pipeline.IsFailure)
        {
            return Result.Failure<PipelineSchedule>(pipeline.Error!);
        }

        var now = time.GetUtcNow();
        var spec = ScheduleRules.Parse(request.Cron, request.Timezone, now);
        if (spec.IsFailure)
        {
            return Result.Failure<PipelineSchedule>(spec.Error!);
        }

        var inputs = TriggerChecks.Inputs(pipeline.Value, request.Inputs, schemas);
        if (inputs.IsFailure)
        {
            return Result.Failure<PipelineSchedule>(inputs.Error!);
        }

        var schedule = new PipelineSchedule
        {
            ProjectId = request.ProjectId,
            PipelineId = request.PipelineId,
            Cron = spec.Value.Cron.Expression,
            Timezone = request.Timezone,
            Inputs = inputs.Value,
            NextFireAt = ScheduleRules.NextAfterFire(spec.Value, now)!.Value,
            CreatedBy = request.UserId,
        };
        db.PipelineSchedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(schedule);
    }

    /// <summary>Applies <paramref name="change"/> to a tracked schedule. A new cron or timezone, or a resume, sets the next
    /// fire from now (a paused period is never caught up).</summary>
    public async Task<Result<PipelineSchedule>> UpdateAsync(PipelineSchedule schedule, UpdateSchedule change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(change);
        var now = time.GetUtcNow();
        var cron = change.Cron ?? schedule.Cron;
        var timezone = change.Timezone ?? schedule.Timezone;
        var spec = ScheduleRules.Parse(cron, timezone, now);
        if (spec.IsFailure)
        {
            return Result.Failure<PipelineSchedule>(spec.Error!);
        }

        if (change.Inputs is not null)
        {
            if (!registry.TryGet(schedule.PipelineId, out var loaded) || loaded.Definition is null)
            {
                return Result.Failure<PipelineSchedule>(new DomainError(EngineErrorCodes.TriggerNotSupported, "This pipeline can't be started that way."));
            }

            var inputs = TriggerChecks.Inputs(loaded, change.Inputs, schemas);
            if (inputs.IsFailure)
            {
                return Result.Failure<PipelineSchedule>(inputs.Error!);
            }

            schedule.Inputs = inputs.Value;
        }

        var resumed = change.Paused == false && schedule.Paused;
        var retimed = !string.Equals(cron.Trim(), schedule.Cron, StringComparison.Ordinal) || !string.Equals(timezone, schedule.Timezone, StringComparison.Ordinal);
        schedule.Cron = spec.Value.Cron.Expression;
        schedule.Timezone = timezone;
        schedule.Paused = change.Paused ?? schedule.Paused;
        if (resumed || retimed)
        {
            schedule.NextFireAt = ScheduleRules.NextAfterFire(spec.Value, now)!.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(schedule);
    }

    public async Task DeleteAsync(PipelineSchedule schedule, CancellationToken cancellationToken)
    {
        db.PipelineSchedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// §4.71: claims every due schedule and queues one <c>trigger.fire</c> for it. Each claim is a conditional update
    /// (<c>WHERE id = … AND next_fire_at = &lt;the due time read&gt; AND NOT paused</c>) committed with the job, so of two
    /// instances sweeping at once only one moves the schedule on and queues the fire. The next fire is the first due time
    /// after now: fires missed during downtime fire once. Returns how many fires were queued.
    /// </summary>
    public async Task<int> QueueDueFiresAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await db.PipelineSchedules.AsNoTracking()
            .Where(s => !s.Paused && s.NextFireAt <= now)
            .OrderBy(s => s.NextFireAt)
            .Take(ClaimBatch)
            .ToListAsync(cancellationToken);
        var queued = 0;
        foreach (var schedule in due)
        {
            try
            {
                if (await QueueDueFireAsync(schedule, now, cancellationToken))
                {
                    queued++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One bad schedule never stops the others, the sweeper or the host.
                db.ChangeTracker.Clear();
                LogClaimFailed(logger, schedule.Id, ex);
            }
        }

        return queued;
    }

    private async Task<bool> QueueDueFireAsync(PipelineSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spec = ScheduleRules.Parse(schedule.Cron, schedule.Timezone, now);
        if (spec.IsFailure)
        {
            // E.g. a timezone this host's tz database doesn't have: pause rather than retry every pass.
            LogUnusable(logger, schedule.Id, spec.Error!.Message);
            await db.PipelineSchedules.Where(s => s.Id == schedule.Id && s.NextFireAt == schedule.NextFireAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Paused, true), cancellationToken);
            return false;
        }

        return await ClaimAsync(schedule, ScheduleRules.NextAfterFire(spec.Value, now)!.Value, now, cancellationToken);
    }

    /// <summary>One claim: move the schedule on and queue its fire, atomically; <c>false</c> when another sweep won.</summary>
    public async Task<bool> ClaimAsync(PipelineSchedule schedule, DateTimeOffset next, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var dueAt = schedule.NextFireAt;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.PipelineSchedules
            .Where(s => s.Id == schedule.Id && s.NextFireAt == dueAt && !s.Paused)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextFireAt, next).SetProperty(x => x.LastFireAt, dueAt), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        jobs.Enqueue(EngineJobTypes.TriggerFire, new ScheduleFirePayload(schedule.Id, dueAt) { PipelineId = schedule.PipelineId }, schedule.ProjectId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogQueued(logger, schedule.Id, schedule.ProjectId, dueAt, next);
        return true;
    }

    /// <summary>
    /// FR-336: one fire. Starts a run with the schedule's inputs (<see cref="PipelineRunStarter"/>, no user). While a run is
    /// active the fire is skipped, or with <c>on_active: submit</c> and a trigger <c>input</c> the schedule has a value for,
    /// that value is submitted to the active run. Every fire is recorded as a <c>SCHEDULE_FIRED</c> event with its outcome;
    /// none fails the job (a fire is never retried into a second run). A schedule paused or deleted after its claim records
    /// <c>skipped</c> with a reason; when the project itself is gone there is nothing to record against (events belong to a
    /// project), so the fire is only logged.
    /// </summary>
    public async Task FireAsync(ScheduleFirePayload payload, Guid? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var schedule = await db.PipelineSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payload.ScheduleId, cancellationToken);
        var owner = schedule?.ProjectId ?? projectId;
        if (owner is not { } ownerId || await projects.FindAsync(ownerId, cancellationToken) is not { } mode)
        {
            LogProjectGone(logger, payload.ScheduleId);
            return;
        }

        if (schedule is null || schedule.Paused)
        {
            var reason = schedule is null ? "deleted" : "paused";
            await RecordAsync(ownerId, payload.ScheduleId, schedule?.PipelineId ?? payload.PipelineId, payload.DueAt,
                new { outcome = ScheduleFireOutcomes.Skipped, reason }, cancellationToken);
            LogStale(logger, payload.ScheduleId, reason);
            return;
        }

        var inputs = TriggerChecks.ParseInputs(schedule.Inputs);
        var started = await starter.StartAsync(new StartPipelineRun(schedule.ProjectId, schedule.PipelineId, null, mode, inputs, null), cancellationToken);
        db.ChangeTracker.Clear();
        string outcome;
        Guid? runId = null;
        string? errorCode = null;
        if (started.IsSuccess)
        {
            (outcome, runId) = (ScheduleFireOutcomes.Started, started.Value.Id);
        }
        else if (started.Error!.Code == EngineErrorCodes.RunAlreadyActive)
        {
            (outcome, runId, errorCode) = await OnActiveAsync(schedule, inputs, cancellationToken);
        }
        else
        {
            (outcome, errorCode) = (ScheduleFireOutcomes.Failed, started.Error.Code);
        }

        await RecordAsync(schedule.ProjectId, schedule.Id, schedule.PipelineId, payload.DueAt, new { outcome, runId, errorCode }, cancellationToken);
        LogFired(logger, schedule.Id, schedule.ProjectId, outcome, runId);
    }

    /// <summary>Writes the fire's <c>SCHEDULE_FIRED</c> event: <c>{ scheduleId, pipelineId, dueAt }</c> plus <paramref name="result"/>.</summary>
    private async Task RecordAsync(Guid projectId, Guid scheduleId, string? pipelineId, DateTimeOffset dueAt, object result, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToNode(new { scheduleId, pipelineId, dueAt }, JsonSerializerOptions.Web)!.AsObject();
        foreach (var (key, value) in JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!.AsObject().ToList())
        {
            payload[key] = value?.DeepClone();
        }

        db.Events.Add(new AuditEvent
        {
            ProjectId = projectId,
            Type = TriggerEventTypes.ScheduleFired,
            Payload = payload.ToJsonString(),
            Actor = SystemActor,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string Outcome, Guid? RunId, string? ErrorCode)> OnActiveAsync(
        PipelineSchedule schedule, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(schedule.PipelineId, out var loaded) || loaded.Definition is not { } definition
            || definition.TriggerOf(TriggerKind.Schedule) is not { OnActive: TriggerOnActive.Submit, Input: { } key }
            || !inputs.TryGetValue(key, out var value)
            || await TriggerChecks.ActiveRunAsync(db, schedule.ProjectId, schedule.PipelineId, cancellationToken) is not { } run)
        {
            return (ScheduleFireOutcomes.Skipped, null, null);
        }

        var submitted = await control.SubmitInputAsync(run, key, value, null, cancellationToken);
        db.ChangeTracker.Clear();
        return submitted.IsSuccess
            ? (ScheduleFireOutcomes.Submitted, run.Id, null)
            : (ScheduleFireOutcomes.Failed, run.Id, submitted.Error!.Code);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} of project {ProjectId}: fire due {DueAt} queued; next {NextFireAt}.")]
    private static partial void LogQueued(ILogger logger, Guid scheduleId, Guid projectId, DateTimeOffset dueAt, DateTimeOffset nextFireAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} of project {ProjectId} fired: {Outcome} (run {RunId}).")]
    private static partial void LogFired(ILogger logger, Guid scheduleId, Guid projectId, string outcome, Guid? runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} was {Reason} after its fire was queued: skipped.")]
    private static partial void LogStale(ILogger logger, Guid scheduleId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId}: its project is gone; the queued fire does nothing.")]
    private static partial void LogProjectGone(ILogger logger, Guid scheduleId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule {ScheduleId} couldn't be claimed; the sweep continues with the others.")]
    private static partial void LogClaimFailed(ILogger logger, Guid scheduleId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} can't be evaluated ({Reason}); it is paused.")]
    private static partial void LogUnusable(ILogger logger, Guid scheduleId, string reason);
}

/// <summary>FR-336: the <c>trigger.fire</c> job handler.</summary>
public sealed class TriggerFireJobHandler(PipelineSchedules schedules) : IJobHandler
{
    public Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ScheduleFirePayload.Parse(job.Payload) is { } payload
            ? schedules.FireAsync(payload, job.ProjectId, cancellationToken)
            : throw new JobFailedException("The trigger.fire payload is not valid.", transient: false, EngineErrorCodes.Validation);
    }
}

/// <summary>FR-336, FR-337: checks shared by schedules and webhooks.</summary>
public static class TriggerChecks
{
    /// <summary>The engine-controlled pipeline declaring <paramref name="kind"/>: else <c>NOT_FOUND</c>,
    /// <c>RUN_CONTROLLED_BY_HOST</c> or <c>TRIGGER_NOT_SUPPORTED</c>.</summary>
    public static Result<LoadedPipelineDefinition> Pipeline(PipelineRegistry registry, string pipelineId, TriggerKind kind)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!registry.TryGet(pipelineId, out var loaded) || loaded.Definition is not { } definition)
        {
            return Result.Failure<LoadedPipelineDefinition>(new DomainError(EngineErrorCodes.NotFound, $"Pipeline '{pipelineId}' was not found."));
        }

        if (loaded.RunControl == RunControl.Host)
        {
            return Result.Failure<LoadedPipelineDefinition>(RunControlErrors.ControlledByHost());
        }

        if (definition.TriggerOf(kind) is not { } trigger || (kind == TriggerKind.Webhook && trigger.Input is null))
        {
            return Result.Failure<LoadedPipelineDefinition>(new DomainError(EngineErrorCodes.TriggerNotSupported, "This pipeline can't be started that way."));
        }

        return Result.Success(loaded);
    }

    /// <summary>Checks <paramref name="inputs"/> as a run start does (<c>VALIDATION</c> for an undeclared key,
    /// <c>INPUT_INVALID</c> for a schema failure) and returns them as a JSON object.</summary>
    public static Result<string> Inputs(LoadedPipelineDefinition loaded, IReadOnlyDictionary<string, JsonElement>? inputs, ArtifactSchemas schemas)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in inputs ?? new Dictionary<string, JsonElement>())
        {
            if (loaded.Definition!.FindArtifact(key) is not { Input: true } artifact)
            {
                return Result.Failure<string>(new DomainError(EngineErrorCodes.Validation, $"'{key}' is not an input of this pipeline.") { Field = $"inputs.{key}" });
            }

            var content = RunInputs.Content(artifact, value, loaded.PackId, schemas);
            if (content.IsFailure)
            {
                return Result.Failure<string>(content.Error!);
            }

            values[key] = value;
        }

        return Result.Success(JsonSerializer.Serialize(values, JsonSerializerOptions.Web));
    }

    /// <summary>Stored inputs (a JSON object) back as values by key; empty when unreadable.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ParseInputs(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>The project's newest non-final run of the pipeline (tracked), or <c>null</c>.</summary>
    public static Task<PipelineRun?> ActiveRunAsync(IEngineDbContext db, Guid projectId, string pipelineId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PipelineRuns
            .Where(r => r.ProjectId == projectId && r.PipelineId == pipelineId
                && r.Status != GenericRunStatus.Completed && r.Status != GenericRunStatus.Failed && r.Status != GenericRunStatus.Cancelled)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
