using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Agents;

public enum LlmRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>One conversation turn sent to a provider.</summary>
public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>
/// A completion request (FR-22). <see cref="ApiKey"/> is the resolved credential (project key, else system key) and
/// is excluded from <see cref="ToString"/> so it can't leak into logs. <see cref="Agent"/> lets the Fake provider pick
/// an agent-specific responder; real providers ignore it.
/// </summary>
public sealed class LlmRequest
{
    public required string Model { get; init; }

    public required string System { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    public int MaxTokens { get; init; } = 16_000;

    public AgentKind? Agent { get; init; }

    /// <summary>Anthropic/OpenAI credential. Never logged or persisted.</summary>
    public string? ApiKey { get; init; }

    public override string ToString() => $"LlmRequest {{ Model = {Model}, Agent = {Agent?.ToWire()}, Messages = {Messages.Count}, MaxTokens = {MaxTokens} }}";
}

/// <summary>A provider's answer: the text and the token usage it reported.</summary>
public sealed record LlmCompletion(string Text, int InputTokens, int OutputTokens, string StopReason);

/// <summary>
/// A failed provider call (FR-25). <see cref="Transient"/> failures (timeouts, connection errors, 408/425/429/5xx) are
/// retried by the job worker; others (auth, validation, unknown model, missing key) fail the run at once.
/// <see cref="Exception.Message"/> never contains credentials.
/// </summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException()
        : this("The LLM provider call failed.", transient: true)
    {
    }

    public LlmProviderException(string message)
        : this(message, transient: true)
    {
    }

    public LlmProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
        Transient = true;
        ErrorCode = ErrorCodes.ProviderError;
    }

    public LlmProviderException(string message, bool transient, int? statusCode = null, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Transient = transient;
        StatusCode = statusCode;
        ErrorCode = errorCode ?? ErrorCodes.ProviderError;
    }

    public bool Transient { get; }

    /// <summary>The provider's HTTP status, when it answered.</summary>
    public int? StatusCode { get; }

    /// <summary><c>PROVIDER_ERROR</c> or <c>PROVIDER_NOT_CONFIGURED</c>.</summary>
    public string ErrorCode { get; }
}

/// <summary>
/// The one LLM abstraction (FR-22, CLAUDE.md rule 11). Implementations: Anthropic, OpenAI, Ollama and Fake. Called only
/// by the agent runner (background jobs), never from endpoints. Failures throw <see cref="LlmProviderException"/>.
/// </summary>
public interface ILlmProvider
{
    LlmProvider Kind { get; }

    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>The provider, model and credential an agent run uses (FR-22).</summary>
public sealed record ResolvedLlm(LlmProvider Provider, string Model, string? ApiKey)
{
    /// <summary>Anthropic and OpenAI need a key; without one the run fails as <c>PROVIDER_NOT_CONFIGURED</c>.</summary>
    public bool IsConfigured => !Aide.Domain.Projects.ProjectSettingsRules.RequiresApiKey(Provider) || !string.IsNullOrWhiteSpace(ApiKey);

    public override string ToString() => $"ResolvedLlm {{ Provider = {Provider.ToWire()}, Model = {Model}, HasApiKey = {ApiKey is not null} }}";
}
