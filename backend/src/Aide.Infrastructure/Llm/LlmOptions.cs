using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Llm;

/// <summary>
/// System LLM configuration (section <c>Llm</c>, FR-22–FR-26, §11 Q4). Keys:
/// <list type="bullet">
/// <item><c>Llm:DefaultProvider</c> (anthropic|openai|ollama|fake, default anthropic);</item>
/// <item><c>Llm:Providers:{Anthropic|OpenAI|Ollama|Fake}:DefaultModel</c> and <c>…:Models:{AgentKind}</c> (e.g. <c>Models:IdeaRefinement</c>);</item>
/// <item><c>Llm:Providers:Anthropic:ApiKey</c>, <c>Llm:Providers:OpenAI:ApiKey</c> (secrets: user-secrets / secrets.env only);</item>
/// <item><c>Llm:AllowSystemKeys</c> (default <c>false</c>, D-V2-5): whether those system keys may be used at all, and then only
/// for projects with no bound connection (FR-79);</item>
/// <item><c>Llm:Providers:{Provider}:BaseUrl</c> (defaults: https://api.anthropic.com, https://api.openai.com/v1, and for
/// Ollama http://localhost:11434 in the dev loop or http://host.docker.internal:11434 in a container);</item>
/// <item><c>Llm:Pricing:{model-id}:InputPerMTok</c> / <c>OutputPerMTok</c> (USD per million tokens; unpriced models cost 0);</item>
/// <item><c>Llm:KeyCheckTimeout</c> (per key check, FR-78, default 00:00:15);</item>
/// <item>FR-161: <c>Llm:RequestTimeout</c> (the whole of one streamed provider call, default 00:30:00) and
/// <c>Llm:StreamIdleTimeout</c> (no stream data for this long fails the call, default 00:02:00);</item>
/// <item>FR-157: <c>Llm:Providers:{Provider}:Enabled</c> (default <c>true</c>): a disabled provider is absent from every list,
/// can't be chosen and fails the runs of projects still set to it;</item>
/// <item>FR-158, FR-160: <c>Llm:Providers:{Provider}:Catalog:{n}:{Id, Name, MaxOutputTokens, ContextTokens, Recommended}</c>
/// (the model catalog), <c>Llm:MaxOutputTokens</c> (default 16000: the output limit of models not in the catalog) and the
/// optional <c>Llm:MaxOutputTokensCeiling</c> (caps every call's limit).</item>
/// </list>
/// Environment variables use <c>__</c>, e.g. <c>Llm__Providers__Anthropic__ApiKey</c>.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string DefaultProvider { get; set; } = "anthropic";

    /// <summary>D-V2-5, FR-79: system keys are used only when this is <c>true</c> and the project has no bound connection.
    /// Off by default: runs use the key an Owner bound to the project.</summary>
    public bool AllowSystemKeys { get; set; }

    public LlmProvidersOptions Providers { get; set; } = new();

    /// <summary>Price per model id (case-insensitive), for run cost estimates (FR-23, D15).</summary>
    public Dictionary<string, LlmPriceOptions> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>FR-161: how long one (streamed) provider call may take in all before it counts as a (transient) timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>FR-161: how long a streamed provider call may go without receiving any data before it counts as a
    /// (transient) timeout.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>FR-78: how long a key check (list models) may take before it counts as <c>UPSTREAM_ERROR</c>.</summary>
    public TimeSpan KeyCheckTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>FR-160: the output token limit of a model that isn't in its provider's catalog (and, for Anthropic, whose
    /// limit couldn't be discovered).</summary>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>FR-160: optional operator ceiling (at least 1) on every call's output token limit.</summary>
    public int? MaxOutputTokensCeiling { get; set; }

    /// <summary>The Ollama endpoint: <c>Llm:Providers:Ollama:BaseUrl</c>, else host.docker.internal in a container or
    /// localhost for the dev loop.</summary>
    public Uri ResolveOllamaBaseUrl() => new(
        Providers.Ollama.BaseUrl is { Length: > 0 } url ? url
        : string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? LlmProviderCatalog.ContainerOllamaUrl
            : LlmProviderCatalog.LocalOllamaUrl);
}

/// <summary>USD per million input and output tokens.</summary>
public sealed class LlmPriceOptions
{
    public decimal InputPerMTok { get; set; }

    public decimal OutputPerMTok { get; set; }
}

public sealed class LlmProvidersOptions
{
    public LlmProviderOptions Anthropic { get; set; } = new();

    public LlmProviderOptions OpenAI { get; set; } = new();

    public LlmProviderOptions Ollama { get; set; } = new();

    public LlmProviderOptions Fake { get; set; } = new();

    public LlmProviderOptions For(LlmProvider provider) => provider switch
    {
        LlmProvider.Anthropic => Anthropic,
        LlmProvider.Openai => OpenAI,
        LlmProvider.Ollama => Ollama,
        LlmProvider.Fake => Fake,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}

public sealed class LlmProviderOptions
{
    /// <summary>FR-157: whether the provider is offered and can be used on this deployment (default <c>true</c>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>FR-158, FR-160: the provider's model catalog (§4.24), shipped in <c>appsettings.json</c>.</summary>
    public List<LlmCatalogModelOptions> Catalog { get; set; } = [];

