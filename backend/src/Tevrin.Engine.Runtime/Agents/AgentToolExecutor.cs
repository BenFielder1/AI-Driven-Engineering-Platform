using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// One tool call made during an agent run, as recorded in <c>agent_runs.tool_calls</c>. <see cref="Arguments"/> is
/// secret-redacted (<see cref="SecretRedaction.Redact"/>) and at most <see cref="AgentToolExecutor.MaxLoggedArgumentLength"/>
/// characters. FR-358 (0.8.0): <see cref="Tier"/> is the called tool's registered tier at call time (<c>read</c>,
/// <c>write</c> or <c>destructive</c>), refused calls included; <c>null</c> (left out of the JSON) for a tool the stage
/// doesn't list, for an executor without a tier policy, and in rows written before 0.8.0.
/// </summary>
public sealed record AgentToolCallRecord(
    string Tool,
    string Arguments,
    bool IsError,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier = null);

/// <summary>
/// FR-358: a tool a stage lists, as the runner resolves it for one call: its registered name, the bound tool, its tier in
/// the registry now (<c>null</c>: nothing registers it any more) and whether it is registered <c>external</c> (FR-360).
/// </summary>
public sealed record StageTool(string Name, IAgentTool Tool, ToolTier? Tier, bool External);

/// <summary>
/// FR-280, FR-295: runs a stage's <see cref="IAgentTool"/>s (resolved by name from DI) in the LLM tool loop. A call to a
/// tool the stage doesn't list, arguments that aren't JSON, or a tool that throws is an error result, never an exception,
/// unless the host's <see cref="AgentToolSession"/> (FR-317, FR-319) says otherwise; the session also refuses calls at its
/// limit, supplies the fallback context and writes the log.
/// <para>
/// FR-358 (<see cref="ForStage"/>, the runner's executor): only the listed tools whose current tier is within the stage's
/// <c>max_tier</c> are offered, and only offered <c>read</c>/<c>write</c> tools that aren't external ever run. A call to
/// any other tool is refused with an error result and logged (never with its arguments). FR-359: a call to an offered
/// destructive tool is never run here: it suspends the tool loop (<see cref="LlmToolCallSuspendedException"/>), and the
/// runner saves the loop and raises a <c>tool_approval</c> gate; once the gate is approved the runner runs it once through
/// <see cref="ExecuteApprovedAsync"/>. An external tool waits on an <c>external_action</c> gate (FR-360), which isn't
/// built yet, so it is refused.
/// </para>
/// </summary>
public sealed partial class AgentToolExecutor : ILlmToolExecutor
{
    /// <summary>The longest logged arguments text (as 0.6.1's repo tools).</summary>
    public const int MaxLoggedArgumentLength = 2000;

    /// <summary>The longest logged tool name.</summary>
    public const int MaxLoggedToolNameLength = 60;

    /// <summary>The tools that run when called (by <see cref="ToolSpec.Name"/>): with a policy, only offered read/write
    /// tools that aren't external.</summary>
    private readonly Dictionary<string, IAgentTool> _tools;

    /// <summary>FR-358: every listed tool by <see cref="ToolSpec.Name"/>, for the verdict and the recorded tier; <c>null</c>
    /// without a tier policy.</summary>
    private readonly Dictionary<string, StageTool>? _listed;
    private readonly ToolTier _maxTier;
    private readonly ILogger _logger;
    private readonly List<AgentToolCallRecord> _calls = [];
    private readonly AgentToolSession _session;
    private bool _refused;

