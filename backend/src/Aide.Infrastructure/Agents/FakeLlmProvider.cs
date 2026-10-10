using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;

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
/// </summary>
public sealed class FakeLlmProvider(IEnumerable<IFakeAgentResponder> responders) : ILlmProvider
{
    public const string StopReason = "end_turn";

    private readonly Dictionary<AgentKind, IFakeAgentResponder> _responders = responders
        .GroupBy(r => r.Agent)
        .ToDictionary(g => g.Key, g => g.Last());

    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = request.System + "\n" + string.Join("\n", request.Messages.Select(m => m.Content));
        var text = request.Agent is { } agent && _responders.TryGetValue(agent, out var responder)
            ? responder.Respond(request)
            : GenericReply(request, prompt);

        return Task.FromResult(new LlmCompletion(text, EstimateTokens(prompt), EstimateTokens(text), StopReason));
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
