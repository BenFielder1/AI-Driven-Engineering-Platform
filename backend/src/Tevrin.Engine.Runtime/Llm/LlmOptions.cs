using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Llm;

/// <summary>
/// System LLM configuration (section <c>Llm</c>, FR-22–FR-26, §11 Q4). Keys:
/// <list type="bullet">
/// <item><c>Llm:DefaultProvider</c> (anthropic|openai|ollama|fake, default anthropic);</item>
/// <item><c>Llm:Providers:{Anthropic|OpenAI|Ollama|Fake}:DefaultModel</c> and <c>…:Models:{agent}</c>;</item>
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

    /// <summary>The Ollama endpoint in a container (no <c>Llm:Providers:Ollama:BaseUrl</c>).</summary>
    public const string ContainerOllamaUrl = "http://host.docker.internal:11434";

    /// <summary>The Ollama endpoint in the dev loop (no <c>Llm:Providers:Ollama:BaseUrl</c>).</summary>
    public const string LocalOllamaUrl = "http://localhost:11434";

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
            ? ContainerOllamaUrl
            : LocalOllamaUrl);
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

    /// <summary>Default model per agent, keyed by the agent's configuration name. The host binds the names to agent keys
    /// (the SDLC accepts the key or its 0.6.1 enum member name in any case; FR-274).</summary>
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

/// <summary>What the system LLM configuration offers (FR-9, FR-22, FR-157, FR-158): the default provider, which providers
/// have a system key, and the default models per provider and agent. The host implements it with its project rules
/// (<c>ILlmProviderCatalog</c>).</summary>
public interface ILlmCatalog
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

    /// <summary>Default models per agent for a provider, keyed by the agent key.</summary>
    IReadOnlyDictionary<string, string> DefaultAgentModels(LlmProvider provider);

    /// <summary>FR-157: whether <c>Llm:Providers:&lt;P&gt;:Enabled</c> is on.</summary>
    bool IsEnabled(LlmProvider provider);

    /// <summary>FR-157: the enabled providers, in declaration order (Anthropic, OpenAI, Ollama, Fake).</summary>
    IReadOnlyList<LlmProvider> EnabledProviders { get; }

    /// <summary>FR-158, §4.24: the provider's model catalog, in configuration order.</summary>
    IReadOnlyList<LlmCatalogModel> Catalog(LlmProvider provider);

    /// <summary>The Ollama endpoint: <c>Llm:Providers:Ollama:BaseUrl</c>, else host.docker.internal in a container
    /// or localhost for the dev loop.</summary>
    Uri OllamaBaseUrl { get; }

    /// <summary>The system API key of a provider (Anthropic, OpenAI), or <c>null</c> (none, or system keys not allowed).
    /// Never log it.</summary>
    string? SystemApiKey(LlmProvider provider);

    /// <summary>The configured price of a model (<c>Llm:Pricing</c>), or <c>null</c> when unpriced.</summary>
    ModelPrice? PriceOf(string model);
}
