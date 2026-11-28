using System.Text;
using System.Text.Json;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>
/// FR-359, §4.73: the stored phase of a destructive call saved on its agent run (<c>agent_runs.tool_loop_state</c>). The
/// other §4.73 states are derived: <c>denied</c> is a rejected <c>tool_approval</c> gate, and <c>resumed</c> is the state
/// cleared from the agent run.
/// </summary>
public enum ToolApprovalPhase
{
    /// <summary>The state is saved and the job deferred; the gate is raised, or waits for the run's other one.</summary>
    PendingApproval = 0,

    /// <summary>The gate was approved and the call claimed (committed before the tool is invoked): it never runs again.</summary>
    Approved = 1,
}

/// <summary>FR-359, §4.73: what one delivery of a job does with a saved destructive call (<see cref="ToolApprovalRules.Next"/>).</summary>
public enum ToolApprovalStep
{
    /// <summary>No gate yet (another call of the run held the one pending <c>tool_approval</c>): raise it now.</summary>
    RaiseGate = 0,

    /// <summary>The gate is pending within its timer: defer again, with no model call.</summary>
    Wait = 1,

    /// <summary>The gate is still pending at its timer: it is rejected by <see cref="ToolApprovalRules.TimerUser"/>.</summary>
    TimeOut = 2,

    /// <summary>The gate was approved: claim the call, then run the tool once.</summary>
    Run = 3,

    /// <summary>The gate was rejected: the model gets the denial text; the tool never runs.</summary>
    Deny = 4,

    /// <summary>An earlier delivery claimed the call and stopped before resuming: at most once, so it isn't run again.</summary>
    AlreadyClaimed = 5,

    /// <summary>The gate was cancelled (the run was cancelled or rolled back) or is gone: the tool never runs.</summary>
    Closed = 6,
}

/// <summary>
/// FR-359, §4.73: the pure rules of approval in the middle of a stage. A destructive call is saved with the tool loop's
/// messages, a <c>tool_approval</c> gate is raised and the job deferred; the next delivery after the decision runs the tool
/// once (approve) or sends <see cref="DenialText"/> (reject, or no decision within <see cref="GateTimeout"/>), then the loop
/// resumes from the saved messages. No approval mode skips the gate: nothing here, nor in the runner, reads the run's
/// approval mode (entry gates follow their stage's <c>modes</c>; this gate never does).
/// </summary>
public static class ToolApprovalRules
{
    /// <summary>FR-359: a <c>tool_approval</c> gate with no decision this long after it was raised counts as rejected.</summary>
    public static readonly TimeSpan GateTimeout = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a waiting job sleeps before it checks its gate again (with no model call): short, so a decision saved while
    /// the job was still deferring itself is picked up within this, not at the queue's longest deferral.
    /// </summary>
    public static readonly TimeSpan WaitPoll = TimeSpan.FromMinutes(2);

    /// <summary>§4.73, D-0.7.0-47: the cap on the saved state, in UTF-8 bytes (as stored inputs).</summary>
    public const int MaxStateBytes = AgentRunInput.MaxBytes;

    /// <summary>Who rejects a gate whose timer ran out.</summary>
    public const string TimerUser = "system";

    /// <summary>The comment recorded on a gate its timer rejected.</summary>
    public const string TimeoutComment = "no decision within 24 hours";

    /// <summary>
    /// The error result of a call the model made in the same turn after the destructive one: it isn't run on resume (its
    /// arguments were saved redacted, and nobody approved them).
    /// </summary>
    public const string NotRunAfterPause =
        "Not run: the turn was paused for approval of an earlier call. Call it again if it is still needed.";

    /// <summary>The longest reason shown on the gate.</summary>
    public const int MaxReasonLength = 2000;

    /// <summary>The error result of a claimed call whose delivery stopped before the loop resumed (never run twice).</summary>
    public const string OutcomeUnknown =
        "The approved call was started, but the run stopped before its result was saved; it was not run again, so its outcome is unknown.";

    /// <summary>
    /// §4.73: what a delivery does with a saved call in <paramref name="phase"/>. <paramref name="gateRaised"/>: the state
    /// names a gate; <paramref name="gateStatus"/> and <paramref name="gateCreatedAt"/> are that gate's (<c>null</c>: gone).
    /// </summary>
    public static ToolApprovalStep Next(
        ToolApprovalPhase phase, bool gateRaised, GateStatus? gateStatus, DateTimeOffset? gateCreatedAt, DateTimeOffset now)
    {
        if (phase == ToolApprovalPhase.Approved)
        {
            return ToolApprovalStep.AlreadyClaimed;
        }

        if (!gateRaised)
        {
            return ToolApprovalStep.RaiseGate;
        }

        return gateStatus switch
        {
            GateStatus.Pending when gateCreatedAt is { } raised && now >= DeadlineOf(raised) => ToolApprovalStep.TimeOut,
            GateStatus.Pending => ToolApprovalStep.Wait,
            GateStatus.Approved => ToolApprovalStep.Run,
            GateStatus.Rejected => ToolApprovalStep.Deny,
            _ => ToolApprovalStep.Closed,
        };
    }

    /// <summary>When a gate raised at <paramref name="raisedAt"/> times out.</summary>
    public static DateTimeOffset DeadlineOf(DateTimeOffset raisedAt) => raisedAt + GateTimeout;

    /// <summary>When a job waiting at <paramref name="now"/> on a gate raised at <paramref name="raisedAt"/> runs again: after
    /// <see cref="WaitPoll"/>, or at the timer if that comes first.</summary>
    public static DateTimeOffset WakeAt(DateTimeOffset now, DateTimeOffset raisedAt)
    {
        var poll = now + WaitPoll;
        var deadline = DeadlineOf(raisedAt);
        return poll < deadline ? poll : deadline;
    }

    /// <summary>FR-359: the tool result the model gets for a rejected call: <c>denied by &lt;user&gt;: &lt;comment&gt;</c>.</summary>
    public static string DenialText(string user, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        return $"denied by {user}: {(string.IsNullOrWhiteSpace(comment) ? "no comment" : comment.Trim())}";
    }

    /// <summary>The denial text of a gate its timer rejected.</summary>
    public static string TimeoutDenial => DenialText(TimerUser, TimeoutComment);

    /// <summary>§4.73: whether the serialized state fits <see cref="MaxStateBytes"/>.</summary>
    public static bool FitsCap(string stateJson)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        return Encoding.UTF8.GetByteCount(stateJson) <= MaxStateBytes;
    }

    /// <summary>
    /// FR-359: the agent's stated reason for a call: a non-empty top-level <c>reason</c> string in its arguments, else the
    /// text the model wrote with the call, else <c>null</c>; trimmed and at most <see cref="MaxReasonLength"/> characters.
    /// (Redacting it is the caller's job, with the rest of the saved state.)
    /// </summary>
    public static string? ReasonOf(string? argumentsJson, string? assistantText)
    {
        var reason = ArgumentReason(argumentsJson) ?? (string.IsNullOrWhiteSpace(assistantText) ? null : assistantText.Trim());
        return reason is { Length: > MaxReasonLength } ? reason[..MaxReasonLength] : reason;
    }

    private static string? ArgumentReason(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(reason.GetString())
                    ? reason.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
