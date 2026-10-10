using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Agents;

/// <summary>Picks the provider, model and credential for an agent run (FR-22).</summary>
public interface ILlmResolver
{
    /// <summary>
    /// The project's LLM settings (provider, the agent's model or the default model) when saved, else the system
    /// defaults (<c>Llm:DefaultProvider</c> if usable, else Fake, with its configured models); plus the key.
    /// </summary>
    Task<ResolvedLlm> ResolveAsync(Guid projectId, AgentKind agent, CancellationToken cancellationToken);

    /// <summary>The credential for <paramref name="provider"/>: the project's own key when its settings are for that
    /// provider, else the system key; <c>null</c> for Ollama/Fake or when none is configured.</summary>
    Task<string?> ApiKeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken);

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
        var settings = await LoadAsync(projectId, cancellationToken);
        LlmChoice choice;
        if (settings is not null && !string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            choice = AgentRunRules.ChooseModel(agent, settings.Provider, settings.DefaultModel, settings.ModelsByAgent);
        }
        else
        {
            var provider = settings?.Provider ?? catalog.DefaultForNewProject;
            choice = AgentRunRules.ChooseModel(agent, provider, catalog.DefaultModel(provider), catalog.DefaultAgentModels(provider));
        }

        return new ResolvedLlm(choice.Provider, choice.Model, KeyFor(projectId, settings, choice.Provider));
    }

    public async Task<string?> ApiKeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken) =>
        KeyFor(projectId, await LoadAsync(projectId, cancellationToken), provider);

    public ILlmProvider ProviderFor(LlmProvider provider) =>
        providers.LastOrDefault(p => p.Kind == provider)
        ?? throw new InvalidOperationException($"No ILlmProvider is registered for {provider}.");

    private Task<ProjectLlmSettings?> LoadAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.ProjectLlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);

    private string? KeyFor(Guid projectId, ProjectLlmSettings? settings, LlmProvider provider)
    {
        if (!Aide.Domain.Projects.ProjectSettingsRules.RequiresApiKey(provider))
        {
            return null;
        }

        if (settings is { ApiKeyEncrypted: { } encrypted } && settings.Provider == provider)
        {
            if (secrets.TryUnprotect(encrypted, out var key) && !string.IsNullOrWhiteSpace(key))
            {
                return key;
            }

            LogUnreadableKey(logger, projectId);
        }

        return catalog.SystemApiKey(provider);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The LLM API key of project {ProjectId} can't be decrypted; falling back to the system key")]
    private static partial void LogUnreadableKey(ILogger logger, Guid projectId);
}
