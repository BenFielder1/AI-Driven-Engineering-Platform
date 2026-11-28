using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>One saved message of the request (<c>user</c> or <c>assistant</c>).</summary>
public sealed record SavedToolLoopMessage(string Role, string Content);

/// <summary>One saved model turn: its text, its calls and the results of those that ran or were decided.</summary>
public sealed record SavedToolLoopTurn(string Text, IReadOnlyList<LlmToolCall> Calls, IReadOnlyList<LlmToolResult> Results);

/// <summary>
/// FR-359: the destructive call that waits: the provider's call id, the tool's registered name and the model's name for
/// it, its tier, its redacted arguments (the tool runs with exactly these, which the approver saw) and the agent's reason.
/// </summary>
public sealed record SavedToolCall(string CallId, string Tool, string Name, string Tier, string Arguments, string? Reason);

/// <summary>
/// FR-359, §4.73: the tool loop saved on an agent run (<c>agent_runs.tool_loop_state</c>) while a destructive call waits:
/// the request's system prompt and messages, the completed exchanges, the current turn (with the results of the calls
/// before the waiting one), the turn number, the waiting call, the tool calls recorded so far and the gate. Every text is
/// secret-redacted (<see cref="SecretRedaction"/>) and the JSON is at most <see cref="ToolApprovalRules.MaxStateBytes"/>.
/// The resumed loop sends these saved (redacted) messages.
/// </summary>
public sealed record ToolLoopState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public ToolApprovalPhase Phase { get; init; }

    /// <summary>The <c>tool_approval</c> gate; <c>null</c> until it is raised (the run's other one was pending).</summary>
    public Guid? GateId { get; init; }

    /// <summary>
    /// FR-360: the gate type the call waits on: <c>external_action</c> for an external tool (destructive or not);
    /// <c>null</c> (left out of the JSON, as in states saved before it) for <c>tool_approval</c>
    /// (<see cref="ToolLoopStates.GateTypeOf"/>).
    /// </summary>
    public string? GateType { get; init; }

    /// <summary>FR-359: the job that saved the call (it alone resumes it); a job that ends without succeeding closes it.</summary>
    public Guid? JobId { get; init; }

    /// <summary>When the approved call was claimed (just before it ran).</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    public required string System { get; init; }

    public required IReadOnlyList<SavedToolLoopMessage> Messages { get; init; }

    public required IReadOnlyList<SavedToolLoopTurn> Exchanges { get; init; }

    public required SavedToolLoopTurn Current { get; init; }

    public int Turn { get; init; }

    public required SavedToolCall Pending { get; init; }

    public IReadOnlyList<AgentToolCallRecord> Calls { get; init; } = [];
}