    /// <summary>
    /// An executor of <paramref name="tools"/> with no tier policy: every tool is offered and runs. For tests and host
    /// code; the runner builds its executors with <see cref="ForStage"/>.
    /// </summary>
    public AgentToolExecutor(IEnumerable<IAgentTool> tools, AgentToolSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _session = session ?? AgentToolSession.Default;
        _logger = NullLogger.Instance;
        _tools = tools.GroupBy(t => t.Spec.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Definitions = [.. _tools.Values.Select(Definition)];
    }

    private AgentToolExecutor(IReadOnlyList<StageTool> listed, ToolTier maxTier, AgentToolSession? session, ILogger? logger)
    {
        _session = session ?? AgentToolSession.Default;
        _logger = logger ?? NullLogger.Instance;
        _maxTier = maxTier;

        // One entry per name the model sees; an offered entry wins over an unoffered one with the same name.
        _listed = listed
            .GroupBy(t => t.Tool.Spec.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(t => ToolTierRules.IsOffered(maxTier, t.Tier)) ?? g.First(), StringComparer.Ordinal);
        var offered = _listed.Values.Where(t => ToolTierRules.IsOffered(maxTier, t.Tier)).ToList();
        Definitions = [.. offered.Select(t => Definition(t.Tool))];

        // Defence in depth: a tool that needs approval is offered to the model but never in the table that runs tools.
        _tools = offered
            .Where(t => ToolTierRules.VerdictOf(maxTier, t.Tier, t.External) == ToolCallVerdict.Run)
            .ToDictionary(t => t.Tool.Spec.Name, t => t.Tool, StringComparer.Ordinal);
    }

    /// <summary>
    /// FR-358: the executor of one stage's call. <paramref name="listed"/> are the tools the stage or binding lists, each
    /// with its tier in the registry now; those within <paramref name="maxTier"/> are offered, the rest refused when
    /// called. An offered destructive tool suspends the loop (FR-359); an external one is refused as
    /// <see cref="ApprovalUnavailable"/> (FR-360).
    /// </summary>
    public static AgentToolExecutor ForStage(IEnumerable<StageTool> listed, ToolTier maxTier, AgentToolSession? session = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return new AgentToolExecutor([.. listed], maxTier, session, logger);
    }

    private static LlmToolDefinition Definition(IAgentTool tool) => new(tool.Spec.Name, tool.Spec.Description, tool.Spec.InputSchema);

    /// <summary>
    /// FR-317: no tools for this call (as <see cref="AgentCallOptions.Tools"/>, e.g. no repository snapshot): the call is a
    /// plain completion, unlike <c>null</c>, which means the stage's tools. Stateless, so it is safe to share.
    /// </summary>
    public static ILlmToolExecutor None { get; } = new NoToolExecutor();

    /// <summary>The placeholder logged when redacting the arguments timed out (as the recorded input does).</summary>
    public const string RedactionTimedOut = "[redaction timed out]";

    public IReadOnlyList<LlmToolDefinition> Definitions { get; }

    /// <summary>FR-317: a call was refused by the session, or the session says its limit is reached.</summary>
    public bool LimitReached => _refused || _session.LimitReached;

    /// <summary>Every call made, in order.</summary>
    public IReadOnlyList<AgentToolCallRecord> Calls => _calls;

    public async Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ToolResult result;
        if (AwaitsApproval(toolCall) is { } waiting)
        {
            if (LimitReached)
            {
                // FR-359: at the session's limit no gate is raised for a call that couldn't run anyway.
                _refused = true;
                Record(toolCall, isError: true);
                return new LlmToolResult(toolCall.Id, LimitRefusal(toolCall).Content, IsError: true);
            }

            // FR-359: never run, and not recorded yet: the runner records it once the gate is decided.
            LogAwaitingApproval(_logger, waiting.Name, _maxTier.ToWire());
            throw new LlmToolCallSuspendedException($"The tool '{waiting.Name}' waits on a tool_approval gate.", toolCall);
        }

        if (TierRefusal(toolCall) is { } tierRefusal)
        {
            // FR-358–FR-360: never run; not counted against the session's limit (the tool loop's turn cap still applies).
            result = tierRefusal;
        }
        else if (_session.BeforeCall(toolCall) is { } refusal)
        {
            _refused = true;
            result = refusal with { IsError = true };
        }
        else
        {
            try
            {
                result = _session.AfterCall(toolCall, await InvokeAsync(toolCall, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FR-317: a tool exception the session rethrows (failing the run) is still in the log.
                Record(toolCall, isError: true);
                throw;
            }
        }

        Record(toolCall, result.IsError);
        return new LlmToolResult(toolCall.Id, result.Content, result.IsError);
    }

    /// <summary>
    /// FR-359: the listed tool a call to <paramref name="modelName"/> resolves to (its registered name and tier), or
    /// <c>null</c> when the stage doesn't list it (or the executor has no tier policy).
    /// </summary>
    public StageTool? ListedTool(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return _listed is not null && _listed.TryGetValue(modelName, out var tool) ? tool : null;
    }

    /// <summary>FR-359: an offered destructive, non-external tool the call names: it suspends the loop instead of running.</summary>
    private StageTool? AwaitsApproval(LlmToolCall toolCall) =>
        ListedTool(toolCall.Name) is { } tool && IsApprovable(tool) ? tool : null;

    /// <summary>FR-359: whether <paramref name="tool"/> is offered here and waits on a <c>tool_approval</c> gate.</summary>
    private bool IsApprovable(StageTool tool) =>
        !tool.External && tool.Tier == ToolTier.Destructive && ToolTierRules.VerdictOf(_maxTier, tool.Tier, tool.External) == ToolCallVerdict.NeedsApproval;

    /// <summary>
    /// FR-359: restores the calls an earlier delivery of this agent run recorded before the loop stopped, so the run's
    /// <c>tool_calls</c> keep them. Only before the first call.
    /// </summary>
    public void Restore(IEnumerable<AgentToolCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (_calls.Count > 0)
        {
            throw new InvalidOperationException("Calls can only be restored before the first call.");
        }

        _calls.AddRange(calls);
    }

    /// <summary>The session's refusal of a call at its limit (its own text, else a generic one).</summary>
    private ToolResult LimitRefusal(LlmToolCall toolCall) =>
        _session.BeforeCall(toolCall) is { } refusal
            ? refusal with { IsError = true }
            : new ToolResult($"The tool call limit is reached; '{toolCall.Name}' was not run.", IsError: true);

    /// <summary>
    /// FR-359: the checks an approved destructive call must pass before the runner claims it, as they stand now: the tool
    /// is still listed, offered under the stage's <c>max_tier</c>, destructive and not external, and the session doesn't
    /// refuse it. A refusal is recorded and returned (the call is never claimed or run); <c>null</c>: run it with
    /// <see cref="RunApprovedAsync"/>.
    /// </summary>
    public LlmToolResult? RefuseApproved(LlmToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ToolResult refused;
        if (ListedTool(toolCall.Name) is not { } tool || !IsApprovable(tool))
        {
            LogRefused(_logger, Shorten(toolCall.Name, MaxLoggedToolNameLength), "approved_tool_not_offered", ListedTool(toolCall.Name)?.Tier?.ToWire() ?? "unregistered", _maxTier.ToWire());
            refused = new ToolResult(NotOffered(toolCall.Name), IsError: true);
        }
        else if (_session.BeforeCall(toolCall) is { } refusal)
        {
            _refused = true;
            refused = refusal with { IsError = true };
        }
        else
        {
            return null;
        }

        Record(toolCall, isError: true);
        return new LlmToolResult(toolCall.Id, refused.Content, IsError: true);
    }

    /// <summary>FR-359: runs an approved destructive call that passed <see cref="RefuseApproved"/> (and was claimed), once.</summary>
    public async Task<LlmToolResult> RunApprovedAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var tool = ListedTool(toolCall.Name) is { } listed && IsApprovable(listed)
            ? listed
            : throw new InvalidOperationException("Only a call that passed RefuseApproved runs.");
        ToolResult result;
        try
        {
            result = _session.AfterCall(toolCall, await InvokeToolAsync(tool.Tool, toolCall, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(toolCall, isError: true);
            throw;
        }

        Record(toolCall, result.IsError);
        return new LlmToolResult(toolCall.Id, result.Content, result.IsError);
    }

    /// <summary>FR-359: <see cref="RefuseApproved"/>, then <see cref="RunApprovedAsync"/> (for callers that claim nothing).</summary>
    public async Task<LlmToolResult> ExecuteApprovedAsync(LlmToolCall toolCall, CancellationToken cancellationToken) =>
        RefuseApproved(toolCall) ?? await RunApprovedAsync(toolCall, cancellationToken);

    /// <summary>FR-359: records a call that was decided without running (denied, or claimed before) with its result text.</summary>
    public LlmToolResult RecordUnrun(LlmToolCall toolCall, string content)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(content);
        Record(toolCall, isError: true);
        return new LlmToolResult(toolCall.Id, content, IsError: true);
    }

    private void Record(LlmToolCall toolCall, bool isError) =>
        _calls.Add(new AgentToolCallRecord(Shorten(toolCall.Name, MaxLoggedToolNameLength), LoggedArguments(toolCall.ArgumentsJson), isError, TierOf(toolCall)));

    /// <summary>FR-358: the called tool's tier as recorded (<c>null</c>: not listed, or no tier policy).</summary>
    private string? TierOf(LlmToolCall toolCall) =>
        _listed is not null && _listed.TryGetValue(toolCall.Name, out var tool) ? tool.Tier?.ToWire() : null;

    /// <summary>
    /// FR-358–FR-360: the refusal of a call to a listed tool that isn't offered, or that needs an approval gate; <c>null</c>
    /// when the tool may run, or when it isn't listed (<see cref="AgentToolSession.UnknownTool"/> answers, as before).
    /// </summary>
    private ToolResult? TierRefusal(LlmToolCall toolCall)
    {
        if (_listed is null || !_listed.TryGetValue(toolCall.Name, out var tool))
        {
            return null;
        }

        var verdict = ToolTierRules.VerdictOf(_maxTier, tool.Tier, tool.External);
        if (verdict == ToolCallVerdict.Run)
        {
            return null;
        }

        var tier = tool.Tier?.ToWire() ?? "unregistered";
        if (verdict == ToolCallVerdict.NotOffered)
        {
            LogRefused(_logger, tool.Name, "above_max_tier", tier, _maxTier.ToWire());
            return new ToolResult(NotOffered(toolCall.Name), IsError: true);
        }

        LogRefused(_logger, tool.Name, tool.External ? "external_needs_approval" : "destructive_needs_approval", tier, _maxTier.ToWire());
        return new ToolResult(ApprovalUnavailable(toolCall.Name), IsError: true);
    }

    /// <summary>FR-358: the error result of a call to a listed tool above the stage's <c>max_tier</c> (or no longer registered).</summary>
    public static string NotOffered(string tool) => $"The tool '{tool}' is not offered to this stage.";

    /// <summary>FR-360: the error result of a call to an offered external tool until its approval gate is built (M71d); the
    /// tool is never run.</summary>
    public static string ApprovalUnavailable(string tool) => $"The tool '{tool}' requires approval, which is not available yet; it was not run.";

    // NFR-SEC: the tool's registered name (from the pack, not the model), the reason and the tiers; never the arguments.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent tool call refused: {Tool} ({Reason}; tier {Tier}, stage max tier {MaxTier})")]
    private static partial void LogRefused(ILogger logger, string tool, string reason, string tier, string maxTier);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent tool call waits on approval: {Tool} (destructive; stage max tier {MaxTier})")]
    private static partial void LogAwaitingApproval(ILogger logger, string tool, string maxTier);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent tool call refused: {Tool} (not_listed; stage max tier {MaxTier})")]
    private static partial void LogUnlisted(ILogger logger, string tool, string maxTier);

    public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => _session.FallbackContextAsync(cancellationToken);

    /// <summary>The calls as the JSON array stored on the agent run (the session's format).</summary>
    public string CallsJson() => _session.CallsJson(_calls);

    /// <summary>NFR-SEC: arguments as logged: secret-redacted, then capped; <see cref="RedactionTimedOut"/> when redacting
    /// them timed out, so writing the log never fails the run.</summary>
    public static string LoggedArguments(string? argumentsJson) => LoggedArguments(argumentsJson, text => SecretRedaction.Redact(text));

    internal static string LoggedArguments(string? argumentsJson, Func<string, string> redact)
    {
        try
        {
            return Shorten(redact(argumentsJson ?? string.Empty), MaxLoggedArgumentLength);
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactionTimedOut;
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task<ToolResult> InvokeAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            if (_listed is not null)
            {
                // FR-358: the name comes from the model, so it is logged shortened.
                LogUnlisted(_logger, Shorten(toolCall.Name, MaxLoggedToolNameLength), _maxTier.ToWire());
            }

            return _session.UnknownTool(toolCall);
        }

        return await InvokeToolAsync(tool, toolCall, cancellationToken);
    }

    private async Task<ToolResult> InvokeToolAsync(IAgentTool tool, LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            if (tool is IRawArgumentsAgentTool raw)
            {
                return await raw.InvokeRawAsync(toolCall.ArgumentsJson, cancellationToken);
            }

            JsonDocument args;
            try
            {
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
            }
            catch (JsonException)
            {
                return _session.InvalidArguments(toolCall);
            }

            using (args)
            {
                return await tool.InvokeAsync(args.RootElement.Clone(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // FR-317: rethrown as is, so the run fails as 0.6.1's did (PROVIDER_ERROR, transient by exception type).
            var handled = _session.OnToolException(toolCall, ex);
            if (handled is null)
            {
                throw;
            }

            return handled;
        }
    }

    /// <summary>FR-317: offers no tools and keeps no state (<see cref="None"/>).</summary>
    private sealed class NoToolExecutor : ILlmToolExecutor
    {
        public IReadOnlyList<LlmToolDefinition> Definitions => [];

        public bool LimitReached => false;

        public Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(toolCall);
            return Task.FromResult(new LlmToolResult(toolCall.Id, $"Unknown tool '{toolCall.Name}'.", IsError: true));
        }

        public Task<string?> FallbackContextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
