using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Infrastructure.Deployments;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Platforms;

/// <summary>
/// FR-142, §4.20 Vercel: the v2 check (<c>GET /v2/user</c>, the token's scope and team) through the deployment provider in
/// use (the Fake under <c>Deployments:Provider=Fake</c>); teams (<c>GET /v2/teams</c>) and projects (<c>GET /v9/projects</c>)
/// for the picker. The token's team goes into the metadata (sent as <c>teamId</c> on every Vercel request).
/// </summary>
public sealed class VercelConnectionValidator(IDeploymentProviderResolver providers) : IPlatformConnectionValidator
{
    public PlatformProvider Provider => PlatformProvider.Vercel;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var account = await Call(() => providers.Vercel.ValidateTokenAsync(credential.Token, cancellationToken));
        return new PlatformTokenCheck(
            account.UserId,
            account.Username,
            [],
            new PlatformConnectionMetadata { TeamId = account.TeamId, TeamSlug = account.TeamSlug },
            [],
            account.ExpiresAt);
    }

    public async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Metadata.TeamId is { } teamId)
        {
            // A team-scoped token reaches its team only.
            return [new PlatformAccountRef(teamId, credential.Metadata.TeamSlug ?? teamId, credential.Metadata.TeamSlug, "team")];
        }

        var teams = await Call(() => providers.Vercel.ListTeamsAsync(new DeploymentAuth(credential.Token, null), cancellationToken));
        return [.. teams.Select(t => new PlatformAccountRef(t.Id, t.Name, t.Slug, "team"))];
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        // The account id of a Vercel connection is the user; another id is a team.
        var team = accountId is null || accountId == credential.AccountId ? credential.Metadata.TeamId : accountId;
        var projects = await Call(() => providers.Vercel.ListProjectsAsync(new DeploymentAuth(credential.Token, team), cancellationToken));
        return [.. projects.Select(p => new PlatformResource(p.Id, p.Name, null, p.LinkedRepo))];
    }

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (DeploymentProviderException ex)
        {
            throw new PlatformApiException(PlatformConnectionRules.FromVercel(ex.Failure), ex.Status, $"Vercel: {ex.Message}", retryAt: ex.RetryAt);
        }
    }
}

/// <summary>
/// FR-142, §4.20 Netlify: a personal access token (<c>GET /user</c>, <c>GET /accounts</c>); sites for the picker
/// (<c>GET /{account_slug}/sites</c>, or <c>GET /sites?filter=all</c>).
/// </summary>
public sealed class NetlifyConnectionValidator(PlatformHttp http) : IPlatformConnectionValidator
{
    public PlatformProvider Provider => PlatformProvider.Netlify;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var user = await SendAsync("user", credential, cancellationToken);
        var root = user.RootElement;
        var accounts = await ListAccountsAsync(credential, cancellationToken);
        return new PlatformTokenCheck(
            Json.Text(root, "id"),
            Json.Text(root, "full_name") ?? Json.Text(root, "slug") ?? Json.Text(root, "email"),
            accounts,
            new PlatformConnectionMetadata(),
            [],
            null);
    }

    public async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await SendAsync("accounts", credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Select(a => (Id: Json.Text(a, "id"), Name: Json.Text(a, "name"), Slug: Json.Text(a, "slug"), Kind: Json.Text(a, "type_name") ?? Json.Text(a, "type")))
                .Where(a => a.Id is not null)
                .Select(a => new PlatformAccountRef(a.Id!, a.Name ?? a.Slug ?? a.Id!, a.Slug, a.Kind)),
        ];
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var account = accountId ?? credential.AccountId;
        var slug = account is null ? null
            : credential.Metadata.Accounts.FirstOrDefault(a => a.Id == account || a.Slug == account)?.Slug ?? account;
        var path = slug is null ? "sites?filter=all&per_page=100" : $"{Uri.EscapeDataString(slug)}/sites?per_page=100";
        using var json = await SendAsync(path, credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Where(s => Json.Text(s, "id") is not null)
                .Select(s => new PlatformResource(
                    Json.Text(s, "id")!,
                    Json.Text(s, "name") ?? Json.Text(s, "id")!,
                    Json.Text(s, "ssl_url") ?? Json.Text(s, "url"),
                    Json.Text(Json.Property(s, "build_settings"), "repo_path"))),
        ];
    }

    private Task<JsonDocument> SendAsync(string path, PlatformCredential credential, CancellationToken cancellationToken) =>
        http.SendAsync(Provider, PlatformHosts.NetlifyClient, HttpMethod.Get, path, credential.Token, null, cancellationToken);
}