/// <summary>FR-359, §4.73: builds, reads and writes <see cref="ToolLoopState"/>, and the <c>tool_approval</c> gate payload.</summary>
public static class ToolLoopStates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The redacted state of a loop stopped at <paramref name="snapshot"/>'s waiting call to <paramref name="tool"/>, with
    /// <paramref name="secrets"/> (the run's key) masked as well as the usual credential patterns. Throws
    /// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> when the text is too much to redact in time.
    /// </summary>
    public static ToolLoopState Create(
        LlmRequest request, LlmToolLoopSnapshot snapshot, StageTool tool, IReadOnlyList<AgentToolCallRecord> calls, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(calls);
        string R(string text) => SecretRedaction.Redact(text, secrets);
        SavedToolLoopTurn Turn(LlmToolExchange exchange) => new(
            R(exchange.AssistantText),
            [.. exchange.Calls.Select(c => c with { ArgumentsJson = R(c.ArgumentsJson) })],
            [.. exchange.Results.Select(r => r with { Content = R(r.Content) })]);

        var waiting = snapshot.Waiting;
        var arguments = R(waiting.ArgumentsJson);
        var reason = ToolApprovalRules.ReasonOf(waiting.ArgumentsJson, snapshot.Current.AssistantText);
        return new ToolLoopState
        {
            Phase = ToolApprovalPhase.PendingApproval,
            System = R(request.System),
            Messages = [.. request.Messages.Select(m => new SavedToolLoopMessage(m.Role == LlmRole.Assistant ? "assistant" : "user", R(m.Content)))],
            Exchanges = [.. snapshot.Exchanges.Select(Turn)],
            Current = Turn(snapshot.Current),
            Turn = snapshot.Turn,
            Pending = new SavedToolCall(waiting.Id, tool.Name, waiting.Name, ToolTierWire(tool), arguments, reason is null ? null : R(reason)),
            Calls = calls,
            GateType = tool.External ? EngineGates.ExternalAction : null,
        };
    }

    /// <summary>FR-359, FR-360: the gate type a saved call waits on (<see cref="ToolLoopState.GateType"/>, else
    /// <c>tool_approval</c>).</summary>
    public static string GateTypeOf(ToolLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.GateType ?? EngineGates.ToolApproval;
    }

    private static string ToolTierWire(StageTool tool) => tool.Tier?.ToWire() ?? "unregistered";

    /// <summary>The stored JSON (snake_case enum values, no nulls).</summary>
    public static string Serialize(ToolLoopState state) => JsonSerializer.Serialize(state, Json);

    /// <summary>A stored state, or <c>null</c> when it can't be read (a newer version, or not this shape).</summary>
    public static ToolLoopState? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ToolLoopState>(json, Json);
            return state is { Version: ToolLoopState.CurrentVersion, Current.Calls.Count: > 0 } && state.Current.Results.Count < state.Current.Calls.Count
                && string.Equals(state.Current.Calls[state.Current.Results.Count].Id, state.Pending.CallId, StringComparison.Ordinal)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The waiting call as the model made it, with the saved (redacted) arguments.</summary>
    public static LlmToolCall WaitingCall(ToolLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Current.Calls[state.Current.Results.Count] with { ArgumentsJson = state.Pending.Arguments };
    }

    /// <summary>The saved request messages.</summary>
    public static IReadOnlyList<LlmMessage> Messages(ToolLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. state.Messages.Select(m => new LlmMessage(m.Role == "assistant" ? LlmRole.Assistant : LlmRole.User, m.Content))];
    }

    /// <summary>The calls of the saved turn after the waiting one (never run on resume).</summary>
    public static IReadOnlyList<LlmToolCall> CallsAfterWaiting(ToolLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. state.Current.Calls.Skip(state.Current.Results.Count + 1)];
    }

    /// <summary>
    /// The resume of the saved loop with <paramref name="decided"/> as the waiting call's result and
    /// <paramref name="after"/> as the results of the turn's later calls, so no call of the paused turn runs on resume.
    /// </summary>
    public static LlmToolLoopResume Resume(ToolLoopState state, LlmToolResult decided, IReadOnlyList<LlmToolResult> after)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(decided);
        ArgumentNullException.ThrowIfNull(after);
        var current = new LlmToolExchange(state.Current.Text, state.Current.Calls, [.. state.Current.Results, decided, .. after]);
        return new LlmToolLoopResume([.. state.Exchanges.Select(e => new LlmToolExchange(e.Text, e.Calls, e.Results))], current, state.Turn);
    }

    /// <summary>
    /// FR-359: the <c>tool_approval</c> gate payload: <c>{ tool, tier, arguments, reason, agentRunId }</c>, from the
    /// redacted state. FR-360: an external tool's <c>external_action</c> gate has the same payload; its
    /// <c>arguments</c> are exactly what the tool will be called with.
    /// </summary>
    public static string GatePayload(ToolLoopState state, Guid agentRunId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = new JsonObject
        {
            ["tool"] = state.Pending.Tool,
            ["tier"] = state.Pending.Tier,
            ["arguments"] = state.Pending.Arguments,
            ["reason"] = state.Pending.Reason,
            ["agentRunId"] = agentRunId,
        };
        return payload.ToJsonString();
    }

    /// <summary>The agent run a <c>tool_approval</c> gate payload names, or <c>null</c>.</summary>
    public static Guid? AgentRunOf(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) is JsonObject node && node["agentRunId"] is JsonValue value && value.TryGetValue<Guid>(out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>FR-359: whether a gate payload already records a claim (<c>ranAt</c>).</summary>
    public static bool HasRanAt(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(payload) is JsonObject node && node.ContainsKey("ranAt");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>FR-359: the payload with <c>ranAt</c>, when the approved call was claimed (the gate's row records the claim).</summary>
    public static string WithRanAt(string? payload, DateTimeOffset ranAt)
    {
        JsonObject node;
        try
        {
            node = JsonNode.Parse(payload ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            node = new JsonObject();
        }

        node["ranAt"] = ranAt;
        return node.ToJsonString();
    }
}
