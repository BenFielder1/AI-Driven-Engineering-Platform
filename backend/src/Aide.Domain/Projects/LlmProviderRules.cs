using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Projects;

/// <summary>
/// Why an enabled LLM provider is or isn't usable by a user (FR-158, §4.24). Wire values: <c>system_key</c>,
/// <c>own_connection</c>, <c>keyless</c>, <c>needs_connection</c>.
/// </summary>
public enum LlmProviderReason
{
    /// <summary>Usable: a system key is configured and <c>Llm:AllowSystemKeys</c> is on.</summary>
    SystemKey = 0,

    /// <summary>Usable: the user has a connection (FR-78) for the provider.</summary>
    OwnConnection = 1,

    /// <summary>Usable: the provider needs no key (Ollama, Fake).</summary>
    Keyless = 2,

    /// <summary>Not usable yet: the provider needs a key and the user has no connection for it.</summary>
    NeedsConnection = 3,
}

/// <summary>Whether a user can use an enabled provider, and why (FR-158).</summary>
public sealed record LlmProviderAvailability(bool Usable, LlmProviderReason Reason);

/// <summary>FR-157, FR-158, FR-159, §4.24: LLM provider enablement and availability.</summary>
public static class LlmProviderRules
{
    /// <summary>§4.24: the order in which a new project's default provider is looked for after the configured default.</summary>
    public static IReadOnlyList<LlmProvider> PreferenceOrder { get; } =
        [LlmProvider.Anthropic, LlmProvider.Openai, LlmProvider.Ollama, LlmProvider.Fake];

    /// <summary>
    /// §4.24 <c>LlmProviderRules.Available(enabled, configured, userConnections, allowSystemKeys)</c>: <c>null</c> for a
    /// disabled provider (it's absent from every list and can't be chosen). Ollama and Fake are <c>keyless</c>. Anthropic and
    /// OpenAI are <c>system_key</c> when a system key is <paramref name="configured"/> and <paramref name="allowSystemKeys"/>
    /// is on, else <c>own_connection</c> when the user has a connection, else <c>needs_connection</c> (not usable).
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="enabled"><c>Llm:Providers:&lt;P&gt;:Enabled</c>.</param>
    /// <param name="configured">Whether a non-blank system key is configured for the provider.</param>
    /// <param name="userConnections">How many LLM connections the user has for the provider.</param>
    /// <param name="allowSystemKeys"><c>Llm:AllowSystemKeys</c>.</param>
    public static LlmProviderAvailability? Available(LlmProvider provider, bool enabled, bool configured, int userConnections, bool allowSystemKeys)
    {
        if (!enabled)
        {
            return null;
        }

        if (!ProjectSettingsRules.RequiresApiKey(provider))
        {
            return new LlmProviderAvailability(true, LlmProviderReason.Keyless);
        }

        if (configured && allowSystemKeys)
        {
            return new LlmProviderAvailability(true, LlmProviderReason.SystemKey);
        }

        return userConnections > 0
            ? new LlmProviderAvailability(true, LlmProviderReason.OwnConnection)
            : new LlmProviderAvailability(false, LlmProviderReason.NeedsConnection);
    }

    /// <summary>
    /// FR-157, FR-159: a provider can be chosen for a project (create, <c>PATCH …/settings/llm</c>) only when it's enabled
    /// and usable by the caller; otherwise <c>LLM_PROVIDER_UNAVAILABLE</c>.
    /// </summary>
    /// <param name="provider">The chosen provider.</param>
    /// <param name="availability">Its <see cref="Available"/> result for the caller (<c>null</c>: disabled).</param>
    public static Result CanChoose(LlmProvider provider, LlmProviderAvailability? availability) =>
        availability is { Usable: true } ? Result.Success() : Result.Failure(Unavailable(provider, availability));

    /// <summary>FR-157: a provider that is enabled on this deployment (usable or not).</summary>
    public static Result IsEnabled(LlmProvider provider, bool enabled) =>
        enabled ? Result.Success() : Result.Failure(Unavailable(provider, null));

    /// <summary>The <c>LLM_PROVIDER_UNAVAILABLE</c> error for <paramref name="provider"/>.</summary>
    public static DomainError Unavailable(LlmProvider provider, LlmProviderAvailability? availability) =>
        new(ErrorCodes.LlmProviderUnavailable, availability is null
            ? $"The {provider.ToWire()} AI provider is turned off on this server. Choose another provider."
            : $"The {provider.ToWire()} AI provider isn't available to you yet. Add a {provider.ToWire()} connection or choose another provider.");
}
