using System.Diagnostics.CodeAnalysis;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Domain.Triggers;

/// <summary>A parsed, valid schedule: its cron and IANA timezone (FR-336).</summary>
public sealed record ScheduleSpec(CronSchedule Cron, TimeZoneInfo Zone);

/// <summary>FR-336, §4.71: pure rules for pipeline schedules.</summary>
public static class ScheduleRules
{
    /// <summary>
    /// Checks a cron and an IANA timezone (D-0.8.0-11: <see cref="TimeZoneInfo"/>, IANA ids only). A cron that never fires
    /// within <see cref="CronSchedule.SearchYears"/> is invalid. Failure: <c>SCHEDULE_INVALID</c> with the field.
    /// </summary>
    public static Result<ScheduleSpec> Parse(string? cron, string? timezone, DateTimeOffset now)
    {
        if (!CronSchedule.TryParse(cron, out var schedule, out var error))
        {
            return Result.Failure<ScheduleSpec>(Invalid("cron", error));
        }

        if (!TryZone(timezone, out var zone))
        {
            return Result.Failure<ScheduleSpec>(Invalid("timezone", $"'{timezone}' is not an IANA timezone (e.g. Europe/London)."));
        }

        return schedule.NextOccurrence(now, zone) is null
            ? Result.Failure<ScheduleSpec>(Invalid("cron", "The cron expression never fires."))
            : Result.Success(new ScheduleSpec(schedule, zone));
    }

    /// <summary>An IANA timezone by id (<c>UTC</c> included); Windows ids are refused.</summary>
    public static bool TryZone(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
        {
            return false;
        }

        // On Windows the lookup also takes Windows ids ("GMT Standard Time"): only IANA ids are stored.
        if (!found.HasIanaId && !string.Equals(id, "UTC", StringComparison.Ordinal))
        {
            return false;
        }

        zone = found;
        return true;
    }

    /// <summary>
    /// §4.71 catch-up: a fire due at or before <paramref name="now"/> fires once, and the next fire is the first due time
    /// strictly after <paramref name="now"/>, so fires missed during downtime collapse into the one. <c>null</c>: never again.
    /// </summary>
    public static DateTimeOffset? NextAfterFire(ScheduleSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Cron.NextOccurrence(now, spec.Zone);
    }

    /// <summary>Whether a schedule is due at <paramref name="now"/>.</summary>
    public static bool IsDue(bool paused, DateTimeOffset nextFireAt, DateTimeOffset now) => !paused && nextFireAt <= now;

    private static DomainError Invalid(string field, string message) =>
        new(EngineErrorCodes.ScheduleInvalid, message) { Field = field };
}

/// <summary>FR-336: what a schedule fire did (the <c>outcome</c> of its <see cref="TriggerEventTypes.ScheduleFired"/> event).</summary>
public static class ScheduleFireOutcomes
{
    /// <summary>A new run started.</summary>
    public const string Started = "started";

    /// <summary>A run was still active and the trigger says <c>skip</c> (or has nothing to submit): no run.</summary>
    public const string Skipped = "skipped";

    /// <summary>A run was active and the schedule's input was submitted to it (<c>on_active: submit</c>).</summary>
    public const string Submitted = "submitted";

    /// <summary>The run couldn't start (e.g. the stored inputs no longer match their schema); see the error code.</summary>
    public const string Failed = "failed";
}

/// <summary>FR-336: audit event types (table <c>events</c>) the triggers write.</summary>
public static class TriggerEventTypes
{
    /// <summary>One schedule fire: <c>{ scheduleId, pipelineId, dueAt, outcome, runId?, errorCode? }</c>.</summary>
    public const string ScheduleFired = "SCHEDULE_FIRED";
}
