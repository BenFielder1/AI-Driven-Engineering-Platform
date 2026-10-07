namespace Aide.Domain.Gates;

/// <summary>What the gate-reminder worker should do for a gate right now.</summary>
public sealed record GateTimerActions(bool SendReminder, bool MarkOverdue)
{
    public static GateTimerActions Nothing { get; } = new(false, false);
}

/// <summary>FR-15 / D19: reminders after 24 h, <c>overdue</c> (with a notification) after 72 h. Nothing is decided automatically.</summary>
public static class GateTimers
{
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(72);

    public static GateTimerActions Evaluate(GateStatus status, DateTimeOffset createdAt, DateTimeOffset now, DateTimeOffset? remindedAt, bool overdue)
    {
        if (status != GateStatus.Pending)
        {
            return GateTimerActions.Nothing;
        }

        var age = now - createdAt;
        var markOverdue = !overdue && age >= OverdueAfter;
        var remind = !markOverdue && remindedAt is null && age >= ReminderAfter;
        return new GateTimerActions(remind, markOverdue);
    }
}
