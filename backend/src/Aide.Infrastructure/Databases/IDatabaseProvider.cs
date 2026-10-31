using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Databases;

/// <summary>A database provider's credential: the decrypted platform token and the connection's account (Supabase organisation slug, Render owner, Railway workspace). Never logged.</summary>
public sealed record DatabaseAuth(string Token, string? AccountId)
{
    public override string ToString() => $"DatabaseAuth {{ AccountId = {AccountId} }}";
}

/// <summary>
/// Where the app runs, for providers that place the database next to it (§4.21): the Render backend service (its region),
/// the Railway target's <c>project:service:env</c> and its <c>aide-preview</c> environment id.
/// </summary>
public sealed record DatabaseHostContext(string? RenderServiceId, string? RailwayResourceId, string? RailwayPreviewEnvironmentId)
{
    public static DatabaseHostContext None { get; } = new(null, null, null);
}

/// <summary>
/// FR-152: create or link a database for one environment. <see cref="Name"/> is the resource name to create (Render
/// preview: <c>&lt;name&gt;-preview</c>); <see cref="Password"/> the AIDE-generated database password (Supabase create, and
/// the reset when linking a project whose password AIDE doesn't hold; Railway).
/// </summary>
public sealed record DatabaseSetupRequest(
    Guid ProjectId,
    DeploymentEnvironment Environment,
    DatabaseSetupMode Mode,
    string Name,
    string? Region,
    string? Plan,
    string? LinkResourceId,
    string? Password,
    DatabaseHostContext Host)
{
    public override string ToString() => $"DatabaseSetupRequest {{ ProjectId = {ProjectId}, Environment = {Environment}, Mode = {Mode}, Name = {Name} }}";
}

/// <summary>The created or linked resource: its id and name, region and plan when known, and the password set (if any).</summary>
public sealed record DatabaseSetupResult(string? ResourceId, string Name, string? Region, string? Plan, bool PasswordSet, string? DashboardUrl)
{
    public override string ToString() => $"DatabaseSetupResult {{ ResourceId = {ResourceId}, Name = {Name} }}";
}

/// <summary>A database as a provider sees it on a poll: its resource, environment, name, the password AIDE holds and what is stored.</summary>
public sealed record DatabaseRef(string? ResourceId, DeploymentEnvironment Environment, string? Name, string? Password, ManagedDatabaseConnection? Connection, DatabaseHostContext Host)
{
    public override string ToString() => $"DatabaseRef {{ ResourceId = {ResourceId}, Environment = {Environment} }}";
}

/// <summary>
/// One provisioning poll (§4.21): the state (<see cref="Terminal"/> ends polling), a detail for the card, and, once
/// available, the connection details (encrypted by the caller), expiry, region and plan.
/// </summary>
public sealed record DatabasePoll(
    ManagedDatabaseStatus Status,
    bool Terminal,
    string? Detail,
    ManagedDatabaseConnection? Connection,
    DateTimeOffset? ExpiresAt = null,
    string? Region = null,
    string? Plan = null)
{
    public override string ToString() => $"DatabasePoll {{ Status = {Status}, Terminal = {Terminal} }}";
}

/// <summary>
/// FR-152, FR-153, §4.21: one database host (Supabase, Render Postgres, Railway Postgres, External, Fake). Platform calls
/// happen only in jobs (the <c>database_provisioning</c> job, the migration runner, the restore action). Failures throw
/// <see cref="DatabaseProviderException"/>.
/// </summary>
public interface IDatabaseProvider
{
    ManagedDatabaseProvider Kind { get; }

    /// <summary>Creates (idempotently: an existing resource of the same name is adopted) or links the database.</summary>
    Task<DatabaseSetupResult> SetupAsync(DatabaseAuth auth, DatabaseSetupRequest request, CancellationToken cancellationToken);

    /// <summary>Reads the database's state; once available, its connection details.</summary>
    Task<DatabasePoll> PollAsync(DatabaseAuth auth, DatabaseRef database, CancellationToken cancellationToken);

    /// <summary>Restores a paused database (Supabase <c>POST /projects/{ref}/restore</c>); others: <see cref="DeploymentFailure.Rejected"/>.</summary>
    Task RestoreAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken);

    /// <summary>FR-153: Supabase Auth <c>site_url</c> and <c>uri_allow_list</c> (comma-separated); a no-op elsewhere.</summary>
    Task ApplyAuthUrlsAsync(DatabaseAuth auth, string resourceId, string siteUrl, string allowList, CancellationToken cancellationToken);

    /// <summary>FR-153: Supabase security advisor findings; empty elsewhere.</summary>
    Task<IReadOnlyList<AdvisorFinding>> SecurityAdvisorsAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken);
}

/// <summary>The provider for a database host: the Fake (emulating the host) in Fake mode or for <c>fake</c>.</summary>
public interface IDatabaseProviderResolver
{
    bool UsesFake { get; }

    IDatabaseProvider ForHost(ManagedDatabaseProvider provider);
}

/// <summary>
/// A failed database provider call: how (<see cref="DeploymentFailure"/>; <c>PlanLimit</c> is <c>PLATFORM_PLAN_LIMIT</c>),
/// the HTTP status, a cleaned message (never a secret), the permissions the platform named, and the rate-limit reset.
/// </summary>
public sealed class DatabaseProviderException : Exception
{
    public DatabaseProviderException()
    {
    }

    public DatabaseProviderException(string message)
        : base(message)
    {
    }

    public DatabaseProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DatabaseProviderException(
        DeploymentFailure failure, int? status, string message, ManagedDatabaseProvider provider, DateTimeOffset? retryAt = null, IReadOnlyList<string>? missingPermissions = null)
        : base(message)
    {
        Failure = failure;
        Status = status;
        Provider = provider;
        RetryAt = retryAt;
        MissingPermissions = missingPermissions ?? [];
    }

    public DeploymentFailure Failure { get; }

    public int? Status { get; }

    public ManagedDatabaseProvider Provider { get; }

    public DateTimeOffset? RetryAt { get; }

    public IReadOnlyList<string> MissingPermissions { get; } = [];

    /// <summary>A platform API failure as a database failure (as <see cref="Deployments.PlatformDeploymentCalls.Map"/>).</summary>
    public static DatabaseProviderException From(ManagedDatabaseProvider provider, PlatformApiException ex, DeploymentFailure? special = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var failure = special ?? ex.Failure switch
        {
            PlatformFailure.Rejected => DeploymentFailure.CredentialRejected,
            PlatformFailure.MissingPermissions => DeploymentFailure.Forbidden,
            PlatformFailure.RateLimited => DeploymentFailure.RateLimited,
            PlatformFailure.NotFound => DeploymentFailure.NotFound,
            _ when ex.Status is >= 400 and < 500 => DeploymentFailure.Rejected,
            _ => DeploymentFailure.Upstream,
        };
        return new DatabaseProviderException(failure, ex.Status, ex.Message, provider, ex.RetryAt, ex.MissingPermissions);
    }

    /// <summary>Runs a platform call, mapping <see cref="PlatformApiException"/>.</summary>
    public static async Task<T> Call<T>(ManagedDatabaseProvider provider, Func<Task<T>> call, Func<PlatformApiException, DeploymentFailure?>? classify = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return await call();
        }
        catch (PlatformApiException ex)
        {
            throw From(provider, ex, classify?.Invoke(ex));
        }
    }
}
