using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.15: Vercel's limit on env var writes (120 a minute), counted per Vercel project across requests in this process. A
/// write that wouldn't fit fails as rate-limited (retry at the given time) before anything is sent.
/// </summary>
public sealed class VercelEnvWriteLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _writes = new(StringComparer.Ordinal);

    /// <summary>Takes <paramref name="count"/> writes from the project's minute, or throws rate-limited.</summary>
    public void Take(string vercelProjectId, int count)
    {
        var now = time.GetUtcNow();
        var writes = _writes.GetOrAdd(vercelProjectId, _ => []);
        lock (writes)
        {
            writes.RemoveAll(t => now - t >= EnvSyncRules.Window);
            var (allowed, retryAt) = EnvSyncRules.TryWrite(writes, count, now);
            if (!allowed)
            {
                throw new DeploymentProviderException(
                    VercelFailure.RateLimited, 429, "Vercel allows 120 environment variable writes a minute; try again shortly.", retryAt: retryAt);
            }

            writes.AddRange(Enumerable.Repeat(now, count));
        }
    }
}

/// <summary>
/// FR-87–FR-91, §4.15, D-V2-10: Vercel's REST API through the <c>vercel</c> HttpClient on the fixed host
/// https://api.vercel.com/ (never configurable or user-supplied, §8). Tokens travel only in the Authorization header and
/// never appear in exceptions or logs; env values are only in request bodies. Team-scoped tokens send <c>teamId</c> on
/// every project and deployment request. Endpoint versions as documented in 2026: <c>GET /v2/user</c>,
/// <c>GET /v5/user/tokens/current</c>, <c>GET /v2/teams/{id}</c>, <c>POST /v11/projects</c>, <c>GET /v9/projects/{id}</c>,
/// <c>POST /v10/projects/{id}/env?upsert=true</c>, <c>PATCH|DELETE /v9/projects/{id}/env/{envId}</c>,
/// <c>POST|GET /v13/deployments</c>, <c>GET /v3/deployments/{id}/events</c>, <c>GET /v6/deployments</c>,
/// <c>POST /v10/projects/{id}/promote/{deploymentId}</c>.
/// </summary>
public sealed class VercelDeploymentProvider(IHttpClientFactory clients, VercelEnvWriteLimiter limiter, TimeProvider time) : IDeploymentProvider
{
    public const string HttpClientName = "vercel";

    /// <summary>The deployment <c>meta</c> key carrying the AIDE row id (idempotency key).</summary>
    public const string AideDeploymentIdMeta = "aideDeploymentId";

    /// <summary>§8: the only Vercel host AIDE talks to.</summary>
    public const string ApiBaseUrl = "https://api.vercel.com/";

    private const int MaxMessageLength = 300;

    public DeploymentProvider Kind => DeploymentProvider.Vercel;

    public async Task<DeploymentAccount> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var user = await SendAsync(HttpMethod.Get, "v2/user", token, null, cancellationToken);
        var root = user.RootElement.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : user.RootElement;
        var userId = Text(root, "id") ?? Text(root, "uid")
            ?? throw new DeploymentProviderException(VercelFailure.Upstream, null, "Vercel returned a user without an id.");
        var username = Text(root, "username");

