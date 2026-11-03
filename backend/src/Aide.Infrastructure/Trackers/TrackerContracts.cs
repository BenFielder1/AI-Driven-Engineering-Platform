using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Platforms;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Trackers;

/// <summary>Issue tracker settings (section <c>Trackers</c>, §9).</summary>
public sealed class TrackerOptions
{
    public const string SectionName = "Trackers";

    /// <summary>The longest <see cref="SearchTimeout"/> accepted at startup.</summary>
    public static readonly TimeSpan MaxSearchTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <c>Fake</c>: the deterministic Fake for every tracker (Development, Testing and opted-in E2E; else <c>Deployments:AllowFake=true</c>).
    /// Unset or <c>Real</c>: the real adapters. Anything else fails startup.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>§8: tracker search and browse proxy a provider with this timeout (default 15 s; at most 2 minutes).</summary>
    public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// §8, FR-173: the cap on a whole <c>sources/check</c>, with <c>unknown</c> for what remains (default and maximum 10 s;
    /// tests set it lower).
    /// </summary>
    public TimeSpan CheckTimeout { get; set; } = SourceItemDriftRules.CheckTimeout;

    public bool UseFake => string.Equals(Provider?.Trim(), "Fake", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <see cref="Provider"/> is unset, <c>Real</c> or <c>Fake</c> (any case).</summary>
    public bool HasKnownProvider =>
        string.IsNullOrWhiteSpace(Provider) || UseFake || string.Equals(Provider.Trim(), "Real", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A tracker token in use (FR-165–FR-168), bound to where it may go: Jira's site and cloud id, or a GitLab instance. GitHub's
/// token is the user's for listing repositories and an installation's for reading issues. Never logged.
/// </summary>
public sealed record TrackerCredential(string Token)
{
    /// <summary>Jira: the Atlassian account email; the pair is sent as Basic auth.</summary>
    public string? Email { get; init; }

    public string? Site { get; init; }

    public string? CloudId { get; init; }

    public string? AcceptanceCriteriaField { get; init; }

    public Uri? InstanceBaseUrl { get; init; }

    /// <summary>Redacted: the token and email never reach a log.</summary>
    public override string ToString() => $"TrackerCredential {{ Site = {Site}, Instance = {InstanceBaseUrl} }}";
}

/// <summary>What checking a tracker token found. Never an email.</summary>
public sealed record TrackerTokenCheck(string? AccountId, string? AccountName, IReadOnlyList<string> Scopes)
{
    public string? Site { get; init; }

    public string? CloudId { get; init; }

    public string? Organization { get; init; }

    public string? OrganizationUrlKey { get; init; }

    public IReadOnlyList<string> MissingPermissions { get; init; } = [];
}

/// <summary>A project, team or repository to browse (FR-169).</summary>
public sealed record TrackerContainer(string Id, string Name, string? Detail = null, string? Url = null);

public sealed record TrackerContainerPage(IReadOnlyList<TrackerContainer> Items, string? NextCursor);

/// <summary>A search: a native query, or structured filters, in a container, from a provider's cursor.</summary>
public sealed record TrackerSearch(string? ContainerId, TrackerFilter? Filter, string? Query, string? Cursor);

public sealed record TrackerItemPage(IReadOnlyList<SourceItem> Items, string? NextCursor);

/// <summary>A Jira field for the acceptance criteria picker (FR-165).</summary>
public sealed record TrackerField(string Id, string Name);

/// <summary>A failed tracker request: how, the HTTP status, a cleaned message (never a token) and when to try again if rate limited.</summary>
public sealed class TrackerApiException : Exception
{
    public TrackerApiException()
    {
    }

    public TrackerApiException(string message)
        : base(message)
    {
    }

    public TrackerApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TrackerApiException(TrackerFailure failure, int? status, string message, DateTimeOffset? retryAt = null)
        : base(message)
    {
        Failure = failure;
        Status = status;
        RetryAt = retryAt;
    }

    public TrackerFailure Failure { get; }

    public int? Status { get; }

    public DateTimeOffset? RetryAt { get; }

    /// <summary>FR-167: the App settings link, on <see cref="TrackerFailure.AppPermissionMissing"/>.</summary>
    public string? Link { get; init; }
}

/// <summary>
/// §4.26: one tracker behind one abstraction, read-only (§8: no code path writes to a tracker). Failures throw
/// <see cref="TrackerApiException"/>. Adapters return items as the tracker has them; normalising is the import's.
/// </summary>
public interface IIssueTrackerProvider
{
    TrackerProvider Provider { get; }

    /// <summary>Who the token belongs to and whether it can read (FR-165, FR-166).</summary>
    Task<TrackerTokenCheck> ValidateAsync(TrackerCredential credential, CancellationToken cancellationToken);

    /// <summary>Projects, teams or repositories to browse (FR-169).</summary>
    Task<TrackerContainerPage> ListContainersAsync(TrackerCredential credential, string? search, string? cursor, CancellationToken cancellationToken);

    /// <summary>One page of matching item summaries (without descriptions, comments or children). Done items are hidden unless the filter asks for them.</summary>
    Task<TrackerItemPage> SearchAsync(TrackerCredential credential, TrackerSearch search, CancellationToken cancellationToken);

    /// <summary>
    /// The selected items in full, with their children when asked, up to one more than <see cref="SourceItemLimits.MaxItems"/>
    /// (so the caller can refuse them). A key the tracker doesn't return is simply absent.
    /// </summary>
    Task<IReadOnlyList<SourceItem>> GetItemsAsync(TrackerCredential credential, TrackerSelection selection, TrackerImportOptions options, CancellationToken cancellationToken);

    /// <summary>The fields a Jira site has (FR-165); empty for other trackers.</summary>
    Task<IReadOnlyList<TrackerField>> ListFieldsAsync(TrackerCredential credential, CancellationToken cancellationToken);
}

/// <summary>The adapter for a tracker: the Fake for all of them under <c>Trackers:Provider=Fake</c>.</summary>
public interface IIssueTrackers
{
    bool UsesFake { get; }

    IIssueTrackerProvider ForProvider(TrackerProvider provider);
}

/// <summary>§8 SSRF: the fixed hosts and HttpClient names of the trackers. Jira's site is reached only for <c>_edge/tenant_info</c>, with redirects not followed.</summary>
public static class TrackerHosts
{
    public const string JiraGatewayClient = "jira-gateway";
    public const string JiraGatewayBaseUrl = "https://api.atlassian.com/";

    /// <summary>No base address: only ever a URL built from a site that passed the FR-165 rule.</summary>
    public const string JiraSiteClient = "jira-site";

    public const string LinearClient = "linear";
    public const string LinearBaseUrl = "https://api.linear.app/";

    public static IReadOnlyList<string> AllClients { get; } = [JiraGatewayClient, JiraSiteClient, LinearClient];
}

/// <summary>§8: the stored secret of a tracker connection: Jira's email and token together (encrypted as one value), Linear's key as it is.</summary>
public static class TrackerSecrets
{
    private sealed record JiraSecret(string Email, string Token);

    public static string Encode(PlatformProvider provider, string? email, string token) =>
        provider == PlatformProvider.Jira ? JsonSerializer.Serialize(new JiraSecret(email?.Trim() ?? string.Empty, token)) : token;

    /// <summary>The email and token in a decrypted secret; a Jira secret that can't be read has no token.</summary>
    public static (string? Email, string Token) Decode(PlatformProvider provider, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (provider != PlatformProvider.Jira)
        {
            return (null, secret);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<JiraSecret>(secret);
            return (parsed?.Email, parsed?.Token ?? string.Empty);
        }
        catch (JsonException)
        {
            return (null, string.Empty);
        }
    }
}

/// <summary>
/// JSON-over-HTTP for the Jira and Linear adapters: the secret only in the header the caller sets, failures classified with the
/// tracker's message cleaned and any secret scrubbed, and rate-limit resets read (<c>Retry-After</c>, Linear's reset headers).
/// </summary>
public sealed class TrackerHttp(IHttpClientFactory clients, TimeProvider time)
{
    /// <summary>How a tracker's answer maps to a failure when the status alone can't say (Linear's GraphQL errors); <c>null</c>: by status.</summary>
    public delegate (TrackerFailure Failure, string Message)? AnswerClassifier(int status, JsonElement root);

    public async Task<JsonDocument> SendAsync(
        TrackerProvider provider,
        string clientName,
        HttpMethod method,
        string pathOrUrl,
        Action<HttpRequestMessage> authorize,
        object? body,
        IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken,
        AnswerClassifier? classify = null,
        bool badRequestIsQuery = false)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(secrets);
        var name = TrackerRules.DisplayName(provider);
        using var request = new HttpRequestMessage(method, pathOrUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        authorize(request);
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
            throw new TrackerApiException(TrackerFailure.Upstream, null, $"Could not reach {name}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TrackerApiException(TrackerFailure.Upstream, null, $"Could not reach {name}: the request timed out.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;
            JsonDocument? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                if (response.IsSuccessStatusCode)
                {
                    throw new TrackerApiException(TrackerFailure.Upstream, status, $"{name} returned an unreadable reply.");
                }
            }

            var classified = json is null ? null : classify?.Invoke(status, json.RootElement);
            if (classified is null && response.IsSuccessStatusCode)
            {
                return json!;
            }

            using (json)
            {
                var failure = classified?.Failure ?? ByStatus(status, badRequestIsQuery);
                var message = Scrub(classified?.Message ?? Message(json?.RootElement) ?? response.ReasonPhrase ?? "request failed", secrets);
                throw new TrackerApiException(failure, status, message, failure == TrackerFailure.RateLimited ? RetryAt(response) : null);
            }
        }
    }

    private static TrackerFailure ByStatus(int status, bool badRequestIsQuery) => status switch
    {
        401 => TrackerFailure.Rejected,
        403 => TrackerFailure.MissingPermissions,
        404 => TrackerFailure.NotFound,
        429 => TrackerFailure.RateLimited,
        400 when badRequestIsQuery => TrackerFailure.QueryInvalid,
        _ => TrackerFailure.Upstream,
    };

    private static string? Message(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } body)
        {
            return null;
        }

        // Jira: {"errorMessages": ["…"], "errors": {"jql": "…"}}.
        var first = Json.Items(body, "errorMessages").Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : null).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (first is not null)
        {
            return first;
        }