/// <summary>
/// FR-142, §4.20 Cloudflare: a <b>user</b> API token (<c>GET /user/tokens/verify</c>, <c>GET /accounts</c>; account-owned
/// <c>cfat_</c> tokens are refused before calling). The token's policies (<c>GET /user/tokens/{id}</c>) are recorded where
/// the token may read them. Workers (<c>GET /accounts/{id}/workers/scripts</c>) and Pages projects
/// (<c>GET /accounts/{id}/pages/projects</c>) for the picker.
/// </summary>
public sealed class CloudflareConnectionValidator(PlatformHttp http) : IPlatformConnectionValidator
{
    public PlatformProvider Provider => PlatformProvider.Cloudflare;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var verify = await SendAsync("user/tokens/verify", credential, cancellationToken);
        var result = Json.Property(verify.RootElement, "result");
        var tokenStatus = Json.Text(result, "status");
        if (tokenStatus is not null && !string.Equals(tokenStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformApiException(PlatformFailure.Rejected, 200, $"Cloudflare: the token is {PlatformHttp.Clean(tokenStatus)}.");
        }

        var tokenId = Json.Text(result, "id");
        var accounts = await ListAccountsAsync(credential, cancellationToken);
        IReadOnlyList<string> policies = [];
        if (tokenId is not null)
        {
            try
            {
                using var details = await SendAsync($"user/tokens/{Uri.EscapeDataString(tokenId)}", credential, cancellationToken);
                policies =
                [
                    .. Json.Items(Json.Property(details.RootElement, "result"), "policies")
                        .SelectMany(p => Json.Items(p, "permission_groups"))
                        .Select(g => Json.Text(g, "name"))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal),
                ];
            }
            catch (PlatformApiException ex) when (ex.Failure is not PlatformFailure.RateLimited)
            {
                // Reading the policies needs "API Tokens Read", which §4.20 doesn't ask for: when refused (or failing), the
                // token is still valid and its policies stay unknown.
            }
        }

        return new PlatformTokenCheck(
            null,
            null,
            accounts,
            new PlatformConnectionMetadata { Policies = policies, TokenKind = "user" },
            [],
            Json.Time(result, "expires_on"));
    }

    public async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await SendAsync("accounts?per_page=50", credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement, "result")
                .Where(a => Json.Text(a, "id") is not null)
                .Select(a => new PlatformAccountRef(Json.Text(a, "id")!, Json.Text(a, "name") ?? Json.Text(a, "id")!, null, Json.Text(a, "type"))),
        ];
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var account = Uri.EscapeDataString(accountId ?? credential.AccountId
            ?? throw new PlatformApiException(PlatformFailure.NotFound, null, "Cloudflare: choose an account first."));
        if (type == PlatformResourceType.Projects)
        {
            using var pages = await SendAsync($"accounts/{account}/pages/projects?per_page=100", credential, cancellationToken);
            return
            [
                .. Json.Items(pages.RootElement, "result")
                    .Where(p => Json.Text(p, "name") is not null)
                    .Select(p => new PlatformResource(
                        Json.Text(p, "name")!,
                        Json.Text(p, "name")!,
                        Json.Text(p, "subdomain") is { } sub ? $"https://{sub}" : null,
                        Json.Text(p, "production_branch"))),
            ];
        }

        using var workers = await SendAsync($"accounts/{account}/workers/scripts", credential, cancellationToken);
        return
        [
            .. Json.Items(workers.RootElement, "result")
                .Where(w => Json.Text(w, "id") is not null)
                .Select(w => new PlatformResource(Json.Text(w, "id")!, Json.Text(w, "id")!)),
        ];
    }

    private Task<JsonDocument> SendAsync(string path, PlatformCredential credential, CancellationToken cancellationToken) =>
        http.SendAsync(Provider, PlatformHosts.CloudflareClient, HttpMethod.Get, path, credential.Token, null, cancellationToken);
}

