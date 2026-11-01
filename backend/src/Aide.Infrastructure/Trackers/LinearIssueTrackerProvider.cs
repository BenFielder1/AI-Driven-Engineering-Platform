using System.Globalization;
using System.Text.Json;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Trackers;

/// <summary>
/// FR-166, §4.26: Linear's GraphQL API at <c>https://api.linear.app/graphql</c>, with <c>Authorization: &lt;key&gt;</c> and no <c>Bearer</c>.
/// Relay cursors. Children are fetched per page, keeping each query under Linear's 10,000 complexity points. A container is
/// <c>team:KEY</c> or <c>project:ID</c>. Structured filters only (FR-169).
/// </summary>
public sealed class LinearIssueTrackerProvider(TrackerHttp http) : IIssueTrackerProvider
{
    private const int PageSize = 50;
    private const int MaxDepth = 3;
    private const string ViewerQuery = "query { viewer { id name email organization { name urlKey } } }";

    private const string SummaryFields =
        "id identifier title url updatedAt priorityLabel estimate state { name type } labels(first: 20) { nodes { name } } parent { identifier } project { id name } assignee { displayName }";

    private const string FullFields = SummaryFields + " description children(first: 50) { nodes { identifier } } attachments(first: 20) { nodes { title } }";

    private const string CommentFields = " comments(first: 20) { nodes { body createdAt user { displayName } } pageInfo { hasNextPage } }";

    public TrackerProvider Provider => TrackerProvider.Linear;

    public async Task<TrackerTokenCheck> ValidateAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await QueryAsync(ViewerQuery, null, credential, cancellationToken);

