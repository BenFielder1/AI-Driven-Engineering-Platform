using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Infrastructure.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// Shared HTTP plumbing for the real providers (FR-22, FR-25): JSON in and out, and one failure classification —
/// timeouts and connection errors are transient, HTTP statuses follow <see cref="AgentRunRules.IsTransientStatus"/>,
/// and an unreadable success body is permanent (retrying would only spend more tokens). The worker does the retrying.
/// Error text never carries credentials (NFR-SEC): 401/403 get a fixed message and other provider messages are redacted.
/// </summary>
internal static class LlmHttp
{
    public const int MaxErrorDetailLength = 300;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Uri Combine(string baseUrl, string relative) =>
        new(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), relative);

    public static async Task<JsonDocument> PostAsync(
        HttpClient http,
        string providerName,
        Uri url,
        object body,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        configure?.Invoke(request);

        string text;
        int status;
        bool success;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            status = (int)response.StatusCode;
            success = response.IsSuccessStatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmProviderException($"{providerName} did not answer within the request timeout.", transient: true, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException($"{providerName} could not be reached ({ex.HttpRequestError}).", transient: true, innerException: ex);
        }

        if (status is 401 or 403)
        {
            // NFR-SEC: never relay what the provider says about a rejected key (OpenAI echoes a fragment of it).
            throw new LlmProviderException($"{providerName} rejected the API key (HTTP {status}).", transient: false, status);
        }

        if (!success)
        {
            var detail = ErrorDetail(text);
            throw new LlmProviderException(
                detail is null ? $"{providerName} returned HTTP {status}." : $"{providerName} returned HTTP {status}: {detail}",
                AgentRunRules.IsTransientStatus(status),
                status);
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException($"{providerName} returned a response that isn't JSON.", transient: false, status, innerException: ex);
        }
    }

    /// <summary>
    /// FR-78: a minimal authenticated <c>GET</c> (list models): 2xx is valid, 401/403 rejected, anything else (network,
    /// timeout, 429, 5xx, other statuses) unavailable. The response body is never read or relayed.
    /// </summary>
    public static async Task<LlmKeyCheck> CheckKeyAsync(
        HttpClient http,
        string providerName,
        Uri url,
        Action<HttpRequestMessage> configure,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure(request);

        // A short per-check timeout (Llm:KeyCheckTimeout), not the client's completion timeout: a user is waiting.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new LlmKeyCheck(LlmKeyCheckOutcome.Valid, status, $"{providerName} accepted the key."),
                401 or 403 => new LlmKeyCheck(LlmKeyCheckOutcome.Rejected, status, $"{providerName} rejected the API key (HTTP {status})."),
                _ => new LlmKeyCheck(LlmKeyCheckOutcome.Unavailable, status, $"{providerName} returned HTTP {status} while checking the key."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlmKeyCheck(LlmKeyCheckOutcome.Unavailable, null, $"{providerName} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new LlmKeyCheck(LlmKeyCheckOutcome.Unavailable, null, $"{providerName} could not be reached ({ex.HttpRequestError}).");
        }
    }

    public static LlmProviderException Unreadable(string providerName, string what) =>
        new($"{providerName} returned an unexpected response: {what}.", transient: false);

    public static int Int(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return 0;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
    }

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The provider's error message (<c>{"error":{"message":…}}</c> or <c>{"error":"…"}</c>), with key-like
    /// tokens redacted (NFR-SEC), shortened.</summary>
    private static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                message = error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString(),
                    JsonValueKind.Object => String(error, "message") ?? String(error, "type"),
                    _ => null,
                };
            }

            message ??= String(doc.RootElement, "message");
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy's HTML page): no detail.
        }

        if (message is null)
        {
            return null;
        }

        var oneLine = SecretRedaction.Redact(message.ReplaceLineEndings(" ")).Trim();
        return oneLine.Length > MaxErrorDetailLength ? oneLine[..MaxErrorDetailLength] + "…" : oneLine;
    }
}

/// <summary>
/// Anthropic Messages API (FR-22): <c>POST {BaseUrl}/v1/messages</c> with <c>x-api-key</c> and
/// <c>anthropic-version: 2023-06-01</c>. Text blocks of the reply are concatenated.
/// </summary>
public sealed class AnthropicLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string ApiVersion = "2023-06-01";
    private const string Name = "Anthropic";

    public LlmProvider Kind => LlmProvider.Anthropic;

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = RequireKey(request, Name);
        var baseUrl = options.Value.Providers.Anthropic.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        var body = new
        {
            model = request.Model,
            maxTokens = request.MaxTokens,
            system = request.System,
            messages = request.Messages.Select(m => new { role = Role(m.Role), content = m.Content }).ToArray(),
        };

        using var doc = await LlmHttp.PostAsync(
            http,
            Name,
            LlmHttp.Combine(baseUrl, "v1/messages"),
            body,
            r =>
            {
                r.Headers.Add("x-api-key", apiKey);
                r.Headers.Add("anthropic-version", ApiVersion);
            },
            cancellationToken);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw LlmHttp.Unreadable(Name, "no content");
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (LlmHttp.String(block, "type") == "text" && LlmHttp.String(block, "text") is { } part)
            {
                text.Append(part);
            }
        }

        return new LlmCompletion(
            text.ToString(),
            LlmHttp.Int(root, "usage", "input_tokens"),
            LlmHttp.Int(root, "usage", "output_tokens"),
            LlmHttp.String(root, "stop_reason") ?? "unknown");
    }

    /// <summary>FR-78: <c>GET {BaseUrl}/v1/models?limit=1</c> with the key.</summary>
    public Task<LlmKeyCheck> CheckKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        var baseUrl = options.Value.Providers.Anthropic.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        return LlmHttp.CheckKeyAsync(
            http,
            Name,
            LlmHttp.Combine(baseUrl, "v1/models?limit=1"),
            r =>
            {
                r.Headers.Add("x-api-key", apiKey.Trim());
                r.Headers.Add("anthropic-version", ApiVersion);
            },
            options.Value.KeyCheckTimeout,
            cancellationToken);
    }

    internal static string RequireKey(LlmRequest request, string providerName) =>
        string.IsNullOrWhiteSpace(request.ApiKey)
            ? throw new LlmProviderException($"{providerName} is not configured: no API key.", transient: false, errorCode: Aide.Domain.Common.ErrorCodes.ProviderNotConfigured)
            : request.ApiKey.Trim();

    private static string Role(LlmRole role) => role == LlmRole.Assistant ? "assistant" : "user";
}