/// <summary>
/// FR-142, §4.20 Render: an account-wide API key (<c>GET /v1/users</c>), with the workspaces it reaches from
/// <c>GET /v1/owners</c>. Services (<c>GET /v1/services</c>) and regions for the picker.
/// </summary>
public sealed class RenderConnectionValidator(PlatformHttp http) : IPlatformConnectionValidator
{
    public PlatformProvider Provider => PlatformProvider.Render;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var user = await SendAsync("users", credential, cancellationToken);
        var accounts = await ListAccountsAsync(credential, cancellationToken);
        return new PlatformTokenCheck(
            null,
            Json.Text(user.RootElement, "name") ?? Json.Text(user.RootElement, "email"),
            accounts,
            new PlatformConnectionMetadata(),
            [],
            null);
    }

    public async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await SendAsync("owners?limit=100", credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Select(o => Json.Property(o, "owner").ValueKind == JsonValueKind.Object ? Json.Property(o, "owner") : o)
                .Where(o => Json.Text(o, "id") is not null)
                .Select(o => new PlatformAccountRef(Json.Text(o, "id")!, Json.Text(o, "name") ?? Json.Text(o, "email") ?? Json.Text(o, "id")!, null, Json.Text(o, "type"))),
        ];
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (type == PlatformResourceType.Regions)
        {
            return [.. PlatformRegions.Render.Select(r => new PlatformResource(r, r))];
        }

        var owner = accountId ?? credential.AccountId;
        var path = owner is null ? "services?limit=100" : $"services?limit=100&ownerId={Uri.EscapeDataString(owner)}";
        using var json = await SendAsync(path, credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Select(s => Json.Property(s, "service").ValueKind == JsonValueKind.Object ? Json.Property(s, "service") : s)
                .Where(s => Json.Text(s, "id") is not null)
                .Select(s => new PlatformResource(
                    Json.Text(s, "id")!,
                    Json.Text(s, "name") ?? Json.Text(s, "id")!,
                    Json.Text(Json.Property(s, "serviceDetails"), "url"),
                    Json.Text(s, "type"))),
        ];
    }

    private Task<JsonDocument> SendAsync(string path, PlatformCredential credential, CancellationToken cancellationToken) =>
        http.SendAsync(Provider, PlatformHosts.RenderClient, HttpMethod.Get, path, credential.Token, null, cancellationToken);
}

/// <summary>
/// FR-142, §4.20 Railway: a workspace token (or an account token) with <c>Authorization: Bearer</c> against the public
/// GraphQL API (<c>/graphql/v2</c>). An account token answers <c>me</c> (with its workspaces); a workspace token can't, so
/// with the workspace id (<c>accountId</c>) <c>workspace(workspaceId)</c> is asked. GraphQL's <c>Not Authorized</c> is a
/// rejected token. Projects and regions for the picker.
/// </summary>
public sealed class RailwayConnectionValidator(PlatformHttp http) : IPlatformConnectionValidator
{
    private const string MeQuery = "query { me { id name email workspaces { id name } } }";
    private const string WorkspaceQuery = "query($id: String!) { workspace(workspaceId: $id) { id name } }";
    private const string ProjectsQuery = "query($id: String!) { projects(workspaceId: $id, first: 100) { edges { node { id name } } } }";
    private const string RegionsQuery = "query { regions { name country location } }";

