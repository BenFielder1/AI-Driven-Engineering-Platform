using System.Text.Json;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Databases;

/// <summary>
/// FR-152, FR-153, §4.21 Supabase through a scoped personal access token (<c>api.supabase.com/v1</c>, 120 requests a minute;
/// a 429 waits for <c>X-RateLimit-Reset</c> through <see cref="PlatformHttp"/>), in the connection's organisation:
/// <list type="bullet">
/// <item>Create: <c>POST /projects {name, organization_slug, db_pass, region_selection}</c>
/// (<see cref="ManagedDatabaseRules.SupabaseCreateBody"/>); a project of that name in the organisation is adopted (and its
/// password reset), so a retried job doesn't create a second one. The free-plan project limit is <c>PLATFORM_PLAN_LIMIT</c>.</item>
/// <item>Link: <c>GET /projects/{ref}</c>, then <c>PATCH /projects/{ref}/database/password</c> when AIDE doesn't hold its password.</item>
/// <item>Poll: <c>GET /projects/{ref}</c> until <c>ACTIVE_HEALTHY</c>, then <c>GET …/health?services=db,pooler,auth,rest</c>,
/// the keys (<c>GET …/api-keys?reveal=true</c>, <c>POST …/api-keys {type, name}</c> for a missing publishable or secret key)
/// and the pooler (<c>GET …/config/database/pooler</c>: host, user and database).</item>
/// <item>Restore (<c>POST …/restore</c>), Auth URLs (<c>PATCH …/config/auth</c>) and security advisors (<c>GET …/advisors/security</c>).</item>
/// </list>
/// </summary>
public sealed class SupabaseDatabaseProvider(PlatformHttp http) : IDatabaseProvider
{
    private const ManagedDatabaseProvider Supabase = ManagedDatabaseProvider.Supabase;

    /// <summary>The name of the keys AIDE creates when a project has no publishable or secret key.</summary>
    public const string KeyName = "aide";

    public ManagedDatabaseProvider Kind => Supabase;

    public async Task<DatabaseSetupResult> SetupAsync(DatabaseAuth auth, DatabaseSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == DatabaseSetupMode.Link)
        {
            var projectRef = request.LinkResourceId!;
            using var project = await SendAsync(auth, HttpMethod.Get, $"projects/{Uri.EscapeDataString(projectRef)}", null, cancellationToken);
            if (request.Password is { } password)
            {
                await ResetPasswordAsync(auth, projectRef, password, cancellationToken);
            }

            return Result(project.RootElement, projectRef, request.Password is not null);
        }

        var org = auth.AccountId ?? throw new DatabaseProviderException(
            DeploymentFailure.Rejected, 400, "The Supabase connection has no organisation; choose one in Settings → Connections.", Supabase);
        var pass = request.Password ?? throw new DatabaseProviderException(DeploymentFailure.Rejected, 400, "A new Supabase project needs a password.", Supabase);
        using (var existing = await SendAsync(auth, HttpMethod.Get, $"organizations/{Uri.EscapeDataString(org)}/projects", null, cancellationToken))
        {
            var match = Items(existing.RootElement, "projects").FirstOrDefault(p => Text(p, "name") == request.Name);
            if ((Text(match, "ref") ?? Text(match, "id")) is { } found)
            {
                await ResetPasswordAsync(auth, found, pass, cancellationToken);
                return Result(match, found, true);
            }
        }

