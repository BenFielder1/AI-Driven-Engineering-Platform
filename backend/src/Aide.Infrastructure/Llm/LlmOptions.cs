using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Llm;

/// <summary>
/// System LLM configuration (section <c>Llm</c>, FR-22, §11 Q4). Keys:
/// <c>Llm:DefaultProvider</c> (anthropic|openai|ollama|fake, default anthropic);
/// <c>Llm:Providers:{Anthropic|OpenAI|Ollama|Fake}:DefaultModel</c>;
/// <c>Llm:Providers:{Provider}:Models:{AgentKind}</c> (e.g. <c>Models:IdeaRefinement</c>);
/// <c>Llm:Providers:Anthropic:ApiKey</c>, <c>Llm:Providers:OpenAI:ApiKey</c> (secrets: user-secrets / secrets.env only);
/// <c>Llm:Providers:Ollama:BaseUrl</c>. Environment variables use <c>__</c>, e.g. <c>Llm__Providers__Anthropic__ApiKey</c>.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string DefaultProvider { get; set; } = "anthropic";

    public LlmProvidersOptions Providers { get; set; } = new();
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
    /// <summary>System API key (Anthropic, OpenAI). Never logged or returned.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Endpoint (Ollama).</summary>
    public string? BaseUrl { get; set; }

    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Default model per agent, keyed by <see cref="AgentKind"/> name (PascalCase or snake_case).</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Validates <see cref="LlmOptions"/> at start-up: a known default provider and a default model for every provider.</summary>
internal sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        var failures = new List<string>();
        if (!LlmProviderCatalog.TryParseProvider(options.DefaultProvider, out _))
        {
            failures.Add($"Llm:DefaultProvider must be one of: {string.Join(", ", EnumStrings.Values<LlmProvider>())}.");
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

        if (!string.IsNullOrWhiteSpace(options.Providers.Ollama.BaseUrl) && !Uri.TryCreate(options.Providers.Ollama.BaseUrl, UriKind.Absolute, out _))
        {
            failures.Add("Llm:Providers:Ollama:BaseUrl must be an absolute URL.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>What the system LLM configuration offers: the default provider, which providers have a system key,
/// and the default models per provider and agent (FR-9, FR-22).</summary>
public interface ILlmProviderCatalog
{
    /// <summary><c>Llm:DefaultProvider</c>.</summary>
    LlmProvider ConfiguredDefault { get; }

    /// <summary>Whether a non-blank system API key is configured for the provider.</summary>
    bool HasSystemKey(LlmProvider provider);

    string DefaultModel(LlmProvider provider);

    /// <summary>Default models per agent for a provider, keyed by the agent's wire name (<c>idea_refinement</c>).</summary>
    IReadOnlyDictionary<string, string> DefaultAgentModels(LlmProvider provider);

    /// <summary>The provider a new project gets: the configured default if usable, else Fake (FR-5).</summary>
    LlmProvider DefaultForNewProject { get; }

    /// <summary>The Ollama endpoint: <c>Llm:Providers:Ollama:BaseUrl</c>, else host.docker.internal in a container
    /// or localhost for the dev loop.</summary>
    Uri OllamaBaseUrl { get; }
}

internal sealed class LlmProviderCatalog(IOptions<LlmOptions> options) : ILlmProviderCatalog
{
    public const string ContainerOllamaUrl = "http://host.docker.internal:11434";
    public const string LocalOllamaUrl = "http://localhost:11434";

    private readonly LlmOptions _options = options.Value;

    public LlmProvider ConfiguredDefault =>
        TryParseProvider(_options.DefaultProvider, out var provider) ? provider : LlmProvider.Fake;

    public LlmProvider DefaultForNewProject =>
        ProjectSettingsRules.DefaultProviderForNewProject(ConfiguredDefault, HasSystemKey(ConfiguredDefault));

    public Uri OllamaBaseUrl => new(
        _options.Providers.Ollama.BaseUrl is { Length: > 0 } url ? url
        : InContainer ? ContainerOllamaUrl
        : LocalOllamaUrl);

    private static bool InContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    public bool HasSystemKey(LlmProvider provider) =>
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