    public PlatformProvider Provider => PlatformProvider.Railway;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            using var me = await QueryAsync(MeQuery, null, credential, cancellationToken);
            var user = Json.Property(Json.Property(me.RootElement, "data"), "me");
            var workspaces = Json.Items(user, "workspaces")
                .Where(w => Json.Text(w, "id") is not null)
                .Select(w => new PlatformAccountRef(Json.Text(w, "id")!, Json.Text(w, "name") ?? Json.Text(w, "id")!, null, "workspace"))
                .ToList();
            return new PlatformTokenCheck(
                Json.Text(user, "id"),
                Json.Text(user, "name") ?? Json.Text(user, "email"),
                workspaces,
                new PlatformConnectionMetadata { TokenKind = "account" },
                [],
                null);
        }
        catch (PlatformApiException ex) when (ex.Failure == PlatformFailure.Rejected && credential.AccountId is not null)
        {
            // A workspace token can't read `me`: ask for its workspace instead.
        }
        catch (PlatformApiException ex) when (ex.Failure == PlatformFailure.Rejected)
        {
            throw new PlatformApiException(
                PlatformFailure.Rejected,
                ex.Status,
                "Railway: Not Authorized. For a workspace token, also give its workspace id.");
        }

        using var workspace = await QueryAsync(WorkspaceQuery, new { id = credential.AccountId }, credential, cancellationToken);
        var found = Json.Property(Json.Property(workspace.RootElement, "data"), "workspace");
        if (Json.Text(found, "id") is not { } id)
        {
            throw new PlatformApiException(PlatformFailure.Rejected, 200, "Railway: the token can't see that workspace.");
        }

        return new PlatformTokenCheck(
            null,
            null,
            [new PlatformAccountRef(id, Json.Text(found, "name") ?? id, null, "workspace")],
            new PlatformConnectionMetadata { TokenKind = "workspace" },
            [],
            null);
    }

    public Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ListAccountsCoreAsync(credential, cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (type == PlatformResourceType.Regions)
        {
            using var regions = await QueryAsync(RegionsQuery, null, credential, cancellationToken);
            return
            [
                .. Json.Items(Json.Property(regions.RootElement, "data"), "regions")
                    .Where(r => Json.Text(r, "name") is not null)
                    .Select(r => new PlatformResource(Json.Text(r, "name")!, Json.Text(r, "location") ?? Json.Text(r, "name")!, null, Json.Text(r, "country"))),
            ];
        }

        var workspace = accountId ?? credential.AccountId
            ?? throw new PlatformApiException(PlatformFailure.NotFound, null, "Railway: choose a workspace first.");
        using var json = await QueryAsync(ProjectsQuery, new { id = workspace }, credential, cancellationToken);
        return
        [
            .. Json.Items(Json.Property(Json.Property(json.RootElement, "data"), "projects"), "edges")
                .Select(e => Json.Property(e, "node"))
                .Where(n => Json.Text(n, "id") is not null)
                .Select(n => new PlatformResource(Json.Text(n, "id")!, Json.Text(n, "name") ?? Json.Text(n, "id")!)),
        ];
    }

    private async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsCoreAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        if (credential.Metadata.TokenKind == "workspace")
        {
            return credential.Metadata.Accounts;
        }

        return (await ValidateAsync(credential, cancellationToken)).Accounts;
    }

    /// <summary>A GraphQL request; <c>errors</c> in a 200 answer are failures too (<c>Not Authorized</c>: rejected).</summary>
    private async Task<JsonDocument> QueryAsync(string query, object? variables, PlatformCredential credential, CancellationToken cancellationToken)
    {
        var json = await http.SendAsync(Provider, PlatformHosts.RailwayClient, HttpMethod.Post, "graphql/v2", credential.Token, new { query, variables }, cancellationToken);
        var errors = Json.Items(json.RootElement, "errors").ToList();
        if (errors.Count == 0)
        {
            return json;
        }

        var message = Json.Text(errors[0], "message") ?? "request failed";
        json.Dispose();
        throw new PlatformApiException(PlatformConnectionRules.ClassifyRailwayError(message), 200, $"Railway: {PlatformHttp.Clean(message)}");
    }
}