        // The email is asked for by FR-166's query and is deliberately not kept (§8 privacy).
        var viewer = Json.Property(Json.Property(json.RootElement, "data"), "viewer");
        var organization = Json.Property(viewer, "organization");
        return new TrackerTokenCheck(Json.Text(viewer, "id"), Json.Text(viewer, "name"), [])
        {
            Organization = Json.Text(organization, "name"),
            OrganizationUrlKey = Json.Text(organization, "urlKey"),
        };
    }

    /// <summary>Teams first, then projects; the cursor says which list it continues (<c>t:…</c>, <c>p:…</c>).</summary>
    public async Task<TrackerContainerPage> ListContainersAsync(TrackerCredential credential, string? search, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var inProjects = cursor?.StartsWith("p:", StringComparison.Ordinal) == true;
        var after = cursor is { Length: > 2 } ? cursor[2..] : null;
        var filter = string.IsNullOrWhiteSpace(search) ? null : new { name = new { containsIgnoreCase = search.Trim() } };
        var kind = inProjects ? "projects" : "teams";
        var filterType = inProjects ? "ProjectFilter" : "TeamFilter";
        var fields = inProjects ? "id name state" : "id key name";
        var query = $"query($first: Int!, $after: String, $filter: {filterType}) {{ {kind}(first: $first, after: $after, filter: $filter) {{ nodes {{ {fields} }} pageInfo {{ hasNextPage endCursor }} }} }}";
        using var json = await QueryAsync(query, new { first = PageSize, after, filter }, credential, cancellationToken);
        var connection = Json.Property(Json.Property(json.RootElement, "data"), kind);
        var items = Json.Items(connection, "nodes")
            .Select(n => inProjects
                ? new TrackerContainer($"project:{Json.Text(n, "id")}", Json.Text(n, "name") ?? string.Empty, "project")
                : new TrackerContainer($"team:{Json.Text(n, "key")}", Json.Text(n, "name") ?? string.Empty, "team"))
            .ToList();
        var pageInfo = Json.Property(connection, "pageInfo");
        var next = Json.Property(pageInfo, "hasNextPage").ValueKind == JsonValueKind.True ? Json.Text(pageInfo, "endCursor") : null;
        return new TrackerContainerPage(
            items,
            next is not null ? $"{(inProjects ? "p" : "t")}:{next}" : inProjects ? null : "p:");
    }

    public async Task<TrackerItemPage> SearchAsync(TrackerCredential credential, TrackerSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(search);
        return await PageAsync(credential, Filter(search.ContainerId, search.Filter), SummaryFields, search.Cursor, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceItem>> GetItemsAsync(TrackerCredential credential, TrackerSelection selection, TrackerImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        var fields = options.IncludeComments ? FullFields + CommentFields : FullFields;
        var byKey = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
        if (selection.Keys is { Count: > 0 } keys)
        {
            await FetchKeysAsync(credential, keys, fields, byKey, cancellationToken);
        }
        else
        {
            var filter = Filter(selection.ContainerId, selection.Filter);
            string? cursor = null;
            do
            {
                var page = await PageAsync(credential, filter, fields, cursor, cancellationToken);
                foreach (var item in page.Items)
                {
                    byKey.TryAdd(item.Key, item);
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null && byKey.Count <= SourceItemLimits.MaxItems);
        }

        if (options.IncludeChildren)
        {
            for (var depth = 0; depth < MaxDepth && byKey.Count <= SourceItemLimits.MaxItems; depth++)
            {
                var missing = byKey.Values.SelectMany(i => i.ChildKeys).Where(k => !byKey.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count == 0)
                {
                    break;
                }

                await FetchKeysAsync(credential, missing, fields, byKey, cancellationToken);
            }
        }

        return [.. byKey.Values.Take(SourceItemLimits.MaxItems + 1)];
    }

    public Task<IReadOnlyList<TrackerField>> ListFieldsAsync(TrackerCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TrackerField>>([]);

    /// <summary>Keys by team and number: <c>ENG-42</c> is team <c>ENG</c>, number 42.</summary>
    private async Task FetchKeysAsync(TrackerCredential credential, IReadOnlyList<string> keys, string fields, Dictionary<string, SourceItem> into, CancellationToken cancellationToken)
    {
        var byTeam = keys.Select(k => (Key: k, Dash: k.LastIndexOf('-')))
            .Where(k => k.Dash > 0)
            .GroupBy(k => k.Key[..k.Dash], k => int.TryParse(k.Key[(k.Dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1, StringComparer.Ordinal);
        foreach (var team in byTeam)
        {
            foreach (var numbers in team.Where(n => n >= 0).Distinct().Chunk(PageSize))
            {
                var filter = new Dictionary<string, object?>
                {
                    ["team"] = new { key = new { eq = team.Key } },
                    ["number"] = new Dictionary<string, object?> { ["in"] = numbers },
                };
                var page = await PageAsync(credential, filter, fields, null, cancellationToken);
                foreach (var item in page.Items)
                {
                    into.TryAdd(item.Key, item);
                }
            }
        }
    }

    private async Task<TrackerItemPage> PageAsync(TrackerCredential credential, Dictionary<string, object?> filter, string fields, string? cursor, CancellationToken cancellationToken)
    {
        var query = $"query($first: Int!, $after: String, $filter: IssueFilter) {{ issues(first: $first, after: $after, filter: $filter) {{ nodes {{ {fields} }} pageInfo {{ hasNextPage endCursor }} }} }}";
        using var json = await QueryAsync(query, new { first = PageSize, after = string.IsNullOrEmpty(cursor) ? null : cursor, filter }, credential, cancellationToken);
        var connection = Json.Property(Json.Property(json.RootElement, "data"), "issues");
        var pageInfo = Json.Property(connection, "pageInfo");
        return new TrackerItemPage(
            [.. Json.Items(connection, "nodes").Select(Map).OfType<SourceItem>()],
            Json.Property(pageInfo, "hasNextPage").ValueKind == JsonValueKind.True ? Json.Text(pageInfo, "endCursor") : null);
    }

    /// <summary>FR-169: container, title text, labels, parent (a sub-issue's key, else a project's name) and states; done hidden unless asked for.</summary>
    internal static Dictionary<string, object?> Filter(string? containerId, TrackerFilter? filter)
    {
        var result = new Dictionary<string, object?>();
        if (containerId?.StartsWith("team:", StringComparison.Ordinal) == true)
        {
            result["team"] = new { key = new { eq = containerId["team:".Length..] } };
        }
        else if (containerId?.StartsWith("project:", StringComparison.Ordinal) == true)
        {
            result["project"] = new { id = new { eq = containerId["project:".Length..] } };
        }

        if (filter?.Text is { Length: > 0 } text)
        {
            result["title"] = new { containsIgnoreCase = text };
        }

        if (filter?.Labels is { Count: > 0 } labels)
        {
            result["labels"] = new { some = new { name = new Dictionary<string, object?> { ["in"] = labels } } };
        }

        if (filter?.Parent is { Length: > 0 } parent)
        {
            var dash = parent.LastIndexOf('-');
            if (dash > 0 && int.TryParse(parent[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                result["parent"] = new { number = new { eq = number }, team = new { key = new { eq = parent[..dash] } } };
            }
            else
            {
                result["project"] = new { name = new { eq = parent } };
            }
        }

        if (filter?.StatusCategories is { Count: > 0 } categories)
        {
            result["state"] = new { type = new Dictionary<string, object?> { ["in"] = categories.SelectMany(StateTypes).ToList() } };
        }
        else
        {
            result["state"] = new { type = new { nin = new[] { "completed", "canceled" } } };
        }

        return result;
    }

    private static string[] StateTypes(SourceItemStatusCategory category) => category switch
    {
        SourceItemStatusCategory.Done => ["completed", "canceled"],
        SourceItemStatusCategory.InProgress => ["started"],
        _ => ["backlog", "unstarted", "triage"],
    };

    private SourceItem? Map(JsonElement issue)
    {
        var key = Json.Text(issue, "identifier");
        if (key is null)
        {
            return null;
        }

        var state = Json.Property(issue, "state");
        var labels = Json.Items(Json.Property(issue, "labels"), "nodes").Select(l => Json.Text(l, "name")).OfType<string>().ToList();
        var parent = Json.Text(Json.Property(issue, "parent"), "identifier");
        var project = Json.Text(Json.Property(issue, "project"), "name");
        var comments = Json.Property(issue, "comments");
        return new SourceItem
        {
            Provider = Provider,
            ExternalId = Json.Text(issue, "id") ?? key,
            Key = key,
            Url = Json.Text(issue, "url") ?? string.Empty,
            Type = TrackerRules.LinearType(parent is not null, labels),
            NativeType = parent is not null ? "sub-issue" : "issue",
            Title = Json.Text(issue, "title") ?? string.Empty,
            DescriptionMarkdown = Json.Text(issue, "description") ?? string.Empty,
            Status = Json.Text(state, "name") ?? "Unknown",
            StatusCategory = TrackerRules.LinearStatusCategory(Json.Text(state, "type")),
            Priority = Json.Text(issue, "priorityLabel"),
            Labels = labels,
            Assignee = Json.Text(Json.Property(issue, "assignee"), "displayName"),
            Estimate = Json.Text(issue, "estimate"),

            // §4.26: a project shows as the parent (an epic).
            ParentKey = parent ?? project,
            ChildKeys = [.. Json.Items(Json.Property(issue, "children"), "nodes").Select(c => Json.Text(c, "identifier")).OfType<string>()],
            Comments = comments.ValueKind == JsonValueKind.Object
                ? [.. Json.Items(comments, "nodes").Select(c => new SourceItemComment(
                    Json.Text(Json.Property(c, "user"), "displayName"),
                    Json.Time(c, "createdAt") ?? DateTimeOffset.UnixEpoch,
                    Json.Text(c, "body") ?? string.Empty))]
                : null,
            AttachmentNames = [.. Json.Items(Json.Property(issue, "attachments"), "nodes").Select(a => Json.Text(a, "title")).OfType<string>()],
            UpdatedAt = Json.Time(issue, "updatedAt") ?? DateTimeOffset.UnixEpoch,
        };
    }

    private Task<JsonDocument> QueryAsync(string query, object? variables, TrackerCredential credential, CancellationToken cancellationToken) =>
        http.SendAsync(
            Provider,
            TrackerHosts.LinearClient,
            HttpMethod.Post,
            "graphql",
            request => request.Headers.TryAddWithoutValidation("Authorization", credential.Token),
            new { query, variables },
            [credential.Token],
            cancellationToken,
            Classify);

    /// <summary>GraphQL errors, with a 200 or a 400: Linear's code decides (<c>AUTHENTICATION_ERROR</c>, <c>RATELIMITED</c>, <c>FORBIDDEN</c>).</summary>
    private static (TrackerFailure Failure, string Message)? Classify(int status, JsonElement root)
    {
        var errors = Json.Items(root, "errors").ToList();
        if (errors.Count == 0)
        {
            return null;
        }

        var first = errors[0];
        var message = Json.Text(first, "message") ?? "request failed";
        var extensions = Json.Property(first, "extensions");
        var code = (Json.Text(extensions, "code") ?? Json.Text(extensions, "type") ?? string.Empty).ToUpperInvariant().Replace(" ", "_", StringComparison.Ordinal);
        return code switch
        {
            "AUTHENTICATION_ERROR" or "UNAUTHENTICATED" => (TrackerFailure.Rejected, message),
            "RATELIMITED" or "RATE_LIMITED" => (TrackerFailure.RateLimited, message),
            "FORBIDDEN" => (TrackerFailure.MissingPermissions, message),
            _ when status == 401 => (TrackerFailure.Rejected, message),
            _ when status == 429 => (TrackerFailure.RateLimited, message),
            _ => (TrackerFailure.Upstream, message),
        };
    }
}