        var errors = Json.Property(body, "errors");
        if (errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in errors.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }

        return Json.Text(body, "message");
    }

    private static string Scrub(string message, IReadOnlyCollection<string> secrets)
    {
        var cleaned = PlatformHttp.Clean(message);
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            cleaned = cleaned.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return cleaned;
    }

    private DateTimeOffset RetryAt(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return now + delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date;
        }

        foreach (var header in new[] { "X-RateLimit-Requests-Reset", "X-RateLimit-Reset" })
        {
            if (response.Headers.TryGetValues(header, out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset) && reset > 0)
            {
                // Linear sends epoch milliseconds; others epoch seconds, or the seconds to wait.
                return reset > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(reset)
                    : reset > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(reset)
                    : now.AddSeconds(reset);
            }
        }

        return now + TimeSpan.FromMinutes(1);
    }
}

/// <summary>§9: the adapters, or the Fake under <c>Trackers:Provider=Fake</c>.</summary>
internal sealed class IssueTrackers(IOptions<TrackerOptions> options, IEnumerable<IIssueTrackerProvider> providers, FakeTrackerScript script) : IIssueTrackers
{
    private readonly Dictionary<TrackerProvider, IIssueTrackerProvider> _byProvider = providers.ToDictionary(p => p.Provider);