/// <summary>
/// FR-142, §4.21 Supabase: a scoped personal access token (<c>GET /organizations</c>, <c>GET /organizations/{slug}/projects</c>;
/// 401 invalid). A 403 naming <c>missing_permissions</c> doesn't reject the token: it saves as <c>insufficient_scope</c> with
/// the names. As the API Key Secrets permission only shows on a project, the first project's keys are read
/// (<c>GET /projects/{ref}/api-keys?reveal=true</c>) when the organisation has one. Organisations, projects and regions for
/// the picker.
/// </summary>
public sealed class SupabaseConnectionValidator(PlatformHttp http) : IPlatformConnectionValidator
{
    /// <summary>The permission the api-keys probe needs (recorded when Supabase refuses without naming it).</summary>
    public const string ApiKeySecretsRead = "API Key Secrets: Read";

    /// <summary>The permission listing an organisation's projects needs.</summary>
    public const string OrganizationProjectsRead = "Organization Projects: Read";

    public PlatformProvider Provider => PlatformProvider.Supabase;

    public async Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var missing = new List<string>();
        IReadOnlyList<PlatformAccountRef> orgs = [];
        try
        {
            orgs = await ListAccountsAsync(credential, cancellationToken);
        }
        catch (PlatformApiException ex) when (ex.Failure == PlatformFailure.MissingPermissions)
        {
            missing.AddRange(PlatformConnectionRules.MissingOrPlaceholder(ex.MissingPermissions));
        }

        var org = PlatformConnectionRules.ChooseAccount(orgs, credential.AccountId) ?? (orgs.Count > 0 ? orgs[0] : null);
        if (org is not null)
        {
            IReadOnlyList<PlatformResource> projects = [];
            try
            {
                projects = await ProjectsAsync(org.Slug ?? org.Id, credential, cancellationToken);
            }
            catch (PlatformApiException ex) when (ex.Failure == PlatformFailure.MissingPermissions)
            {
                missing.AddRange(PlatformConnectionRules.MissingOrPlaceholder(ex.MissingPermissions, OrganizationProjectsRead));
            }

            if (projects.Count > 0)
            {
                try
                {
                    using var _ = await SendAsync($"projects/{Uri.EscapeDataString(projects[0].Id)}/api-keys?reveal=true", credential, cancellationToken);
                }
                catch (PlatformApiException ex) when (ex.Failure == PlatformFailure.MissingPermissions)
                {
                    missing.AddRange(PlatformConnectionRules.MissingOrPlaceholder(ex.MissingPermissions, ApiKeySecretsRead));
                }
            }
        }

        return new PlatformTokenCheck(
            null,
            null,
            orgs,
            new PlatformConnectionMetadata { MissingPermissions = [.. missing.Distinct(StringComparer.Ordinal)] },
            [],
            null);
    }

    public async Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var json = await SendAsync("organizations", credential, cancellationToken);
        return
        [
            .. Json.Items(json.RootElement)
                .Where(o => Json.Text(o, "id") is not null)
                .Select(o => new PlatformAccountRef(Json.Text(o, "id")!, Json.Text(o, "name") ?? Json.Text(o, "id")!, Json.Text(o, "slug") ?? Json.Text(o, "id"), "organization")),
        ];
    }

    public async Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        switch (type)
        {
            case PlatformResourceType.Regions:
                return [.. PlatformRegions.Supabase.Select(r => new PlatformResource(r, r))];
            case PlatformResourceType.Orgs:
                return [.. (await ListAccountsAsync(credential, cancellationToken)).Select(o => new PlatformResource(o.Slug ?? o.Id, o.Name))];
            default:
                var org = accountId ?? credential.AccountId;
                if (org is not null)
                {
                    var slug = credential.Metadata.Accounts.FirstOrDefault(a => a.Id == org || a.Slug == org)?.Slug ?? org;
                    return await ProjectsAsync(slug, credential, cancellationToken);
                }

                using (var all = await SendAsync("projects", credential, cancellationToken))
                {
                    return [.. ReadProjects(all.RootElement)];
                }
        }
    }

    private async Task<IReadOnlyList<PlatformResource>> ProjectsAsync(string orgSlug, PlatformCredential credential, CancellationToken cancellationToken)
    {
        using var json = await SendAsync($"organizations/{Uri.EscapeDataString(orgSlug)}/projects", credential, cancellationToken);
        return [.. ReadProjects(json.RootElement)];
    }

    private static List<PlatformResource> ReadProjects(JsonElement root) =>
        Json.Items(root, "projects")
            .Select(p => (Ref: Json.Text(p, "ref") ?? Json.Text(p, "id"), Name: Json.Text(p, "name"), Region: Json.Text(p, "region"), Status: Json.Text(p, "status")))
            .Where(p => p.Ref is not null)
            .Select(p => new PlatformResource(p.Ref!, p.Name ?? p.Ref!, $"https://{p.Ref}.supabase.co", p.Region is null ? p.Status : $"{p.Region} · {p.Status}"))
            .ToList();

    private Task<JsonDocument> SendAsync(string path, PlatformCredential credential, CancellationToken cancellationToken) =>
        http.SendAsync(Provider, PlatformHosts.SupabaseClient, HttpMethod.Get, path, credential.Token, null, cancellationToken);
}

