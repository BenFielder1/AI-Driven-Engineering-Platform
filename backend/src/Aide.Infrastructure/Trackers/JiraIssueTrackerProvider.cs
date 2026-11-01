using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Trackers;

/// <summary>
/// FR-165, §4.26, §8: Jira Cloud. The site passes the FR-165 rule before any network call and is used only for
/// <c>_edge/tenant_info</c> (redirects not followed); everything else goes to
/// <c>https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/…</c> with Basic <c>email:token</c>. Search is
/// <c>POST /search/jql</c> with <c>nextPageToken</c> (the old <c>/search</c> is removed).
/// </summary>
public sealed class JiraIssueTrackerProvider(TrackerHttp http) : IIssueTrackerProvider
{
    /// <summary>The scope name recorded on a connection that can read (FR-165: shown as <c>read</c>).</summary>
    public const string ReadScope = "read";

    /// <summary>What a token without project read access is missing.</summary>
    public const string MissingReadPermission = "read:jira-work";

    private const int PageSize = 50;
    private const int KeyBatch = 50;
    private const int MaxDepth = 3;

    private static readonly string[] SummaryFields = ["summary", "status", "issuetype", "priority", "labels", "parent", "updated", "assignee"];

    private static readonly string[] FullFields = [.. SummaryFields, "description", "issuelinks", "attachment", "subtasks", "timetracking"];

    public TrackerProvider Provider => TrackerProvider.Jira;

    public async Task<TrackerTokenCheck> ValidateAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var site = SiteOf(credential);
        var cloudId = await ResolveCloudIdAsync(site, cancellationToken);
        var scoped = credential with { Site = site, CloudId = cloudId };

        using var myself = await GatewayAsync(scoped, HttpMethod.Get, "myself", null, cancellationToken);
        var accountId = Json.Text(myself.RootElement, "accountId");
        var account = Json.Text(myself.RootElement, "displayName");
        IReadOnlyList<string> missing = [];
        try
        {
            using var _ = await GatewayAsync(scoped, HttpMethod.Get, "project/search?maxResults=1", null, cancellationToken);
        }
        catch (TrackerApiException ex) when (ex.Failure is TrackerFailure.Rejected or TrackerFailure.MissingPermissions)
        {
            // A scoped token that answers /myself but not this one is real, without read access (Jira answers 401 for a missing scope).
            missing = [MissingReadPermission];
        }

