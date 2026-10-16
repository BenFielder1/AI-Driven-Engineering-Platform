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

    /// <summary>
    /// FR-71: the tool arguments as a JSON object for providers that take an object (Anthropic <c>input</c>, Ollama
    /// <c>arguments</c>); anything that isn't a JSON object becomes <c>{}</c>.
    /// </summary>
    public static JsonElement ObjectOrEmpty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Not JSON: sent as an empty object (the tool already reported the bad arguments).
        }

        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    /// <summary>A generated call id for providers that don't give one (Ollama): <c>call_{turn}_{index}</c>.</summary>
    public static string CallId(int turn, int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"call_{turn}_{index}");

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
public sealed class AnthropicLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider, IToolCallingLlmProvider
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

    /// <summary>Every Anthropic Messages model takes tools.</summary>
    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// FR-71: one Messages turn with <c>tools</c> (<c>name</c>, <c>description</c>, <c>input_schema</c>). Each earlier exchange
    /// is an <c>assistant</c> message (its text and <c>tool_use</c> blocks) followed by a <c>user</c> message of
    /// <c>tool_result</c> blocks (<c>tool_use_id</c>, <c>content</c>, <c>is_error</c>). A forced final answer sends
    /// <c>tool_choice: {"type":"none"}</c>. The reply's <c>tool_use</c> blocks are the calls; its text blocks the text.
    /// </summary>
    public async Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);
        var request = toolRequest.Request;
        var apiKey = RequireKey(request, Name);
        var baseUrl = options.Value.Providers.Anthropic.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        var messages = request.Messages.Select(m => (object)new { role = Role(m.Role), content = m.Content }).ToList();
        foreach (var exchange in toolRequest.Exchanges)
        {
            var blocks = new List<object>();
            if (!string.IsNullOrEmpty(exchange.AssistantText))
            {
                blocks.Add(new { type = "text", text = exchange.AssistantText });
            }

            blocks.AddRange(exchange.Calls.Select(c => (object)new { type = "tool_use", id = c.Id, name = c.Name, input = LlmHttp.ObjectOrEmpty(c.ArgumentsJson) }));
            messages.Add(new { role = "assistant", content = blocks });
            messages.Add(new
            {
                role = "user",
                content = exchange.Results.Select(r => new { type = "tool_result", toolUseId = r.CallId, content = r.Content, isError = r.IsError }).ToArray(),
            });
        }

        var body = new
        {
            model = request.Model,
            maxTokens = request.MaxTokens,
            system = request.System,
            messages,
            tools = toolRequest.Tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }).ToArray(),
            toolChoice = toolRequest.ForceFinalAnswer ? new { type = "none" } : null,
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
        var calls = new List<LlmToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            switch (LlmHttp.String(block, "type"))
            {
                case "text" when LlmHttp.String(block, "text") is { } part:
                    text.Append(part);
                    break;
                case "tool_use":
                    var arguments = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
                    calls.Add(new LlmToolCall(
                        LlmHttp.String(block, "id") ?? LlmHttp.CallId(toolRequest.Exchanges.Count + 1, calls.Count + 1),
                        LlmHttp.String(block, "name") ?? string.Empty,
                        arguments));
                    break;
            }
        }

        return new LlmToolTurn(
            text.ToString(),
            calls,
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
public sealed class OpenAiLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider, IToolCallingLlmProvider
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

    /// <summary>Chat Completions models take function tools.</summary>
    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// FR-71: one Chat Completions turn with <c>tools</c> (<c>{"type":"function","function":{name, description, parameters}}</c>).
    /// Each earlier exchange is an <c>assistant</c> message with <c>tool_calls</c> (<c>id</c>, <c>type: function</c>,
    /// <c>function.name</c>, <c>function.arguments</c> as a JSON string) followed by one <c>tool</c> message per result
    /// (<c>tool_call_id</c>, <c>content</c>). A forced final answer sends <c>tool_choice: "none"</c>.
    /// </summary>
    public async Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);
        var request = toolRequest.Request;
        var apiKey = AnthropicLlmProvider.RequireKey(request, Name);
        var baseUrl = options.Value.Providers.OpenAI.BaseUrl is { Length: > 0 } configured ? configured : DefaultBaseUrl;
        var messages = new List<object> { new { role = "system", content = request.System } };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role == LlmRole.Assistant ? "assistant" : "user", content = m.Content }));
        foreach (var exchange in toolRequest.Exchanges)
        {
            messages.Add(new
            {
                role = "assistant",
                content = string.IsNullOrEmpty(exchange.AssistantText) ? null : exchange.AssistantText,
                toolCalls = exchange.Calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.ArgumentsJson } }).ToArray(),
            });
            messages.AddRange(exchange.Results.Select(r => new { role = "tool", toolCallId = r.CallId, content = r.Content }));
        }

        var body = new
        {
            model = request.Model,
            maxCompletionTokens = request.MaxTokens,
            messages,
            tools = toolRequest.Tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.InputSchema } }).ToArray(),
            toolChoice = toolRequest.ForceFinalAnswer ? "none" : null,
        };

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
        var calls = new List<LlmToolCall>();
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.TryGetProperty("function", out var f) ? f : default;
                calls.Add(new LlmToolCall(
                    LlmHttp.String(call, "id") ?? LlmHttp.CallId(toolRequest.Exchanges.Count + 1, calls.Count + 1),
                    LlmHttp.String(function, "name") ?? string.Empty,
                    LlmHttp.String(function, "arguments") ?? "{}"));
            }
        }

        var text = LlmHttp.String(message, "content");
        if (text is null && calls.Count == 0)
        {
            throw new LlmProviderException(
                LlmHttp.String(message, "refusal") is { } refusal ? $"{Name} refused the request: {refusal}" : $"{Name} returned no text.",
                transient: false);
        }

        return new LlmToolTurn(
            text ?? string.Empty,
            calls,
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
public sealed class OllamaLlmProvider(HttpClient http, IOptions<LlmOptions> options) : ILlmProvider, IToolCallingLlmProvider
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

    /// <summary>
    /// FR-71: <c>POST {BaseUrl}/api/show</c> for the model; it takes tools when its <c>capabilities</c> list
    /// <c>tools</c>. An older Ollama without <c>capabilities</c>, or any failure, counts as no tool support (the run then
    /// falls back to a context-only prompt and records <c>tools_unsupported</c>).
    /// </summary>
    public async Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var doc = await LlmHttp.PostAsync(
                http,
                Name,
                LlmHttp.Combine(options.Value.ResolveOllamaBaseUrl().ToString(), "api/show"),
                new { model = request.Model },
                null,
                cancellationToken);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Array
                && capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "tools");
        }
        catch (LlmProviderException)
        {
            return false;
        }
    }

    /// <summary>
    /// FR-71: one <c>/api/chat</c> turn with <c>tools</c> (function definitions, as OpenAI). Each earlier exchange is an
    /// <c>assistant</c> message with <c>tool_calls</c> (<c>function.name</c>, <c>function.arguments</c> as an object) followed by
    /// one <c>tool</c> message per result (<c>tool_name</c>, <c>content</c>). A forced final answer omits the tools. Ollama
    /// may give no call ids, so missing ones are generated.
    /// </summary>
    public async Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);
        var request = toolRequest.Request;
        var messages = new List<object> { new { role = "system", content = request.System } };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role == LlmRole.Assistant ? "assistant" : "user", content = m.Content }));
        foreach (var exchange in toolRequest.Exchanges)
        {
            messages.Add(new
            {
                role = "assistant",
                content = exchange.AssistantText,
                toolCalls = exchange.Calls.Select(c => new { function = new { name = c.Name, arguments = LlmHttp.ObjectOrEmpty(c.ArgumentsJson) } }).ToArray(),
            });
            messages.AddRange(exchange.Results.Select(r => new
            {
                role = "tool",
                content = r.Content,
                toolName = exchange.Calls.FirstOrDefault(c => c.Id == r.CallId)?.Name ?? string.Empty,
            }));
        }

        var body = new
        {
            model = request.Model,
            messages,
            stream = false,
            tools = toolRequest.ForceFinalAnswer
                ? null
                : toolRequest.Tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.InputSchema } }).ToArray(),
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
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw LlmHttp.Unreadable(Name, "no message");
        }

        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.TryGetProperty("function", out var f) ? f : default;
                var arguments = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                    : "{}";
                calls.Add(new LlmToolCall(
                    LlmHttp.String(call, "id") ?? LlmHttp.CallId(toolRequest.Exchanges.Count + 1, calls.Count + 1),
                    LlmHttp.String(function, "name") ?? string.Empty,
                    arguments));
            }
        }

        return new LlmToolTurn(
            LlmHttp.String(message, "content") ?? string.Empty,
            calls,
            LlmHttp.Int(root, "prompt_eval_count"),
            LlmHttp.Int(root, "eval_count"),
            LlmHttp.String(root, "done_reason") ?? "stop");
    }
}