        // The token's own scope (team or account) and expiry; older tokens may not be introspectable: account scope then.
        string? teamId = null;
        DateTimeOffset? expiresAt = null;
        try
        {
            using var current = await SendAsync(HttpMethod.Get, "v5/user/tokens/current", token, null, cancellationToken);
            var meta = current.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.Object ? t : current.RootElement;
            expiresAt = Epoch(meta, "expiresAt");
            teamId = Text(meta, "teamId");
            if (meta.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var scope in scopes.EnumerateArray())
                {
                    if (Text(scope, "type") == "team" && Text(scope, "teamId") is { Length: > 0 } scoped)
                    {
                        teamId ??= scoped;
                        expiresAt ??= Epoch(scope, "expiresAt");
                    }
                }
            }
        }
        catch (DeploymentProviderException ex) when (ex.Failure is VercelFailure.NotFound or VercelFailure.Forbidden or VercelFailure.Rejected)
        {
            // Not introspectable: an account token without a known expiry.
        }

        string? teamSlug = null;
        if (teamId is not null)
        {
            using var team = await SendAsync(HttpMethod.Get, $"v2/teams/{Uri.EscapeDataString(teamId)}", token, null, cancellationToken);
            teamSlug = Text(team.RootElement, "slug");
        }

        return new DeploymentAccount(userId, username, teamId, teamSlug, teamId is null ? VercelTokenScope.Account : VercelTokenScope.Team, expiresAt);
    }

    public async Task<IReadOnlyList<DeploymentTeam>> ListTeamsAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(HttpMethod.Get, "v2/teams?limit=100", auth.Token, null, cancellationToken);
        if (!json.RootElement.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. teams.EnumerateArray()
                .Select(t => (Id: Text(t, "id"), Slug: Text(t, "slug"), Name: Text(t, "name")))
                .Where(t => t.Id is not null)
                .Select(t => new DeploymentTeam(t.Id!, t.Slug ?? t.Id!, t.Name ?? t.Slug ?? t.Id!)),
        ];
    }

    public async Task<IReadOnlyList<DeploymentProject>> ListProjectsAsync(DeploymentAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(HttpMethod.Get, Team("v9/projects?limit=100", auth), auth.Token, null, cancellationToken);
        if (!json.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. projects.EnumerateArray().Where(p => Text(p, "id") is not null).Select(ReadProject)];
    }

    public async Task<DeploymentProject> CreateProjectAsync(DeploymentAuth auth, CreateDeploymentProject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Name,
            ["framework"] = request.Framework,
            ["gitRepository"] = new { type = "github", repo = request.Repo },
        };
        if (request.RootDirectory != DeploymentTarget.DefaultRootDirectory)
        {
            body["rootDirectory"] = request.RootDirectory;
        }

        using var json = await SendAsync(HttpMethod.Post, Team("v11/projects", auth), auth.Token, body, cancellationToken);
        return ReadProject(json.RootElement);
    }

    public async Task<DeploymentProject?> FindProjectAsync(DeploymentAuth auth, string idOrName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        try
        {
            using var json = await SendAsync(HttpMethod.Get, Team($"v9/projects/{Uri.EscapeDataString(idOrName)}", auth), auth.Token, null, cancellationToken);
            return ReadProject(json.RootElement);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DeploymentEnvWritten>> WriteEnvAsync(
        DeploymentAuth auth, string vercelProjectId, IReadOnlyList<DeploymentEnvWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(writes);
        var written = new List<DeploymentEnvWritten>();
        var creates = new List<DeploymentEnvWrite>();
        var project = Uri.EscapeDataString(vercelProjectId);
        foreach (var write in writes)
        {
            if (write.ExistingId is not { } id)
            {
                creates.Add(write);
                continue;
            }

            limiter.Take(vercelProjectId, 1);
            try
            {
                using var _ = await SendAsync(
                    HttpMethod.Patch,
                    Team($"v9/projects/{project}/env/{Uri.EscapeDataString(id)}", auth),
                    auth.Token,
                    new { value = write.Value, type = EnvSyncRules.VercelType(write.Sensitive), target = new[] { write.Target.ToWire() } },
                    cancellationToken);
                written.Add(new DeploymentEnvWritten(write.Key, write.Target, id));
            }
            catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.NotFound)
            {
                // Deleted in Vercel meanwhile: create it again.
                creates.Add(write with { ExistingId = null });
            }
        }

        // §4.15: creates go in batches (one request each), upserting what Vercel already has for the (key, target).
        var offset = 0;
        foreach (var size in EnvSyncRules.Batches(creates.Count))
        {
            var batch = creates.Skip(offset).Take(size).ToList();
            offset += size;
            limiter.Take(vercelProjectId, 1);
            var body = batch.Select(w => new { key = w.Key, value = w.Value, type = EnvSyncRules.VercelType(w.Sensitive), target = new[] { w.Target.ToWire() } }).ToList();
            using var json = await SendAsync(HttpMethod.Post, Team($"v10/projects/{project}/env?upsert=true", auth), auth.Token, body, cancellationToken);
            written.AddRange(ReadCreatedEnv(json.RootElement, batch));
        }

        return written;
    }

    public async Task DeleteEnvAsync(DeploymentAuth auth, string vercelProjectId, string envId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        limiter.Take(vercelProjectId, 1);
        try
        {
            using var _ = await SendAsync(
                HttpMethod.Delete, Team($"v9/projects/{Uri.EscapeDataString(vercelProjectId)}/env/{Uri.EscapeDataString(envId)}", auth), auth.Token, null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.NotFound)
        {
            // Already gone.
        }
    }

    public async Task<ProviderDeployment> CreateDeploymentAsync(DeploymentAuth auth, CreateDeployment request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        object gitSource = request.RepoId is { } repoId
            ? new { type = "github", repoId, @ref = request.GitRef, sha = request.GitSha }
            : new { type = "github", org = request.RepoOwner, repo = request.RepoName, @ref = request.GitRef, sha = request.GitSha };
        var body = new Dictionary<string, object?>
        {
            ["name"] = request.VercelProjectName,
            ["project"] = request.VercelProjectId,
            ["gitSource"] = gitSource,
            ["meta"] = new Dictionary<string, string> { [AideDeploymentIdMeta] = request.AideDeploymentId.ToString("D") },
        };
        if (request.Target == DeploymentEnvironment.Production)
        {
            body["target"] = "production";
        }

        using var json = await SendAsync(HttpMethod.Post, Team("v13/deployments", auth), auth.Token, body, cancellationToken);
        return ReadDeployment(json.RootElement);
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(HttpMethod.Get, Team($"v13/deployments/{Uri.EscapeDataString(deploymentId)}", auth), auth.Token, null, cancellationToken);
        return ReadDeployment(json.RootElement);
    }

    public async Task<string?> GetBuildLogAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(
            HttpMethod.Get, Team($"v3/deployments/{Uri.EscapeDataString(deploymentId)}/events?builds=1&direction=backward&limit=500", auth), auth.Token, null, cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = json.RootElement.EnumerateArray()
            .Select(e => (Created: Number(e, "created") ?? 0, Text: Text(e, "text") ?? (e.TryGetProperty("payload", out var p) ? Text(p, "text") : null)))
            .Where(e => !string.IsNullOrEmpty(e.Text))
            .OrderBy(e => e.Created)
            .Select(e => e.Text!.TrimEnd('\r', '\n'))
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var log = string.Join('\n', lines);
        return log.Length <= IDeploymentProvider.MaxBuildLogLength ? log : log[^IDeploymentProvider.MaxBuildLogLength..];
    }

    public async Task<ProviderDeployment?> FindByAideIdAsync(
        DeploymentAuth auth, string vercelProjectId, string sha, DeploymentEnvironment target, Guid aideDeploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        try
        {
            using var json = await SendAsync(
                HttpMethod.Get,
                Team($"v6/deployments?projectId={Uri.EscapeDataString(vercelProjectId)}&target={target.ToWire()}&sha={Uri.EscapeDataString(sha)}&limit=20", auth),
                auth.Token,
                null,
                cancellationToken);
            if (!json.RootElement.TryGetProperty("deployments", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var key = aideDeploymentId.ToString("D");
            foreach (var item in list.EnumerateArray())
            {
                var meta = item.TryGetProperty("meta", out var m) ? Text(m, AideDeploymentIdMeta) : null;
                var state = Text(item, "readyState") ?? Text(item, "state");
                if (string.Equals(meta, key, StringComparison.OrdinalIgnoreCase) && DeploymentStates.FromReadyState(state) != DeploymentStatus.Canceled
                    && (Text(item, "uid") ?? Text(item, "id")) is { } id)
                {
                    return new ProviderDeployment(id, WithScheme(Text(item, "url")), Text(item, "inspectorUrl"), state, null);
                }
            }

            return null;
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.NotFound)
        {
            return null;
        }
    }

    public async Task CancelDeploymentAsync(DeploymentAuth auth, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        try
        {
            using var _ = await SendAsync(HttpMethod.Patch, Team($"v12/deployments/{Uri.EscapeDataString(deploymentId)}/cancel", auth), auth.Token, new { }, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is VercelFailure.NotFound or VercelFailure.Rejected)
        {
            // Already gone or finished.
        }
    }

    public async Task<ProviderDeployment?> FindProductionDeploymentAsync(DeploymentAuth auth, string vercelProjectId, string sha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(
            HttpMethod.Get,
            Team($"v6/deployments?projectId={Uri.EscapeDataString(vercelProjectId)}&target=production&sha={Uri.EscapeDataString(sha)}&limit=20", auth),
            auth.Token,
            null,
            cancellationToken);
        if (!json.RootElement.TryGetProperty("deployments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in list.EnumerateArray())
        {
            // Only a deployment whose commit is known to be the SHA (the list filter alone isn't trusted).
            var commit = item.TryGetProperty("meta", out var meta) ? Text(meta, "githubCommitSha") : null;
            var state = Text(item, "readyState") ?? Text(item, "state");
            if (string.Equals(commit, sha, StringComparison.OrdinalIgnoreCase) && DeploymentStates.FromReadyState(state) != DeploymentStatus.Canceled)
            {
                var id = Text(item, "uid") ?? Text(item, "id");
                if (id is not null)
                {
                    return new ProviderDeployment(id, WithScheme(Text(item, "url")), Text(item, "inspectorUrl"), state, null);
                }
            }
        }

        return null;
    }

    public async Task PromoteAsync(DeploymentAuth auth, string vercelProjectId, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var _ = await SendAsync(
            HttpMethod.Post,
            Team($"v10/projects/{Uri.EscapeDataString(vercelProjectId)}/promote/{Uri.EscapeDataString(deploymentId)}", auth),
            auth.Token,
            new { },
            cancellationToken);
    }

    // ---------------------------------------------------------------- HTTP

    private static string Team(string path, DeploymentAuth auth) =>
        auth.TeamId is { Length: > 0 } team ? $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}teamId={Uri.EscapeDataString(team)}" : path;

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new DeploymentProviderException(VercelFailure.Upstream, null, "Could not reach Vercel.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeploymentProviderException(VercelFailure.Upstream, null, "Could not reach Vercel: the request timed out.");
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
                    throw new DeploymentProviderException(VercelFailure.Upstream, (int)response.StatusCode, "Vercel returned an unreadable reply.");
                }
            }

            throw Failure(response, text);
        }
    }

    private DeploymentProviderException Failure(HttpResponseMessage response, string text)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? message = null;
        string? link = null;
        var invalidToken = false;
        try
        {
            using var json = JsonDocument.Parse(text);
            var error = json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : json.RootElement;
            code = Text(error, "code");
            message = Text(error, "message");
            link = Text(error, "link");
            invalidToken = error.TryGetProperty("invalidToken", out var invalid) && invalid.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
        }

        var failure = VercelErrorRules.Classify(status, code, message, invalidToken);
        var cleaned = Clean(message ?? response.ReasonPhrase ?? "request failed");
        return failure switch
        {
            VercelFailure.RateLimited => new DeploymentProviderException(failure, status, $"Vercel's rate limit was reached: {cleaned}", retryAt: RetryAt(response)),
            VercelFailure.GitNotConnected => new DeploymentProviderException(
                failure, status, cleaned, link is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? l : VercelErrorRules.DefaultGitHubInstallUrl),
            _ => new DeploymentProviderException(failure, status, cleaned),
        };
    }

    private DateTimeOffset RetryAt(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return response.Headers.RetryAfter?.Delta is { } delta ? now + delta : now + TimeSpan.FromMinutes(1);
    }

    // ---------------------------------------------------------------- JSON

    private static DeploymentProject ReadProject(JsonElement root)
    {
        var id = Text(root, "id") ?? throw new DeploymentProviderException(VercelFailure.Upstream, null, "Vercel returned a project without an id.");
        string? linked = null;
        long? repoId = null;
        if (root.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object && Text(link, "type") == "github")
        {
            var repo = Text(link, "repo");
            var org = Text(link, "org");
            linked = repo is null ? null : repo.Contains('/', StringComparison.Ordinal) || org is null ? repo : $"{org}/{repo}";
            repoId = link.TryGetProperty("repoId", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var n) ? n : null;
        }

        return new DeploymentProject(id, Text(root, "name") ?? id, linked, repoId);
    }

    private static ProviderDeployment ReadDeployment(JsonElement root)
    {
        var id = Text(root, "id") ?? Text(root, "uid") ?? throw new DeploymentProviderException(VercelFailure.Upstream, null, "Vercel returned a deployment without an id.");
        var alias = root.TryGetProperty("alias", out var aliases) && aliases.ValueKind == JsonValueKind.Array
            ? aliases.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).FirstOrDefault()
            : null;
        return new ProviderDeployment(
            id,
            WithScheme(Text(root, "url")),
            Text(root, "inspectorUrl"),
            Text(root, "readyState") ?? Text(root, "state") ?? Text(root, "status"),
            Text(root, "errorMessage") is { } error ? Clean(error) : null,
            WithScheme(alias));
    }

    private static List<DeploymentEnvWritten> ReadCreatedEnv(JsonElement root, IReadOnlyList<DeploymentEnvWrite> batch)
    {
        if (root.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.Array && failed.GetArrayLength() > 0)
        {
            var first = failed[0];
            var error = first.TryGetProperty("error", out var e) ? e : first;
            throw new DeploymentProviderException(VercelFailure.Rejected, 400, Clean(Text(error, "message") ?? "Vercel refused an environment variable."));
        }

        var created = root.TryGetProperty("created", out var c) ? c : root;
        var items = created.ValueKind == JsonValueKind.Array ? created.EnumerateArray().ToList() : created.ValueKind == JsonValueKind.Object ? [created] : [];
        var written = new List<DeploymentEnvWritten>();
        foreach (var write in batch)
        {
            var match = items.FirstOrDefault(i => Text(i, "key") == write.Key && Targets(i).Contains(write.Target.ToWire()));
            if (match.ValueKind != JsonValueKind.Object || Text(match, "id") is not { } id)
            {
                throw new DeploymentProviderException(VercelFailure.Upstream, null, $"Vercel didn't confirm {write.Key} for {write.Target.ToWire()}.");
            }

            written.Add(new DeploymentEnvWritten(write.Key, write.Target, id));
        }

        return written;
    }

    private static string[] Targets(JsonElement item) =>
        !item.TryGetProperty("target", out var target) ? []
        : target.ValueKind == JsonValueKind.String ? [target.GetString()!]
        : target.ValueKind == JsonValueKind.Array ? [.. target.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)]
        : [];

    private static string? WithScheme(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : $"https://{url.TrimStart('/')}";

    private static string Clean(string text)
    {
        var cleaned = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return cleaned.Length > MaxMessageLength ? cleaned[..MaxMessageLength] : cleaned;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Epoch(JsonElement element, string property) =>
        Number(element, property) is { } ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