        return new TrackerTokenCheck(accountId, account, missing.Count == 0 ? [ReadScope] : [])
        {
            Site = site,
            CloudId = cloudId,
            MissingPermissions = missing,
        };
    }

    public async Task<TrackerContainerPage> ListContainersAsync(TrackerCredential credential, string? search, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var startAt = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var query = new StringBuilder("project/search?maxResults=").Append(PageSize).Append("&startAt=").Append(startAt);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Append("&query=").Append(Uri.EscapeDataString(search.Trim()));
        }

        using var json = await GatewayAsync(await WithCloudIdAsync(credential, cancellationToken), HttpMethod.Get, query.ToString(), null, cancellationToken);
        var items = Json.Items(json.RootElement, "values")
            .Where(p => Json.Text(p, "key") is not null)
            .Select(p => new TrackerContainer(Json.Text(p, "key")!, Json.Text(p, "name") ?? Json.Text(p, "key")!, Json.Text(p, "projectTypeKey")))
            .ToList();
        var last = Json.Property(json.RootElement, "isLast").ValueKind == JsonValueKind.True || items.Count == 0;
        return new TrackerContainerPage(items, last ? null : (startAt + items.Count).ToString(CultureInfo.InvariantCulture));
    }

    public async Task<TrackerItemPage> SearchAsync(TrackerCredential credential, TrackerSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(search);
        var scoped = await WithCloudIdAsync(credential, cancellationToken);
        return await SearchPageAsync(scoped, Jql(search), SummaryFields, search.Cursor, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceItem>> GetItemsAsync(TrackerCredential credential, TrackerSelection selection, TrackerImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        var scoped = await WithCloudIdAsync(credential, cancellationToken);
        var fields = scoped.AcceptanceCriteriaField is { Length: > 0 } ac ? [.. FullFields, ac] : FullFields;
        var byKey = new Dictionary<string, SourceItem>(StringComparer.Ordinal);

        if (selection.Keys is { Count: > 0 } keys)
        {
            foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(KeyBatch))
            {
                await FetchKeysAsync(scoped, batch, fields, byKey, cancellationToken);
            }
        }
        else
        {
            await FetchAllAsync(scoped, Jql(new TrackerSearch(selection.ContainerId, selection.Filter, selection.Query, null)), fields, byKey, cancellationToken);
        }

        if (options.IncludeChildren)
        {
            var frontier = byKey.Keys.ToList();
            for (var depth = 0; depth < MaxDepth && frontier.Count > 0 && byKey.Count <= SourceItemLimits.MaxItems; depth++)
            {
                var before = byKey.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var batch in frontier.Chunk(KeyBatch))
                {
                    await FetchAllAsync(scoped, $"parent in ({string.Join(",", batch)}) ORDER BY key ASC", fields, byKey, cancellationToken);
                }

                frontier = [.. byKey.Keys.Where(k => !before.Contains(k))];
            }
        }

        var items = byKey.Values.Take(SourceItemLimits.MaxItems + 1).ToList();
        if (options.IncludeComments && items.Count <= SourceItemLimits.MaxItems)
        {
            for (var i = 0; i < items.Count; i++)
            {
                items[i] = await WithCommentsAsync(scoped, items[i], cancellationToken);
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<TrackerField>> ListFieldsAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await GatewayAsync(await WithCloudIdAsync(credential, cancellationToken), HttpMethod.Get, "field", null, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Where(f => Json.Text(f, "id") is not null)
                .Select(f => new TrackerField(Json.Text(f, "id")!, Json.Text(f, "name") ?? Json.Text(f, "id")!))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>FR-169: a native query as it is; otherwise structured filters, with done items hidden unless asked for.</summary>
    public static string Jql(TrackerSearch search)
    {
        if (!string.IsNullOrWhiteSpace(search.Query))
        {
            return search.Query.Trim();
        }

        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(search.ContainerId))
        {
            clauses.Add($"project = {Quote(search.ContainerId)}");
        }

        if (search.Filter?.Text is { Length: > 0 } text)
        {
            clauses.Add($"summary ~ {Quote(text)}");
        }

        if (search.Filter?.Labels is { Count: > 0 } labels)
        {
            clauses.Add($"labels in ({string.Join(", ", labels.Select(Quote))})");
        }

        if (search.Filter?.Parent is { Length: > 0 } parent)
        {
            clauses.Add($"parent = {Quote(parent)}");
        }

        clauses.Add(search.Filter?.StatusCategories is { Count: > 0 } categories
            ? $"statusCategory in ({string.Join(", ", categories.Select(c => Quote(CategoryName(c))))})"
            : "statusCategory != Done");
        return $"{string.Join(" AND ", clauses)} ORDER BY key ASC";
    }

    private static string CategoryName(SourceItemStatusCategory category) => category switch
    {
        SourceItemStatusCategory.Done => "Done",
        SourceItemStatusCategory.InProgress => "In Progress",
        _ => "To Do",
    };

    private static string Quote(string text) => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>A key Jira doesn't know fails the whole JQL (400), so a refused batch is split, and a single refused key is simply absent.</summary>
    private async Task FetchKeysAsync(TrackerCredential credential, string[] keys, string[] fields, Dictionary<string, SourceItem> into, CancellationToken cancellationToken)
    {
        try
        {
            await FetchAllAsync(credential, $"key in ({string.Join(",", keys)}) ORDER BY key ASC", fields, into, cancellationToken);
        }
        catch (TrackerApiException ex) when (ex.Failure == TrackerFailure.QueryInvalid)
        {
            if (keys.Length == 1)
            {
                return;
            }

            var half = keys.Length / 2;
            await FetchKeysAsync(credential, [.. keys.Take(half)], fields, into, cancellationToken);
            await FetchKeysAsync(credential, [.. keys.Skip(half)], fields, into, cancellationToken);
        }
    }

    private async Task FetchAllAsync(TrackerCredential credential, string jql, string[] fields, Dictionary<string, SourceItem> into, CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await SearchPageAsync(credential, jql, fields, cursor, cancellationToken);
            foreach (var item in page.Items)
            {
                into.TryAdd(item.Key, item);
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null && into.Count <= SourceItemLimits.MaxItems);
    }

    private async Task<TrackerItemPage> SearchPageAsync(TrackerCredential credential, string jql, string[] fields, string? cursor, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?> { ["jql"] = jql, ["maxResults"] = PageSize, ["fields"] = fields };
        if (!string.IsNullOrEmpty(cursor))
        {
            body["nextPageToken"] = cursor;
        }

        using var json = await GatewayAsync(credential, HttpMethod.Post, "search/jql", body, cancellationToken, badRequestIsQuery: true);
        var items = Json.Items(json.RootElement, "issues").Select(i => Map(i, credential)).OfType<SourceItem>().ToList();
        return new TrackerItemPage(items, Json.Text(json.RootElement, "nextPageToken"));
    }

    private async Task<SourceItem> WithCommentsAsync(TrackerCredential credential, SourceItem item, CancellationToken cancellationToken)
    {
        using var json = await GatewayAsync(
            credential,
            HttpMethod.Get,
            $"issue/{Uri.EscapeDataString(item.Key)}/comment?orderBy=-created&maxResults={SourceItemLimits.MaxComments}",
            null,
            cancellationToken);
        var comments = Json.Items(json.RootElement, "comments")
            .Select(c => new SourceItemComment(
                Json.Text(Json.Property(c, "author"), "displayName"),
                Json.Time(c, "created") ?? item.UpdatedAt,
                AdfConverter.ToMarkdown(Json.Property(c, "body"))))
            .ToList();
        var total = int.TryParse(Json.Text(json.RootElement, "total"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : comments.Count;
        return item with { Comments = comments, CommentsDropped = Math.Max(0, total - comments.Count) };
    }

    private SourceItem? Map(JsonElement issue, TrackerCredential credential)
    {
        var key = Json.Text(issue, "key");
        if (key is null)
        {
            return null;
        }

        var fields = Json.Property(issue, "fields");
        var type = Json.Text(Json.Property(fields, "issuetype"), "name");
        var status = Json.Property(fields, "status");
        var links = Json.Items(fields, "issuelinks").Select(ToLink).OfType<SourceItemLink>().ToList();
        return new SourceItem
        {
            Provider = Provider,
            ExternalId = Json.Text(issue, "id") ?? key,
            Key = key,
            Url = $"https://{credential.Site}/browse/{key}",
            Type = TrackerRules.JiraType(type),
            NativeType = type ?? "Unknown",
            Title = Json.Text(fields, "summary") ?? string.Empty,
            DescriptionMarkdown = AdfConverter.ToMarkdown(Json.Property(fields, "description")),
            Status = Json.Text(status, "name") ?? "Unknown",
            StatusCategory = TrackerRules.JiraStatusCategory(Json.Text(Json.Property(status, "statusCategory"), "key")),
            Priority = Json.Text(Json.Property(fields, "priority"), "name"),
            Labels = [.. Json.Items(fields, "labels").Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)],
            Assignee = Json.Text(Json.Property(fields, "assignee"), "displayName"),
            Estimate = Json.Text(Json.Property(fields, "timetracking"), "originalEstimate"),
            ParentKey = Json.Text(Json.Property(fields, "parent"), "key"),
            ChildKeys = [.. Json.Items(fields, "subtasks").Select(s => Json.Text(s, "key")).OfType<string>()],
            Links = links,
            AcceptanceCriteria = credential.AcceptanceCriteriaField is { Length: > 0 } field ? AcceptanceCriteria(Json.Property(fields, field)) : null,
            AttachmentNames = [.. Json.Items(fields, "attachment").Select(a => Json.Text(a, "filename")).OfType<string>()],
            UpdatedAt = Json.Time(fields, "updated") ?? DateTimeOffset.UnixEpoch,
        };
    }

    private static string? AcceptanceCriteria(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.String && AdfConverter.ToMarkdown(value) is { Length: > 0 } text ? text : null;

    private static SourceItemLink? ToLink(JsonElement link)
    {
        var type = Json.Property(link, "type");
        if (Json.Text(Json.Property(link, "outwardIssue"), "key") is { } outward)
        {
            return new SourceItemLink(Json.Text(type, "outward") ?? "relates to", outward);
        }

        return Json.Text(Json.Property(link, "inwardIssue"), "key") is { } inward
            ? new SourceItemLink(Json.Text(type, "inward") ?? "relates to", inward)
            : null;
    }

    private async Task<TrackerCredential> WithCloudIdAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        var site = SiteOf(credential);
        return credential.CloudId is { Length: > 0 }
            ? credential with { Site = site }
            : credential with { Site = site, CloudId = await ResolveCloudIdAsync(site, cancellationToken) };
    }

    /// <summary>§8: never a host that hasn't passed the rule, even from stored metadata.</summary>
    private static string SiteOf(TrackerCredential credential)
    {
        var site = TrackerRules.NormalizeJiraSite(credential.Site);
        return site.IsSuccess
            ? site.Value
            : throw new TrackerApiException(TrackerFailure.NotConnected, null, "The Jira site isn't allowed.");
    }

    /// <summary>FR-165: <c>GET https://{site}/_edge/tenant_info</c>, unauthenticated; the client follows no redirects (a redirect is a failure).</summary>
    private async Task<string> ResolveCloudIdAsync(string site, CancellationToken cancellationToken)
    {
        using var json = await http.SendAsync(
            Provider,
            TrackerHosts.JiraSiteClient,
            HttpMethod.Get,
            $"https://{site}/_edge/tenant_info",
            _ => { },
            null,
            [],
            cancellationToken);
        return Json.Text(json.RootElement, "cloudId")
            ?? throw new TrackerApiException(TrackerFailure.Upstream, 200, "Jira didn't say which cloud this site belongs to. Check the site name.");
    }

    private Task<JsonDocument> GatewayAsync(TrackerCredential credential, HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool badRequestIsQuery = false)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Email}:{credential.Token}"));
        return http.SendAsync(
            Provider,
            TrackerHosts.JiraGatewayClient,
            method,
            $"ex/jira/{Uri.EscapeDataString(credential.CloudId ?? string.Empty)}/rest/api/3/{path}",
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic),
            body,
            [credential.Token, basic],
            cancellationToken,
            badRequestIsQuery: badRequestIsQuery);
    }
}