/// <summary>
/// OpenAI Chat Completions (FR-22): <c>POST {BaseUrl}/chat/completions</c> (default base https://api.openai.com/v1)
/// with a Bearer key; the system prompt is the first message.
/// </summary>
public sealed class OpenAiLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const string Name = "OpenAI";

    public LlmProvider Kind => LlmProvider.Openai;

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = AnthropicLlmProvider.RequireKey(request, Name);
        var baseUrl = options.Value.Providers.OpenAI.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        var messages = new List<object> { new { role = "system", content = request.System } };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role == LlmRole.Assistant ? "assistant" : "user", content = m.Content }));
        var body = new { model = request.Model, maxCompletionTokens = request.MaxTokens, messages };

        using var doc = await LlmHttp.PostAsync(
            http,
            Name,
            LlmHttp.Combine(baseUrl, "chat/completions"),
            body,
            r => r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey),
            cancellationToken);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw LlmHttp.Unreadable(Name, "no choices");
        }

        var choice = choices[0];
        var message = choice.TryGetProperty("message", out var m) ? m : default;
        var text = LlmHttp.String(message, "content")
            ?? throw new LlmProviderException(
                LlmHttp.String(message, "refusal") is { } refusal ? $"{Name} refused the request: {refusal}" : $"{Name} returned no text.",
                transient: false);

        return new LlmCompletion(
            text,
            LlmHttp.Int(root, "usage", "prompt_tokens"),
            LlmHttp.Int(root, "usage", "completion_tokens"),
            LlmHttp.String(choice, "finish_reason") ?? "unknown");
    }

    /// <summary>FR-78: <c>GET {BaseUrl}/models</c> with the Bearer key.</summary>
    public Task<LlmKeyCheck> CheckKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        var baseUrl = options.Value.Providers.OpenAI.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        return LlmHttp.CheckKeyAsync(
            http,
            Name,
            LlmHttp.Combine(baseUrl, "models"),
            r => r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim()),
            options.Value.KeyCheckTimeout,
            cancellationToken);
    }
}

/// <summary>FR-78: <see cref="ILlmKeyValidator"/> over the Anthropic and OpenAI providers' typed HttpClients.</summary>
internal sealed class LlmKeyValidator(IServiceProvider services) : ILlmKeyValidator
{
    public Task<LlmKeyCheck> CheckAsync(LlmProvider provider, string apiKey, CancellationToken cancellationToken) => provider switch
    {
        LlmProvider.Anthropic => services.GetRequiredService<AnthropicLlmProvider>().CheckKeyAsync(apiKey, cancellationToken),
        LlmProvider.Openai => services.GetRequiredService<OpenAiLlmProvider>().CheckKeyAsync(apiKey, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Only Anthropic and OpenAI keys are checked."),
    };
}

/// <summary>
/// Ollama chat API (FR-22): <c>POST {BaseUrl}/api/chat</c> with <c>stream: false</c>; no key. The output cap is
/// <c>options.num_predict</c>; token counts come from <c>prompt_eval_count</c> / <c>eval_count</c>.
/// </summary>
public sealed class OllamaLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider
{
    private const string Name = "Ollama";

    public LlmProvider Kind => LlmProvider.Ollama;

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<object> { new { role = "system", content = request.System } };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role == LlmRole.Assistant ? "assistant" : "user", content = m.Content }));
        var body = new
        {
            model = request.Model,
            messages,
            stream = false,
            options = new { numPredict = request.MaxTokens },
        };

        using var doc = await LlmHttp.PostAsync(
            http,
            Name,
            LlmHttp.Combine(options.Value.ResolveOllamaBaseUrl().ToString(), "api/chat"),
            body,
            null,
            cancellationToken);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out var message))
        {
            throw LlmHttp.Unreadable(Name, "no message");
        }

        var text = LlmHttp.String(message, "content") ?? throw LlmHttp.Unreadable(Name, "no message content");
        return new LlmCompletion(
            text,
            LlmHttp.Int(root, "prompt_eval_count"),
            LlmHttp.Int(root, "eval_count"),
            LlmHttp.String(root, "done_reason") ?? "stop");
    }
}
