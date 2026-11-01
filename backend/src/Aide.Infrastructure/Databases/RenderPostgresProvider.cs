using System.Text.Json;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Databases;

/// <summary>
/// FR-152, §4.21 Render Postgres through an account API key, in the connection's workspace (<c>ownerId</c>):
/// <list type="bullet">
/// <item>Create: <c>POST /v1/postgres {name, ownerId, plan, version: "17", region, databaseName}</c>, the region being the
/// Owner's choice or the backend service's; an instance of that name is adopted, so a retried job doesn't create two. The
/// preview is a second instance named <c>&lt;name&gt;-preview</c>. Render's one-free-database limit is <c>PLATFORM_PLAN_LIMIT</c>.</item>
/// <item>Poll: <c>GET /v1/postgres/{id}</c> until <c>available</c> (<see cref="ManagedDatabaseRules.RenderState"/>; free plans
/// carry <c>expiresAt</c>), then <c>GET /v1/postgres/{id}/connection-info</c>: <c>internalConnectionString</c> when the
/// backend runs on Render, else <c>externalConnectionString</c>.</item>
/// </list>
/// </summary>
public sealed class RenderPostgresProvider(PlatformHttp http, TimeProvider time) : IDatabaseProvider
{
    private const ManagedDatabaseProvider Render = ManagedDatabaseProvider.Render;

    public ManagedDatabaseProvider Kind => Render;

    public async Task<DatabaseSetupResult> SetupAsync(DatabaseAuth auth, DatabaseSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == DatabaseSetupMode.Link)
        {
            using var linked = await SendAsync(auth, HttpMethod.Get, $"postgres/{Uri.EscapeDataString(request.LinkResourceId!)}", null, cancellationToken);
            return Result(linked.RootElement, request.LinkResourceId!);
        }

        var owner = auth.AccountId ?? throw new DatabaseProviderException(
            DeploymentFailure.Rejected, 400, "The Render connection has no workspace; choose one in Settings → Connections.", Render);
        using (var existing = await SendAsync(
            auth, HttpMethod.Get, $"postgres?name={Uri.EscapeDataString(request.Name)}&ownerId={Uri.EscapeDataString(owner)}&limit=20", null, cancellationToken))
        {
            var match = Items(existing.RootElement).Select(i => Property(i, "postgres")).FirstOrDefault(p => Text(p, "name") == request.Name);
            if (Text(match, "id") is { } found)
            {
                return Result(match, found);
            }
        }

        var region = request.Region ?? await BackendRegionAsync(auth, request.Host, cancellationToken);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = request.Name,
            ["ownerId"] = owner,
            ["plan"] = request.Plan ?? "free",
            ["version"] = ManagedDatabaseRules.RenderPostgresVersion,
            ["region"] = region,
            ["databaseName"] = ManagedDatabaseRules.RenderDatabaseName(request.Name),
        };
        using var created = await SendAsync(
            auth, HttpMethod.Post, "postgres", body, cancellationToken, ex => IsPlanLimit(ex) ? DeploymentFailure.PlanLimit : null);
        var id = Text(created.RootElement, "id")
            ?? throw new DatabaseProviderException(DeploymentFailure.Upstream, null, "Render created the database but returned no id.", Render);
        return Result(created.RootElement, id);
    }

    public async Task<DatabasePoll> PollAsync(DatabaseAuth auth, DatabaseRef database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(database);
        var id = database.ResourceId ?? throw new DatabaseProviderException(DeploymentFailure.NotFound, 404, "The Render database isn't known.", Render);
        string? status;
        DateTimeOffset? expiresAt;
        string? region;
        string? plan;
        string? name;
        using (var postgres = await SendAsync(auth, HttpMethod.Get, $"postgres/{Uri.EscapeDataString(id)}", null, cancellationToken))
        {
            var root = postgres.RootElement;
            status = Text(root, "status");
            expiresAt = Time(root, "expiresAt");
            region = Text(root, "region");
            plan = Text(root, "plan");
            name = Text(root, "name");
        }

        var state = ManagedDatabaseRules.RenderState(status, expiresAt, time.GetUtcNow());
        if (state.Status is not (ManagedDatabaseStatus.Available or ManagedDatabaseStatus.Expiring))
        {
            return new DatabasePoll(state.Status, state.Terminal, $"Render Postgres status {status}", database.Connection, expiresAt, region, plan);
        }

        using var info = await SendAsync(auth, HttpMethod.Get, $"postgres/{Uri.EscapeDataString(id)}/connection-info", null, cancellationToken);
        var internalUrl = Text(info.RootElement, "internalConnectionString");
        var externalUrl = Text(info.RootElement, "externalConnectionString");
        var connection = (database.Connection ?? new ManagedDatabaseConnection()) with
        {
            // §4.21: the internal URL for a backend in the same workspace and region; the external one for consumers elsewhere.
            Url = database.Host.RenderServiceId is not null ? internalUrl ?? externalUrl : externalUrl ?? internalUrl,
            ExternalUrl = externalUrl,
            Name = name,
            DashboardUrl = Dashboard(id),
        };
        return new DatabasePoll(state.Status, true, null, connection, expiresAt, region, plan);
    }

    public async Task<DatabaseLiveState?> ReadStateAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(resourceId);
        using var postgres = await SendAsync(auth, HttpMethod.Get, $"postgres/{Uri.EscapeDataString(resourceId)}", null, cancellationToken);
        var expiresAt = Time(postgres.RootElement, "expiresAt");
        return new DatabaseLiveState(ManagedDatabaseRules.RenderState(Text(postgres.RootElement, "status"), expiresAt, time.GetUtcNow()).Status, expiresAt);
    }

    public Task RestoreAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        throw new DatabaseProviderException(DeploymentFailure.Rejected, 400, "Render Postgres has no restore action; resume it in the Render dashboard.", Render);

    public Task ApplyAuthUrlsAsync(DatabaseAuth auth, string resourceId, string siteUrl, string allowList, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AdvisorFinding>> SecurityAdvisorsAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdvisorFinding>>([]);

    /// <summary>The backend service's region (§4.21: the database sits next to it); <c>null</c> lets Render choose.</summary>
    private async Task<string?> BackendRegionAsync(DatabaseAuth auth, DatabaseHostContext host, CancellationToken cancellationToken)
    {
        if (host.RenderServiceId is not { } serviceId)
        {
            return null;
        }

        using var service = await SendAsync(auth, HttpMethod.Get, $"services/{Uri.EscapeDataString(serviceId)}", null, cancellationToken);
        return Text(Property(service.RootElement, "serviceDetails"), "region") ?? Text(service.RootElement, "region");
    }

    private static DatabaseSetupResult Result(JsonElement postgres, string id) =>
        new(id, Text(postgres, "name") ?? id, Text(postgres, "region"), Text(postgres, "plan"), false, Dashboard(id));

    private static string Dashboard(string id) => $"https://dashboard.render.com/d/{Uri.EscapeDataString(id)}";

    private static bool IsPlanLimit(PlatformApiException ex) =>
        ex.Status is 400 or 402 or 403 or 409 && (ex.Message.Contains("free", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? Time(JsonElement element, string property) =>
        Text(element, property) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : null;

    private Task<JsonDocument> SendAsync(
        DatabaseAuth auth, HttpMethod method, string path, object? body, CancellationToken cancellationToken, Func<PlatformApiException, DeploymentFailure?>? classify = null) =>
        DatabaseProviderException.Call(Render, () => http.SendAsync(PlatformProvider.Render, PlatformHosts.RenderClient, method, path, auth.Token, body, cancellationToken), classify);
}
