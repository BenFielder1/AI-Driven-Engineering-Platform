using Microsoft.Extensions.Options;
using Tevrin.Domain.Projects;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Infrastructure.Llm;

/// <summary>
/// Validates <see cref="LlmOptions"/> at start-up: a known, enabled default provider and at least one enabled provider
/// (FR-157), a default model for every provider, and catalog entries with unique ids per provider and a positive output
/// limit (FR-158, FR-160).
/// </summary>
internal sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        var failures = new List<string>();
        if (!LlmProviderCatalog.TryParseProvider(options.DefaultProvider, out var defaultProvider))
        {
            failures.Add($"Llm:DefaultProvider must be one of: {string.Join(", ", EnumStrings.Values<LlmProvider>())}.");
        }
        else if (!options.Providers.For(defaultProvider).Enabled)
        {
            failures.Add($"Llm:DefaultProvider ({defaultProvider.ToWire()}) must be an enabled provider (Llm:Providers:{defaultProvider}:Enabled is false).");
        }

        if (!Enum.GetValues<LlmProvider>().Any(p => options.Providers.For(p).Enabled))
        {
            failures.Add("At least one LLM provider must be enabled (Llm:Providers:<Provider>:Enabled).");
        }

        foreach (var provider in Enum.GetValues<LlmProvider>())
        {
            ValidateCatalog(provider, options.Providers.For(provider).Catalog, failures);
        }

        foreach (var provider in Enum.GetValues<LlmProvider>())
        {
            var settings = options.Providers.For(provider);
            if (string.IsNullOrWhiteSpace(settings.DefaultModel))
            {
                failures.Add($"Llm:Providers:{provider}:DefaultModel is required.");
            }

            foreach (var agent in settings.Models.Keys)
            {
                if (!LlmProviderCatalog.TryParseAgent(agent, out _))
                {
                    failures.Add($"Llm:Providers:{provider}:Models:{agent} is not an agent ({string.Join(", ", SdlcAgents.All.Select(KeyNames.ToPascalCase))}).");
                }
            }
        }

        foreach (var provider in Enum.GetValues<LlmProvider>())
        {
            var baseUrl = options.Providers.For(provider).BaseUrl;
            if (!string.IsNullOrWhiteSpace(baseUrl)
                && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            {
                failures.Add($"Llm:Providers:{provider}:BaseUrl must be an absolute http(s) URL.");
            }
        }

        foreach (var (model, price) in options.Pricing)
        {
            if (price is null || price.InputPerMTok < 0 || price.OutputPerMTok < 0)
            {
                failures.Add($"Llm:Pricing:{model} prices must be zero or more.");
            }
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("Llm:RequestTimeout must be positive.");
        }

        if (options.StreamIdleTimeout <= TimeSpan.Zero)
        {
            failures.Add("Llm:StreamIdleTimeout must be positive.");
        }

        if (options.KeyCheckTimeout <= TimeSpan.Zero)
        {
            failures.Add("Llm:KeyCheckTimeout must be positive.");
        }

        if (options.MaxOutputTokens < 1)
        {
            failures.Add("Llm:MaxOutputTokens must be at least 1.");
        }

        if (options.MaxOutputTokensCeiling is < 1)
        {
            failures.Add("Llm:MaxOutputTokensCeiling must be at least 1 when set.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>FR-158: every entry has an id and a positive output limit, and ids are unique per provider.</summary>
    private static void ValidateCatalog(LlmProvider provider, List<LlmCatalogModelOptions> catalog, List<string> failures)
    {
        for (var i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
            {
                failures.Add($"Llm:Providers:{provider}:Catalog:{i}:Id is required.");
                continue;
            }

            if (entry.MaxOutputTokens < 1)
            {
                failures.Add($"Llm:Providers:{provider}:Catalog:{i}:MaxOutputTokens must be at least 1.");
            }

            if (entry.ContextTokens < 0)
            {
                failures.Add($"Llm:Providers:{provider}:Catalog:{i}:ContextTokens can't be negative.");
            }
        }

        foreach (var duplicate in catalog
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            failures.Add($"Llm:Providers:{provider}:Catalog lists the model id '{duplicate.Key}' more than once.");
        }
    }
}

/// <summary>What the system LLM configuration offers (<see cref="ILlmCatalog"/>, FR-9, FR-22), plus the project rules that
/// pick a project's provider (FR-5, FR-158, §4.24).</summary>
public interface ILlmProviderCatalog : ILlmCatalog
{
    /// <summary>
    /// The provider of a project without saved LLM settings: the new-project default for a user with no connections
    /// (<see cref="DefaultForNewProject"/>), else the configured default (validated to be enabled).
    /// </summary>
    LlmProvider FallbackProvider { get; }

    /// <summary>FR-158, §4.24: whether a user with <paramref name="userConnections"/> connections for the provider can use
    /// it (<see cref="LlmProviderRules.Available"/>); <c>null</c> when the provider is disabled.</summary>
    LlmProviderAvailability? Availability(LlmProvider provider, int userConnections);

    /// <summary>
    /// §4.24 (amends FR-5): the provider of a project created by a user who has a connection for each entry of
    /// <paramref name="connectionProviders"/> (<see cref="ProjectSettingsRules.DefaultForNewProject"/>), or
    /// <c>LLM_PROVIDER_UNAVAILABLE</c>.
    /// </summary>
    Result<LlmProvider> DefaultForNewProject(IReadOnlyCollection<LlmProvider> connectionProviders);
}

internal sealed class LlmProviderCatalog(IOptions<LlmOptions> options) : ILlmProviderCatalog
{
    private readonly LlmOptions _options = options.Value;

    public LlmProvider ConfiguredDefault =>
        TryParseProvider(_options.DefaultProvider, out var provider) ? provider : LlmProvider.Fake;

    public LlmProvider FallbackProvider =>
        DefaultForNewProject([]) is { IsSuccess: true } chosen ? chosen.Value : ConfiguredDefault;

    public bool IsEnabled(LlmProvider provider) => _options.Providers.For(provider).Enabled;

    public IReadOnlyList<LlmProvider> EnabledProviders => [.. Enum.GetValues<LlmProvider>().Where(IsEnabled)];

    public IReadOnlyList<LlmCatalogModel> Catalog(LlmProvider provider) =>
        [.. _options.Providers.For(provider).Catalog
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Id))
            .Select(e => new LlmCatalogModel(
                e.Id.Trim(),
                string.IsNullOrWhiteSpace(e.Name) ? e.Id.Trim() : e.Name.Trim(),
                e.MaxOutputTokens,
                e.ContextTokens,
                e.Recommended))];

    public LlmProviderAvailability? Availability(LlmProvider provider, int userConnections) =>
        LlmProviderRules.Available(provider, IsEnabled(provider), IsSystemKeySet(provider), userConnections, AllowSystemKeys);

    public Result<LlmProvider> DefaultForNewProject(IReadOnlyCollection<LlmProvider> connectionProviders)
    {
        ArgumentNullException.ThrowIfNull(connectionProviders);
        var availability = Enum.GetValues<LlmProvider>().ToDictionary(
            p => p,
            p => Availability(p, connectionProviders.Count(c => c == p)));
        return ProjectSettingsRules.DefaultForNewProject(ConfiguredDefault, availability);
    }

    public Uri OllamaBaseUrl => _options.ResolveOllamaBaseUrl();

    public string? SystemApiKey(LlmProvider provider) =>
        HasSystemKey(provider) ? _options.Providers.For(provider).ApiKey!.Trim() : null;

    public ModelPrice? PriceOf(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var key = model.Trim();
        var price = _options.Pricing.TryGetValue(key, out var exact)
            ? exact
            : _options.Pricing.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return price is null ? null : new ModelPrice(price.InputPerMTok, price.OutputPerMTok);
    }

    public bool AllowSystemKeys => _options.AllowSystemKeys;

    public bool HasSystemKey(LlmProvider provider) => AllowSystemKeys && IsSystemKeySet(provider);

    public bool IsSystemKeySet(LlmProvider provider) =>
        ProjectSettingsRules.RequiresApiKey(provider) && !string.IsNullOrWhiteSpace(_options.Providers.For(provider).ApiKey);

    public string DefaultModel(LlmProvider provider) => _options.Providers.For(provider).DefaultModel;

    public IReadOnlyDictionary<string, string> DefaultAgentModels(LlmProvider provider)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (agent, model) in _options.Providers.For(provider).Models)
        {
            if (TryParseAgent(agent, out var key) && !string.IsNullOrWhiteSpace(model))
            {
                result[key] = model.Trim();
            }
        }

        return result;
    }

    public static bool TryParseProvider(string? value, out LlmProvider provider) =>
        EnumStrings.TryParse(value?.Trim().ToLowerInvariant(), out provider);

    /// <summary>
    /// FR-274: the agent key of a <c>Llm:Providers:&lt;P&gt;:Models:&lt;agent&gt;</c> name, which binds as in 0.6.1: the
    /// 0.6.1 <c>AgentKind</c> member name in any case (<c>CodeReview</c>, <c>codereview</c>) or the key (<c>code_review</c>).
    /// </summary>
    public static bool TryParseAgent(string? value, out string agent) => SdlcAgents.TryParseConfigName(value, out agent);
}
