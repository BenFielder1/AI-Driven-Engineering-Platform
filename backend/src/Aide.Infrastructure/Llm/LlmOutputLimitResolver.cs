using System.Collections.Concurrent;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Infrastructure.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Llm;

/// <summary>FR-160: the effective output token limit of an agent's LLM calls, per provider and model (§4.24).</summary>
public interface ILlmOutputLimits
{
    /// <summary>
    /// <see cref="LlmOutputLimits.Resolve"/> over the provider's catalog, the discovered limit of an unlisted Anthropic model
    /// (looked up with <paramref name="apiKey"/>, cached; a failure is ignored), <c>Llm:MaxOutputTokensCeiling</c> and
    /// <c>Llm:MaxOutputTokens</c>.
    /// </summary>
    Task<int> ResolveAsync(LlmProvider provider, string model, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// FR-160: what Anthropic reports about a model: <c>max_tokens</c> of <c>GET {BaseUrl}/v1/models/{id}</c>, or <c>null</c>
/// when it can't be read. Behind an interface so tests never reach Anthropic (CLAUDE.md rule 10).
/// </summary>
public interface IAnthropicModelInfo
{
    /// <summary>The named HttpClient used for the lookup (tests swap its handler).</summary>
    const string HttpClientName = "anthropic-model-info";

    Task<int?> MaxOutputTokensAsync(string model, string apiKey, CancellationToken cancellationToken);
}

internal sealed class LlmOutputLimitResolver(ILlmProviderCatalog catalog, IAnthropicModelInfo anthropic, IOptions<LlmOptions> options) : ILlmOutputLimits
{
    public async Task<int> ResolveAsync(LlmProvider provider, string model, string? apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var models = catalog.Catalog(provider);
        int? discovered = null;
        if (LlmOutputLimits.NeedsDiscovery(provider, model, models) && !string.IsNullOrWhiteSpace(apiKey))
        {
            discovered = await anthropic.MaxOutputTokensAsync(model.Trim(), apiKey.Trim(), cancellationToken);
        }

        var settings = options.Value;
        return LlmOutputLimits.Resolve(provider, model, models, discovered, settings.MaxOutputTokensCeiling, settings.MaxOutputTokens);
    }
}

/// <summary>FR-160: discovered Anthropic model limits, kept 24 hours per model id (process-wide).</summary>
internal sealed class AnthropicModelInfoCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (int MaxTokens, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string model, out int maxTokens)
    {
        if (_entries.TryGetValue(model, out var entry) && entry.ExpiresAt > time.GetUtcNow())
        {
            maxTokens = entry.MaxTokens;
            return true;
        }

        maxTokens = 0;
        return false;
    }

    public void Set(string model, int maxTokens) => _entries[model] = (maxTokens, time.GetUtcNow() + Lifetime);
}

/// <summary>
/// FR-160: <c>GET {BaseUrl}/v1/models/{id}</c> with the run's key, reading <c>max_tokens</c>. Successful lookups are cached
/// for 24 hours per model; failures (network, timeout, any non-2xx, an unreadable body) return <c>null</c> and aren't cached.
/// The key is never logged.
/// </summary>
internal sealed partial class AnthropicModelInfo(
    IHttpClientFactory httpClients,
    IOptions<LlmOptions> options,
    AnthropicModelInfoCache cache,
    ILogger<AnthropicModelInfo> logger) : IAnthropicModelInfo
{
    public async Task<int?> MaxOutputTokensAsync(string model, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (cache.TryGet(model, out var cached))
        {
            return cached;
        }

        var baseUrl = options.Value.Providers.Anthropic.BaseUrl is { Length: > 0 } configured ? configured : AnthropicLlmProvider.DefaultBaseUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, LlmHttp.Combine(baseUrl, $"v1/models/{Uri.EscapeDataString(model)}"));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicLlmProvider.ApiVersion);

        // A short lookup (Llm:KeyCheckTimeout): it only refines the limit, so it must never hold up the run for long.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.KeyCheckTimeout);
        try
        {
            using var http = httpClients.CreateClient(IAnthropicModelInfo.HttpClientName);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogLookupFailed(logger, model, (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("max_tokens", out var value)
                && value.TryGetInt32(out var maxTokens)
                && maxTokens > 0)
            {
                cache.Set(model, maxTokens);
                return maxTokens;
            }

            LogLookupFailed(logger, model, (int)response.StatusCode);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            var exceptionType = ex.GetType().Name;
            LogLookupError(logger, model, exceptionType);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic model {Model} limit lookup returned HTTP {Status} without max_tokens; using the fallback output limit")]
    private static partial void LogLookupFailed(ILogger logger, string model, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic model {Model} limit lookup failed ({ExceptionType}); using the fallback output limit")]
    private static partial void LogLookupError(ILogger logger, string model, string exceptionType);
}
