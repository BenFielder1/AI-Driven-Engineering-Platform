using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Agents;

/// <summary>Picks the provider, model and credential for an agent run (FR-22, FR-79, D-V2-5).</summary>
public interface ILlmResolver
{
    /// <summary>
    /// The project's LLM settings (provider, the agent's model or the default model) when saved, else the system
    /// defaults (<see cref="ILlmProviderCatalog.FallbackProvider"/>, with its configured models); plus the key.
    /// </summary>
    Task<ResolvedLlm> ResolveAsync(Guid projectId, AgentKind agent, CancellationToken cancellationToken);

    /// <summary>
    /// The key for <paramref name="provider"/> (<see cref="LlmConnectionRules.ChooseKey"/>): the project's bound connection
    /// (FR-79) whoever triggered the run; else the system key, only with <c>Llm:AllowSystemKeys=true</c>; none for
    /// Ollama/Fake or when nothing is usable. A bound key that can't be decrypted is no key (never a silent fallback).
    /// </summary>
    Task<LlmKey> KeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken);

    /// <summary>The <see cref="ILlmProvider"/> implementation of a provider.</summary>
    ILlmProvider ProviderFor(LlmProvider provider);
}

internal sealed partial class LlmResolver(
    AideDbContext db,
    ILlmProviderCatalog catalog,
    ISecretProtector secrets,
    IEnumerable<ILlmProvider> providers,
    ILogger<LlmResolver> logger) : ILlmResolver
{
    public async Task<ResolvedLlm> ResolveAsync(Guid projectId, AgentKind agent, CancellationToken cancellationToken)
    {
        var (settings, bound) = await LoadAsync(projectId, cancellationToken);
        LlmChoice choice;
        if (settings is not null && !string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            choice = AgentRunRules.ChooseModel(agent, settings.Provider, settings.DefaultModel, settings.ModelsByAgent);
        }
        else
        {
            var provider = settings?.Provider ?? catalog.FallbackProvider;
            choice = AgentRunRules.ChooseModel(agent, provider, catalog.DefaultModel(provider), catalog.DefaultAgentModels(provider));
        }

        var key = KeyFor(projectId, settings, bound, choice.Provider);
        return new ResolvedLlm(choice.Provider, choice.Model, key.ApiKey, key.CredentialId);
    }

    public async Task<LlmKey> KeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken)
    {
        var (settings, bound) = await LoadAsync(projectId, cancellationToken);
        return KeyFor(projectId, settings, bound, provider);
    }

    public ILlmProvider ProviderFor(LlmProvider provider) =>
        providers.LastOrDefault(p => p.Kind == provider)
        ?? throw new InvalidOperationException($"No ILlmProvider is registered for {provider}.");

    /// <summary>The project's settings and, if bound, its connection (FR-79).</summary>
    private async Task<(ProjectLlmSettings? Settings, LlmCredential? Bound)> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectLlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        var bound = settings?.CredentialId is { } credentialId
            ? await db.LlmCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId, cancellationToken)
            : null;
        return (settings, bound);
    }

    private LlmKey KeyFor(Guid projectId, ProjectLlmSettings? settings, LlmCredential? bound, LlmProvider provider)
    {
        // A binding counts only for the provider the project is set to (bindings always match it, FR-79).
        var boundProvider = settings is not null && bound is not null && settings.Provider == bound.Provider ? bound.Provider : (LlmProvider?)null;
        switch (LlmConnectionRules.ChooseKey(provider, boundProvider, catalog.AllowSystemKeys, catalog.HasSystemKey(provider)))
        {
            case LlmKeySource.Connection:
                if (secrets.TryUnprotect(bound!.SecretEncrypted, out var key) && !string.IsNullOrWhiteSpace(key))
                {
                    return new LlmKey(key, bound.Id);
                }

                LogUnreadableKey(logger, projectId, bound.Id);
                return new LlmKey(null, bound.Id);

            case LlmKeySource.System:
                return new LlmKey(catalog.SystemApiKey(provider), null);

            default:
                return LlmKey.None;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The bound LLM connection {CredentialId} of project {ProjectId} can't be decrypted; runs fail as not configured until it is saved again")]
    private static partial void LogUnreadableKey(ILogger logger, Guid projectId, Guid credentialId);
}