        using var created = await SendAsync(
            auth,
            HttpMethod.Post,
            "projects",
            ManagedDatabaseRules.SupabaseCreateBody(request.Name, org, pass, request.Region ?? "americas"),
            cancellationToken,
            ex => IsPlanLimit(ex) ? DeploymentFailure.PlanLimit : null);
        var id = Text(created.RootElement, "ref") ?? Text(created.RootElement, "id")
            ?? throw new DatabaseProviderException(DeploymentFailure.Upstream, null, "Supabase created the project but returned no ref.", Supabase);
        return Result(created.RootElement, id, true);
    }

    public async Task<DatabasePoll> PollAsync(DatabaseAuth auth, DatabaseRef database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(database);
        var projectRef = database.ResourceId ?? throw new DatabaseProviderException(DeploymentFailure.NotFound, 404, "The Supabase project isn't known.", Supabase);
        var path = $"projects/{Uri.EscapeDataString(projectRef)}";
        string? region;
        string? status;
        using (var project = await SendAsync(auth, HttpMethod.Get, path, null, cancellationToken))
        {
            region = Text(project.RootElement, "region");
            status = Text(project.RootElement, "status");
        }

        var state = ManagedDatabaseRules.SupabaseState(status);
        if (state.Status != ManagedDatabaseStatus.Available)
        {
            return new DatabasePoll(state.Status, state.Terminal, $"Supabase project status {status}", database.Connection, Region: region);
        }

        using (var health = await SendAsync(auth, HttpMethod.Get, $"{path}/health?services=db,pooler,auth,rest", null, cancellationToken))
        {
            var services = Items(health.RootElement)
                .Select(s => (Name: Text(s, "name") ?? string.Empty, Status: Text(s, "status"), Healthy: Property(s, "healthy").ValueKind switch
                {
                    JsonValueKind.True => (bool?)true,
                    JsonValueKind.False => false,
                    _ => null,
                }))
                .ToList();
            if (!ManagedDatabaseRules.SupabaseHealthy(services))
            {
                return new DatabasePoll(ManagedDatabaseStatus.Provisioning, false, "Waiting for the database, pooler, auth and REST services", database.Connection, Region: region);
            }
        }

        var (publishable, secret) = await KeysAsync(auth, path, cancellationToken);
        using var pooler = await SendAsync(auth, HttpMethod.Get, $"{path}/config/database/pooler", null, cancellationToken);
        var entries = Items(pooler.RootElement).ToList();
        var primary = entries.FirstOrDefault(e => string.Equals(Text(e, "database_type"), "PRIMARY", StringComparison.OrdinalIgnoreCase));
        primary = primary.ValueKind == JsonValueKind.Object ? primary : entries.FirstOrDefault();
        var host = Text(primary, "db_host") ?? throw new DatabaseProviderException(DeploymentFailure.Upstream, null, "Supabase returned no pooler host.", Supabase);
        var connection = (database.Connection ?? new ManagedDatabaseConnection()) with
        {
            SupabaseRef = projectRef,
            PoolerHost = host,
            PoolerUser = Text(primary, "db_user") ?? $"postgres.{projectRef}",
            DatabaseName = Text(primary, "db_name") ?? ManagedDatabaseRules.DefaultDatabaseName,
            PublishableKey = publishable,
            SecretKey = secret,
            DashboardUrl = Dashboard(projectRef),
        };
        return new DatabasePoll(ManagedDatabaseStatus.Available, true, null, connection, Region: region);
    }

    public async Task RestoreAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var _ = await SendAsync(auth, HttpMethod.Post, $"projects/{Uri.EscapeDataString(resourceId)}/restore", new { }, cancellationToken);
    }

    public async Task ApplyAuthUrlsAsync(DatabaseAuth auth, string resourceId, string siteUrl, string allowList, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var _ = await SendAsync(
            auth, HttpMethod.Patch, $"projects/{Uri.EscapeDataString(resourceId)}/config/auth", new { site_url = siteUrl, uri_allow_list = allowList }, cancellationToken);
    }

    public async Task<IReadOnlyList<AdvisorFinding>> SecurityAdvisorsAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        using var json = await SendAsync(auth, HttpMethod.Get, $"projects/{Uri.EscapeDataString(resourceId)}/advisors/security", null, cancellationToken);
        return
        [
            .. Items(json.RootElement, "lints")
                .Select(l => new AdvisorFinding(
                    Text(l, "name") ?? "unknown",
                    Text(l, "level") ?? "WARN",
                    Text(l, "title") ?? Text(l, "name") ?? "Advisor finding",
                    Clip(Text(l, "detail")),
                    Text(l, "remediation")))
                .Take(100),
        ];
    }

    /// <summary>The publishable and secret keys, creating each that is missing (FR-153).</summary>
    private async Task<(string? Publishable, string? Secret)> KeysAsync(DatabaseAuth auth, string path, CancellationToken cancellationToken)
    {
        var (publishable, secret) = await ReadKeysAsync(auth, path, cancellationToken);
        if (publishable is not null && secret is not null)
        {
            return (publishable, secret);
        }

        foreach (var type in new[] { publishable is null ? "publishable" : null, secret is null ? "secret" : null }.OfType<string>())
        {
            using var _ = await SendAsync(auth, HttpMethod.Post, $"{path}/api-keys", new { type, name = KeyName }, cancellationToken);
        }

        return await ReadKeysAsync(auth, path, cancellationToken);
    }

    private async Task<(string? Publishable, string? Secret)> ReadKeysAsync(DatabaseAuth auth, string path, CancellationToken cancellationToken)
    {
        using var keys = await SendAsync(auth, HttpMethod.Get, $"{path}/api-keys?reveal=true", null, cancellationToken);
        var list = Items(keys.RootElement).ToList();
        string? Key(string type) => list.Where(k => Text(k, "type") == type).Select(k => Text(k, "api_key")).FirstOrDefault(k => !string.IsNullOrEmpty(k));
        return (Key("publishable"), Key("secret"));
    }

    private async Task ResetPasswordAsync(DatabaseAuth auth, string projectRef, string password, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(auth, HttpMethod.Patch, $"projects/{Uri.EscapeDataString(projectRef)}/database/password", new { password }, cancellationToken);
    }

    private static DatabaseSetupResult Result(JsonElement project, string projectRef, bool passwordSet) =>
        new(projectRef, Text(project, "name") ?? projectRef, Text(project, "region"), null, passwordSet, Dashboard(projectRef));

    private static string Dashboard(string projectRef) => $"https://supabase.com/dashboard/project/{Uri.EscapeDataString(projectRef)}";

    /// <summary>Supabase refuses another free project (2 per organisation) with a 4xx naming the limit.</summary>
    private static bool IsPlanLimit(PlatformApiException ex) =>
        ex.Status is 400 or 402 or 403 && (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase));

    private static string? Clip(string? text) => text is null ? null : text.Length > 500 ? text[..500] : text;

    private Task<JsonDocument> SendAsync(
        DatabaseAuth auth, HttpMethod method, string path, object? body, CancellationToken cancellationToken, Func<PlatformApiException, DeploymentFailure?>? classify = null) =>
        DatabaseProviderException.Call(Supabase, () => http.SendAsync(PlatformProvider.Supabase, PlatformHosts.SupabaseClient, method, path, auth.Token, body, cancellationToken), classify);
}
