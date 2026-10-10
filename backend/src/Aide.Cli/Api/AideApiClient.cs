using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aide.Cli.Api;

/// <summary>JSON settings matching the API: camelCase properties, snake_case enum strings, nulls omitted on write.</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Re-indents an API response body for `--json` output (values are passed through untouched).</summary>
    public static string Indent(string json) =>
        string.IsNullOrWhiteSpace(json) ? "null" : JsonNode.Parse(json)?.ToJsonString(Pretty) ?? "null";

    public static string ToIndentedJson(JsonNode? node) => node?.ToJsonString(Pretty) ?? "null";

    public static string ToCompactJson(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";
}

/// <summary>A successful API response: the typed body plus the raw JSON (printed verbatim with --json).</summary>
internal sealed record ApiResult<T>(T Value, string Raw);

/// <summary>A non-success API response, carrying the ProblemDetails body when there is one.</summary>
internal sealed class ApiException(HttpStatusCode statusCode, string? reasonPhrase, ProblemDetailsDto? problem, TimeSpan? retryAfter)
    : CliException(Describe(statusCode, reasonPhrase, problem), ExitCodes.ForStatus(statusCode))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public ProblemDetailsDto? Problem { get; } = problem;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public string? ErrorCode => Problem?.ErrorCode;

    private static string Describe(HttpStatusCode statusCode, string? reasonPhrase, ProblemDetailsDto? problem)
    {
        var title = problem?.Title ?? reasonPhrase ?? statusCode.ToString();
        return $"{(int)statusCode} {title}";
    }
}

/// <summary>Typed HttpClient wrapper for the AIDE API, authenticated with a personal API token.</summary>
internal sealed class AideApiClient : IDisposable
{
    private readonly HttpClient _http;

    public AideApiClient(HttpMessageHandler handler, Uri baseUri, string token)
    {
        BaseUri = baseUri;
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("aide-cli", Version));
    }

    public Uri BaseUri { get; }

    internal static string Version { get; } =
        (typeof(AideApiClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, path, body: null, cancellationToken);

    public Task<ApiResult<T>> PostAsync<T>(string path, object? body, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Post, path, body, cancellationToken);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ApiJson.Options), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Could not reach the AIDE API at {BaseUri}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException($"The AIDE API at {BaseUri} did not respond in time.");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(response.StatusCode, response.ReasonPhrase, TryReadProblem(raw), ReadRetryAfter(response));
            }

            T? value;
            try
            {
                value = string.IsNullOrWhiteSpace(raw) ? default : JsonSerializer.Deserialize<T>(raw, ApiJson.Options);
            }
            catch (JsonException ex)
            {
                throw new CliException($"The API returned a response the CLI could not read ({ex.Message}). Is {BaseUri} an AIDE API?");
            }

            if (value is null)
            {
                throw new CliException($"The API returned an empty response for {method} {path}.");
            }

            return new ApiResult<T>(value, raw);
        }
    }

    private static ProblemDetailsDto? TryReadProblem(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProblemDetailsDto>(raw, ApiJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>Builds `path?k=v&amp;…`, skipping null values and escaping the rest.</summary>
    public static string WithQuery(string path, params (string Key, object? Value)[] query)
    {
        var parts = query
            .Where(q => q.Value is not null)
            .Select(q => $"{q.Key}={Uri.EscapeDataString(Convert.ToString(q.Value, CultureInfo.InvariantCulture)!)}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    public void Dispose() => _http.Dispose();
}