    /// <summary>System API key (Anthropic, OpenAI). Never logged or returned.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Endpoint override (all providers; Ollama has an environment-dependent default).</summary>
    public string? BaseUrl { get; set; }

    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Default model per agent, keyed by <see cref="AgentKind"/> name (PascalCase or snake_case).</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One model catalog entry (§4.24): <c>Llm:Providers:&lt;P&gt;:Catalog:&lt;n&gt;</c>.</summary>
public sealed class LlmCatalogModelOptions
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name (defaults to the id).</summary>
    public string? Name { get; set; }

    public int MaxOutputTokens { get; set; }

    public int ContextTokens { get; set; }

    public bool Recommended { get; set; }
}

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
                    failures.Add($"Llm:Providers:{provider}:Models:{agent} is not an agent ({string.Join(", ", Enum.GetNames<AgentKind>())}).");
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

/// <summary>What the system LLM configuration offers: the default provider, which providers have a system key,
/// and the default models per provider and agent (FR-9, FR-22).</summary>
public interface ILlmProviderCatalog
{
    /// <summary><c>Llm:DefaultProvider</c>.</summary>
    LlmProvider ConfiguredDefault { get; }

    /// <summary><c>Llm:AllowSystemKeys</c> (D-V2-5): whether system keys may be used.</summary>
    bool AllowSystemKeys { get; }

    /// <summary>Whether a usable system API key exists for the provider: one is configured and
    /// <see cref="AllowSystemKeys"/> is on.</summary>
    bool HasSystemKey(LlmProvider provider);

    /// <summary>Whether a non-blank system API key is configured for the provider, allowed or not (admin display).</summary>
    bool IsSystemKeySet(LlmProvider provider);

    string DefaultModel(LlmProvider provider);

    /// <summary>Default models per agent for a provider, keyed by the agent's wire name (<c>idea_refinement</c>).</summary>
    IReadOnlyDictionary<string, string> DefaultAgentModels(LlmProvider provider);

    /// <summary>
    /// The provider of a project without saved LLM settings: the new-project default for a user with no connections
    /// (<see cref="DefaultForNewProject"/>), else the configured default (validated to be enabled).
    /// </summary>
    LlmProvider FallbackProvider { get; }

    /// <summary>FR-157: whether <c>Llm:Providers:&lt;P&gt;:Enabled</c> is on.</summary>
    bool IsEnabled(LlmProvider provider);

    /// <summary>FR-157: the enabled providers, in declaration order (Anthropic, OpenAI, Ollama, Fake).</summary>
    IReadOnlyList<LlmProvider> EnabledProviders { get; }

    /// <summary>FR-158, §4.24: the provider's model catalog, in configuration order.</summary>
    IReadOnlyList<LlmCatalogModel> Catalog(LlmProvider provider);

    /// <summary>FR-158, §4.24: whether a user with <paramref name="userConnections"/> connections for the provider can use
    /// it (<see cref="LlmProviderRules.Available"/>); <c>null</c> when the provider is disabled.</summary>
    LlmProviderAvailability? Availability(LlmProvider provider, int userConnections);

    /// <summary>
    /// §4.24 (amends FR-5): the provider of a project created by a user who has a connection for each entry of
    /// <paramref name="connectionProviders"/> (<see cref="ProjectSettingsRules.DefaultForNewProject"/>), or
    /// <c>LLM_PROVIDER_UNAVAILABLE</c>.
    /// </summary>
    Result<LlmProvider> DefaultForNewProject(IReadOnlyCollection<LlmProvider> connectionProviders);

    /// <summary>The Ollama endpoint: <c>Llm:Providers:Ollama:BaseUrl</c>, else host.docker.internal in a container
    /// or localhost for the dev loop.</summary>
    Uri OllamaBaseUrl { get; }

    /// <summary>The system API key of a provider (Anthropic, OpenAI), or <c>null</c> (none, or system keys not allowed).
    /// Never log it.</summary>
    string? SystemApiKey(LlmProvider provider);

    /// <summary>The configured price of a model (<c>Llm:Pricing</c>), or <c>null</c> when unpriced.</summary>
    ModelPrice? PriceOf(string model);
}

internal sealed class LlmProviderCatalog(IOptions<LlmOptions> options) : ILlmProviderCatalog
{
    public const string ContainerOllamaUrl = "http://host.docker.internal:11434";
    public const string LocalOllamaUrl = "http://localhost:11434";

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
            if (TryParseAgent(agent, out var kind) && !string.IsNullOrWhiteSpace(model))
            {
                result[kind.ToWire()] = model.Trim();
            }
        }

        return result;
    }

    public static bool TryParseProvider(string? value, out LlmProvider provider) =>
        EnumStrings.TryParse(value?.Trim().ToLowerInvariant(), out provider);

    public static bool TryParseAgent(string? value, out AgentKind agent) =>
        Enum.TryParse(value, ignoreCase: true, out agent) && Enum.IsDefined(agent)
            ? !int.TryParse(value, out _)
            : EnumStrings.TryParse(value, out agent);
}
