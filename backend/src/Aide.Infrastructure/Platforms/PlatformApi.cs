using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;

namespace Aide.Infrastructure.Platforms;

/// <summary>
/// A platform token in use (FR-142): the decrypted token, the account the connection is for (or the one asked for on
/// save) and what the platform said about the token before. Never logged.
/// </summary>
public sealed record PlatformCredential(string Token, string? AccountId, PlatformConnectionMetadata Metadata)
{
    public PlatformCredential(string token)
        : this(token, null, new PlatformConnectionMetadata())
    {
    }

    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"PlatformCredential {{ AccountId = {AccountId} }}";
}

/// <summary>
/// What checking a token found (FR-142): the user it belongs to (when the platform says), the teams, accounts, workspaces
/// or organisations it reaches, the rest of the metadata (Vercel team, Cloudflare policies, missing permissions, token
/// kind), its scopes and expiry.
/// </summary>
public sealed record PlatformTokenCheck(
    string? UserId,
    string? UserName,
    IReadOnlyList<PlatformAccountRef> Accounts,
    PlatformConnectionMetadata Metadata,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>An entry of a picker (FR-142 <c>GET …/resources</c>): id, name, URL when there is one, and a short detail.</summary>
public sealed record PlatformResource(string Id, string Name, string? Url = null, string? Detail = null);

/// <summary>
/// A failed platform request (FR-142): how (<see cref="Failure"/>), the HTTP status, a cleaned message (never a token),
/// the missing permissions the platform named and, when rate limited, when to try again.
/// </summary>
public sealed class PlatformApiException : Exception
{
    public PlatformApiException()
    {
    }

    public PlatformApiException(string message)
        : base(message)
    {
    }

    public PlatformApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PlatformApiException(PlatformFailure failure, int? status, string message, IReadOnlyList<string>? missingPermissions = null, DateTimeOffset? retryAt = null)
        : base(message)
    {
        Failure = failure;
        Status = status;
        MissingPermissions = missingPermissions ?? [];
        RetryAt = retryAt;
    }

    public PlatformFailure Failure { get; }

    public int? Status { get; }

    public IReadOnlyList<string> MissingPermissions { get; } = [];

    public DateTimeOffset? RetryAt { get; }
}

/// <summary>
/// FR-142, §4.20, §4.21: one provider's connection checks and picker data, on the provider's fixed API host (§8: never
/// configurable or user-supplied). Never called for deployments (those are jobs). Failures throw
/// <see cref="PlatformApiException"/>.
/// </summary>
public interface IPlatformConnectionValidator
{
    PlatformProvider Provider { get; }

    /// <summary>Who the token belongs to and what it reaches (§4.20 "Token (pasted) and validation").</summary>
    Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken);

    /// <summary>The teams, accounts, workspaces or organisations the token reaches.</summary>
    Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken);

    /// <summary>
    /// Picker data of <paramref name="type"/> (one of <see cref="PlatformConnectionRules.ResourceTypes"/>), in
    /// <paramref name="accountId"/> when the platform scopes it to an account.
    /// </summary>
    Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken);
}

/// <summary>§9: the validator for a provider: the Fake for every provider but Vercel when <c>Deployments:Provider=Fake</c>
/// (Vercel goes through the Fake deployment provider, as in v2).</summary>
public interface IPlatformConnectionValidators
{
    IPlatformConnectionValidator ForProvider(PlatformProvider provider);
}

/// <summary>The fixed API hosts and HttpClient names of the platforms (§8).</summary>
public static class PlatformHosts
{
    public const string NetlifyClient = "netlify";
    public const string NetlifyBaseUrl = "https://api.netlify.com/api/v1/";

    public const string CloudflareClient = "cloudflare";
    public const string CloudflareBaseUrl = "https://api.cloudflare.com/client/v4/";

    public const string RenderClient = "render";
    public const string RenderBaseUrl = "https://api.render.com/v1/";

    public const string RailwayClient = "railway";
    public const string RailwayBaseUrl = "https://backboard.railway.com/";

    public const string SupabaseClient = "supabase";
    public const string SupabaseBaseUrl = "https://api.supabase.com/v1/";

