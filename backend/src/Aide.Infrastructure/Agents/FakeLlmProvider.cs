using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.RepoTools;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// Produces the Fake provider's output for one agent (D13). Register implementations in DI (<c>AddSingleton&lt;IFakeAgentResponder, …&gt;</c>);
/// for an agent with several responders the last registered wins, so tests can override the built-in ones. Responders
/// must be deterministic: the same request gives the same text.
/// </summary>
public interface IFakeAgentResponder
{
    AgentKind Agent { get; }

    string Respond(LlmRequest request);
}

/// <summary>
/// The deterministic, offline provider (FR-22, D13) for demos, E2E and tests: no network, no key. It answers with the
/// agent's registered <see cref="IFakeAgentResponder"/>, else a generic Markdown reply naming the agent, the model and
/// a digest of the prompt. Token counts are estimated as ⌈characters / 4⌉.
/// <para>
/// FR-71: in a tool loop it first makes the agent's scripted calls (<see cref="IFakeToolScript"/>), one per turn, then
/// answers as above. It answers at once when the script is done, a tool returned <c>TOOL_LIMIT</c>, or the final answer
/// is forced.
/// </para>
/// </summary>
public sealed class FakeLlmProvider : ILlmProvider, IToolCallingLlmProvider
{
    public const string StopReason = "end_turn";
    public const string ToolUseStopReason = "tool_use";

    private readonly Dictionary<AgentKind, IFakeAgentResponder> _responders;
    private readonly Dictionary<AgentKind, IFakeToolScript> _scripts;

    public FakeLlmProvider(IEnumerable<IFakeAgentResponder> responders)
        : this(responders, [])
    {
    }

    public FakeLlmProvider(IEnumerable<IFakeAgentResponder> responders, IEnumerable<IFakeToolScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(responders);
        ArgumentNullException.ThrowIfNull(scripts);
        _responders = responders.GroupBy(r => r.Agent).ToDictionary(g => g.Key, g => g.Last());
        _scripts = scripts.GroupBy(s => s.Agent).ToDictionary(g => g.Key, g => g.Last());
    }

    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = Prompt(request);
        var text = Answer(request, prompt);
        return Task.FromResult(new LlmCompletion(text, EstimateTokens(prompt), EstimateTokens(text), StopReason));
    }

    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = Prompt(toolRequest.Request) + "\n" + string.Join(
            "\n",
            toolRequest.Exchanges.SelectMany(e => e.Results.Select(r => r.Content)));
        var made = toolRequest.Exchanges.Sum(e => e.Calls.Count);
        var limited = toolRequest.Exchanges.Any(e => e.Results.Any(r => r.IsError && r.Content.StartsWith(RepoToolErrors.ToolLimit, StringComparison.Ordinal)));
        var script = toolRequest.Request.Agent is { } agent && _scripts.TryGetValue(agent, out var found) ? found.Calls : [];
        if (!toolRequest.ForceFinalAnswer && !limited && made < script.Count)
        {
            var next = script[made];
            var call = new LlmToolCall(LlmHttp.CallId(toolRequest.Exchanges.Count + 1, 1), next.Tool, next.ArgumentsJson);
            return Task.FromResult(new LlmToolTurn(string.Empty, [call], EstimateTokens(prompt), EstimateTokens(next.ArgumentsJson), ToolUseStopReason));
        }

        var text = Answer(toolRequest.Request, Prompt(toolRequest.Request));
        return Task.FromResult(new LlmToolTurn(text, [], EstimateTokens(prompt), EstimateTokens(text), StopReason));
    }

    /// <summary>⌈characters / 4⌉, at least 1.</summary>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Math.Max(1, (text.Length + 3) / 4);
    }

    /// <summary>First 12 hex characters of the SHA-256 of the prompt.</summary>
    public static string Digest(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..12];

    private static string Prompt(LlmRequest request) => request.System + "\n" + string.Join("\n", request.Messages.Select(m => m.Content));

    private string Answer(LlmRequest request, string prompt) =>
        request.Agent is { } agent && _responders.TryGetValue(agent, out var responder)
            ? responder.Respond(request)
            : GenericReply(request, prompt);

    private static string GenericReply(LlmRequest request, string prompt)
    {
        var agent = request.Agent?.ToWire() ?? "unknown";
        return $"""
            # Fake {agent} output

            This is a deterministic reply from the Fake LLM provider (model `{request.Model}`).

            - Agent: {agent}
            - Prompt digest: {Digest(prompt)}
            - Prompt length: {prompt.Length} characters
            """;
    }
}
