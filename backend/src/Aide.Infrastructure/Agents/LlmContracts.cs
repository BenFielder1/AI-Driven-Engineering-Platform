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

/// <summary>The provider, model and credential an agent run uses (FR-22). <see cref="CredentialId"/> is the bound
/// connection the key came from (FR-79), <c>null</c> for a system key or a provider without one.</summary>
public sealed record ResolvedLlm(LlmProvider Provider, string Model, string? ApiKey, Guid? CredentialId = null)
{
    /// <summary>Anthropic and OpenAI need a key; without one the run fails as <c>PROVIDER_NOT_CONFIGURED</c>.</summary>
    public bool IsConfigured => !Aide.Domain.Projects.ProjectSettingsRules.RequiresApiKey(Provider) || !string.IsNullOrWhiteSpace(ApiKey);

    public override string ToString() => $"ResolvedLlm {{ Provider = {Provider.ToWire()}, Model = {Model}, HasApiKey = {ApiKey is not null}, CredentialId = {CredentialId} }}";
}

/// <summary>The key a run of a project uses (FR-79, D-V2-5) and the connection it came from (<c>null</c> for a system key).
/// <see cref="ToString"/> never shows the key.</summary>
public sealed record LlmKey(string? ApiKey, Guid? CredentialId)
{
    public static LlmKey None { get; } = new(null, null);

    public override string ToString() => $"LlmKey {{ HasApiKey = {ApiKey is not null}, CredentialId = {CredentialId} }}";
}

/// <summary>The result of checking a key with the provider (FR-78).</summary>
public enum LlmKeyCheckOutcome
{
    /// <summary>The provider accepted the key.</summary>
    Valid = 0,

    /// <summary>The provider rejected the key (HTTP 401/403): <c>CREDENTIAL_INVALID</c>.</summary>
    Rejected = 1,

    /// <summary>The provider could not be reached or failed (network, timeout, 429, 5xx, other statuses): <c>UPSTREAM_ERROR</c>.</summary>
    Unavailable = 2,
}

/// <summary>A key check's outcome, the provider's HTTP status (when it answered) and a message that never holds the key.</summary>
public sealed record LlmKeyCheck(LlmKeyCheckOutcome Outcome, int? StatusCode, string Message);

/// <summary>
/// FR-78: validates an Anthropic or OpenAI key with a minimal authenticated call (the provider's list-models endpoint)
/// through the provider's typed HttpClient, at the configured fixed host (never a user-supplied URL). No completion is
/// requested, so no tokens are spent.
/// </summary>
public interface ILlmKeyValidator
{
    Task<LlmKeyCheck> CheckAsync(LlmProvider provider, string apiKey, CancellationToken cancellationToken);
}