    /// <summary>Every platform client name, with its base address (Vercel's is registered with the deployment provider).</summary>
    public static IReadOnlyList<(string Name, string BaseUrl)> All { get; } =
    [
        (NetlifyClient, NetlifyBaseUrl),
        (CloudflareClient, CloudflareBaseUrl),
        (RenderClient, RenderBaseUrl),
        (RailwayClient, RailwayBaseUrl),
        (SupabaseClient, SupabaseBaseUrl),
    ];
}

/// <summary>
/// JSON-over-HTTP for the platform validators: the token only in the Authorization header, bodies as JSON, failures
/// classified by <see cref="PlatformConnectionRules.Classify"/> with the platform's message cleaned (control characters
/// removed, at most 300 characters), its missing permissions (Supabase <c>missing_permissions</c>) and its rate-limit
/// reset (<c>Retry-After</c>, <c>X-RateLimit-Reset</c>).
/// </summary>
public sealed class PlatformHttp(IHttpClientFactory clients, TimeProvider time)
{
    private const int MaxMessageLength = 300;

    public async Task<JsonDocument> SendAsync(
        PlatformProvider provider, string clientName, HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(token);
        var name = PlatformConnectionRules.DisplayName(provider);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(clientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new PlatformApiException(PlatformFailure.Upstream, null, $"Could not reach {name}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlatformApiException(PlatformFailure.Upstream, null, $"Could not reach {name}: the request timed out.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                }
                catch (JsonException)
                {
                    throw new PlatformApiException(PlatformFailure.Upstream, (int)response.StatusCode, $"{name} returned an unreadable reply.");
                }
            }

            throw Failure(provider, response, text);
        }
    }

    private PlatformApiException Failure(PlatformProvider provider, HttpResponseMessage response, string text)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? message = null;
        List<string> missing = [];
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            message = Json.Text(root, "message") ?? Json.Text(root, "error_description") ?? Json.Text(root, "error");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                // Cloudflare: {"errors": [{"code": 1000, "message": "Invalid API Token"}]}.
                var first = errors[0];
                message ??= Json.Text(first, "message");
                code = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("code", out var c)
                    ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.ValueKind == JsonValueKind.String ? c.GetString() : null
                    : null;
            }

            missing = MissingPermissions(root);
        }
        catch (JsonException)
        {
        }

        var failure = PlatformConnectionRules.Classify(provider, status, code);
        if (failure == PlatformFailure.Upstream && missing.Count > 0)
        {
            failure = PlatformFailure.MissingPermissions;
        }

        var cleaned = Clean(message ?? response.ReasonPhrase ?? "request failed");
        return new PlatformApiException(
            failure,
            status,
            $"{PlatformConnectionRules.DisplayName(provider)}: {cleaned}",
            missing,
            failure == PlatformFailure.RateLimited ? RetryAt(response) : null);
    }

    /// <summary>Supabase's <c>missing_permissions</c> (strings, or objects with a <c>name</c>), at the root or under <c>error</c>.</summary>
    private static List<string> MissingPermissions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var holder = root.TryGetProperty("missing_permissions", out _) ? root
            : root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error
            : root;
        if (!holder.TryGetProperty("missing_permissions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. list.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : Json.Text(p, "name"))
                .OfType<string>()
                .Select(Clean)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(50),
        ];
    }

    private DateTimeOffset RetryAt(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return now + delta;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset) && reset > 0)
        {
            // An epoch (seconds) or, as some platforms send, the seconds until the reset.
            return reset > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(reset) : now.AddSeconds(reset);
        }

        return now + TimeSpan.FromMinutes(1);
    }

    internal static string Clean(string text)
    {
        var cleaned = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return cleaned.Length > MaxMessageLength ? cleaned[..MaxMessageLength] : cleaned;
    }
}

/// <summary>Reading platform JSON defensively.</summary>
internal static class Json
{
    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static JsonElement Property(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    /// <summary>The array at <paramref name="element"/> itself, or under one of <paramref name="properties"/>.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, params string[] properties)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray();
        }

        foreach (var name in properties)
        {
            var value = Property(element, name);
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray();
            }
        }

        return [];
    }

    public static DateTimeOffset? Time(JsonElement element, string property) =>
        Text(element, property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