/// <summary>
/// §4.20, §9: the deterministic Fake for Netlify, Cloudflare, Render, Railway and Supabase connections
/// (<c>Deployments:Provider=Fake</c>; Development and E2E). No HTTP: any non-empty token is accepted; tokens starting
/// <c>fake_invalid</c> are rejected and <c>fake_scope</c> ones are missing a permission (Supabase: "API Key Secrets: Read").
/// One fake account, and one fake resource of each type.
/// </summary>
public sealed class FakePlatformConnectionValidator : IPlatformConnectionValidator
{
    public const string InvalidTokenPrefix = "fake_invalid";
    public const string MissingScopeTokenPrefix = "fake_scope";
    public const string AccountId = "acct_fake";
    public const string MissingPermission = "API Key Secrets: Read";

    public PlatformProvider Provider { get; init; } = PlatformProvider.Netlify;

    public Task<PlatformTokenCheck> ValidateAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Check(credential.Token);
        IReadOnlyList<string> missing = credential.Token.StartsWith(MissingScopeTokenPrefix, StringComparison.Ordinal) ? [MissingPermission] : [];
        return Task.FromResult(new PlatformTokenCheck("usr_fake", "fake-user", Accounts, new PlatformConnectionMetadata { MissingPermissions = missing }, [], null));
    }

    public Task<IReadOnlyList<PlatformAccountRef>> ListAccountsAsync(PlatformCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Check(credential.Token);
        return Task.FromResult(Accounts);
    }

    public Task<IReadOnlyList<PlatformResource>> ListResourcesAsync(
        PlatformCredential credential, PlatformResourceType type, string? accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Check(credential.Token);
        var wire = type.ToWire();
        IReadOnlyList<PlatformResource> resources = [new PlatformResource($"fake_{wire}_1", $"fake-{wire}", $"https://fake-{wire}.test")];
        return Task.FromResult(resources);
    }

    private static IReadOnlyList<PlatformAccountRef> Accounts { get; } = [new PlatformAccountRef(AccountId, "Fake account", "fake-account", "team")];

    private static void Check(string token)
    {
        if (token.StartsWith(InvalidTokenPrefix, StringComparison.Ordinal))
        {
            throw new PlatformApiException(PlatformFailure.Rejected, 401, "The Fake provider rejects tokens starting with fake_invalid.");
        }
    }
}

/// <summary>§9: the validator for each provider (the Fake for all but Vercel under <c>Deployments:Provider=Fake</c>).</summary>
public sealed class PlatformConnectionValidators(IOptions<DeploymentOptions> options, IEnumerable<IPlatformConnectionValidator> validators)
    : IPlatformConnectionValidators
{
    private readonly Dictionary<PlatformProvider, IPlatformConnectionValidator> _byProvider = validators.ToDictionary(v => v.Provider);

    public IPlatformConnectionValidator ForProvider(PlatformProvider provider)
    {
        if (provider != PlatformProvider.Vercel && options.Value.UseFake)
        {
            return new FakePlatformConnectionValidator { Provider = provider };
        }

        return _byProvider.TryGetValue(provider, out var validator)
            ? validator
            : throw new InvalidOperationException($"No connection validator for {provider}.");
    }
}