    public bool UsesFake => options.Value.UseFake;

    public IIssueTrackerProvider ForProvider(TrackerProvider provider)
    {
        if (UsesFake)
        {
            return new FakeIssueTrackerProvider(provider, script);
        }

        return _byProvider.TryGetValue(provider, out var found)
            ? found
            : throw new InvalidOperationException($"No tracker adapter for {provider}.");
    }
}

/// <summary>
/// FR-165, FR-166: Jira and Linear as platform connections: the v4 flow (validated on save, encrypted, re-test) on top of the tracker
/// adapter. A tracker failure becomes the matching platform failure.
/// </summary>
internal sealed class TrackerConnectionValidator(PlatformProvider provider, IIssueTrackers trackers) : IPlatformConnectionValidator
{
    public PlatformProvider Provider => provider;

    private TrackerProvider Tracker => provider == PlatformProvider.Jira ? TrackerProvider.Jira : TrackerProvider.Linear;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var check = await Call(() => trackers.ForProvider(Tracker).ValidateAsync(ToCredential(credential), cancellationToken));
        return new PlatformTokenCheck(
            check.AccountId,
            check.AccountName,
            [],
            credential.Metadata with
            {
                Site = check.Site,
                CloudId = check.CloudId,
                Organization = check.Organization,
                OrganizationUrlKey = check.OrganizationUrlKey,
                MissingPermissions = check.MissingPermissions,
            },
            check.Scopes,
            null);
    }

    public Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PlatformAccountRef>>([]);

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var fields = await Call(() => trackers.ForProvider(Tracker).ListFieldsAsync(ToCredential(credential), cancellationToken));
        return [.. fields.Select(f => new PlatformResource(f.Id, f.Name))];
    }

    private static TrackerCredential ToCredential(PlatformCredential credential) => new(credential.Token)
    {
        Email = credential.Email,
        Site = credential.Metadata.Site,
        CloudId = credential.Metadata.CloudId,
        AcceptanceCriteriaField = credential.Metadata.AcceptanceCriteriaField,
    };

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TrackerApiException ex)
        {
            throw new PlatformApiException(
                ex.Failure switch
                {
                    TrackerFailure.Rejected => PlatformFailure.Rejected,
                    TrackerFailure.MissingPermissions => PlatformFailure.MissingPermissions,
                    TrackerFailure.RateLimited => PlatformFailure.RateLimited,
                    TrackerFailure.NotFound => PlatformFailure.NotFound,
                    _ => PlatformFailure.Upstream,
                },
                ex.Status,
                ex.Message,
                retryAt: ex.RetryAt);
        }
    }
}
