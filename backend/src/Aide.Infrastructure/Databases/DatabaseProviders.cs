using System.Collections.Concurrent;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Databases;

/// <summary>
/// FR-152, §4.21 External: AIDE only stores the connection string (encrypted, by the PUT) and syncs it; there is no
/// platform. Setup and polls make no call; the database is available at once.
/// </summary>
public sealed class ExternalDatabaseProvider : IDatabaseProvider
{
    public ManagedDatabaseProvider Kind => ManagedDatabaseProvider.External;

    public Task<DatabaseSetupResult> SetupAsync(DatabaseAuth auth, DatabaseSetupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DatabaseSetupResult(null, "External", null, null, false, null));

    public Task<DatabasePoll> PollAsync(DatabaseAuth auth, DatabaseRef database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Task.FromResult(database.Connection?.Url is null
            ? new DatabasePoll(ManagedDatabaseStatus.Error, true, "No connection string is stored.", database.Connection)
            : new DatabasePoll(ManagedDatabaseStatus.Available, true, null, database.Connection));
    }

    public Task<DatabaseLiveState?> ReadStateAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        Task.FromResult<DatabaseLiveState?>(null);

    public Task RestoreAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        throw new DatabaseProviderException(DeploymentFailure.Rejected, 400, "An External database has no restore action.", ManagedDatabaseProvider.External);

    public Task ApplyAuthUrlsAsync(DatabaseAuth auth, string resourceId, string siteUrl, string allowList, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AdvisorFinding>> SecurityAdvisorsAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdvisorFinding>>([]);
}

/// <summary>
/// §9 Fake databases (<c>Deployments:Provider=Fake</c>, or the <c>fake</c> provider), emulating <see cref="Kind"/>'s
/// connection shape. Deterministic: a resource is available on its 2nd poll (never when its name contains <c>never</c>,
/// for the timeout); tokens starting <c>fake_invalid</c> are rejected, <c>fake_limit</c> hit the plan limit, and
/// <c>fake_scope</c> lack Supabase's "API Key Secrets: Read" when the keys are read. Restore makes a paused one available.
/// </summary>
public sealed class FakeDatabaseProvider(ManagedDatabaseProvider kind, ConcurrentDictionary<string, int> polls) : IDatabaseProvider
{
    public const string ApiKeySecretsRead = "API Key Secrets: Read";

    public ManagedDatabaseProvider Kind => kind;

    public Task<DatabaseSetupResult> SetupAsync(DatabaseAuth auth, DatabaseSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        Check(auth);
        if (request.Mode == DatabaseSetupMode.Create && auth.Token.StartsWith("fake_limit", StringComparison.Ordinal))
        {
            throw new DatabaseProviderException(DeploymentFailure.PlanLimit, 402, $"{ManagedDatabaseRules.DisplayName(kind)}: the free plan allows 2 projects; link an existing one instead.", kind);
        }

        if (kind == ManagedDatabaseProvider.External)
        {
            return Task.FromResult(new DatabaseSetupResult(null, "External", null, null, false, null));
        }

        var id = request.Mode == DatabaseSetupMode.Link ? request.LinkResourceId! : $"fake-{kind.ToWire()}-{request.Environment.ToWire()}-{request.Name}";
        return Task.FromResult(new DatabaseSetupResult(id, request.Name, request.Region ?? "fake-region", request.Plan ?? "free", request.Password is not null, $"https://fake.test/databases/{id}"));
    }

    public Task<DatabasePoll> PollAsync(DatabaseAuth auth, DatabaseRef database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(database);
        Check(auth);
        if (kind == ManagedDatabaseProvider.External)
        {
            return new ExternalDatabaseProvider().PollAsync(auth, database, cancellationToken);
        }

        var id = database.ResourceId ?? "fake";
        var count = polls.AddOrUpdate($"{id}|{database.Environment}", 1, (_, n) => n + 1);
        if (count < 2 || id.Contains("never", StringComparison.Ordinal))
        {
            return Task.FromResult(new DatabasePoll(ManagedDatabaseStatus.Provisioning, false, "Fake database is starting", database.Connection));
        }

        if (kind == ManagedDatabaseProvider.Supabase && auth.Token.StartsWith("fake_scope", StringComparison.Ordinal))
        {
            throw new DatabaseProviderException(DeploymentFailure.Forbidden, 403, "Supabase: missing permissions", kind, missingPermissions: [ApiKeySecretsRead]);
        }

        var env = database.Environment.ToWire();
        var slug = new string([.. id.Where(char.IsAsciiLetterOrDigit).Take(20)]).ToLowerInvariant();
        var connection = (database.Connection ?? new ManagedDatabaseConnection()) with
        {
            Name = database.Name,
            DashboardUrl = $"https://fake.test/databases/{id}",
        };
        connection = kind switch
        {
            ManagedDatabaseProvider.Supabase => connection with
            {
                SupabaseRef = slug,
                PoolerHost = "aws-0-fake-1.pooler.supabase.test",
                PoolerUser = $"postgres.{slug}",
                DatabaseName = ManagedDatabaseRules.DefaultDatabaseName,
                PublishableKey = $"sb_publishable_fake_{env}",
                SecretKey = $"sb_secret_fake_{env}",
            },
            ManagedDatabaseProvider.Railway => connection with { RailwayService = ManagedDatabaseRules.RailwayServiceName },
            _ => connection with { Url = $"postgresql://aide:fake@fake-db-{env}.test:5432/{slug}" },
        };
        return Task.FromResult(new DatabasePoll(ManagedDatabaseStatus.Available, true, null, connection, Region: "fake-region", Plan: "free"));
    }

    /// <summary>FR-155: a Supabase resource whose id contains <c>paused</c> reads as paused; the others have nothing to say.</summary>
    public Task<DatabaseLiveState?> ReadStateAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(resourceId);
        Check(auth);
        return Task.FromResult<DatabaseLiveState?>(kind != ManagedDatabaseProvider.Supabase
            ? null
            : new DatabaseLiveState(resourceId.Contains("paused", StringComparison.Ordinal) ? ManagedDatabaseStatus.Paused : ManagedDatabaseStatus.Available, null));
    }

    public Task RestoreAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        Check(auth);
        return Task.CompletedTask;
    }

    public Task ApplyAuthUrlsAsync(DatabaseAuth auth, string resourceId, string siteUrl, string allowList, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AdvisorFinding>> SecurityAdvisorsAsync(DatabaseAuth auth, string resourceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdvisorFinding>>(
            kind == ManagedDatabaseProvider.Supabase
                ? [new AdvisorFinding("rls_disabled_in_public", "ERROR", "RLS Disabled in Public", "Fake finding: a table in public has RLS disabled.", null)]
                : []);

    private void Check(DatabaseAuth auth)
    {
        if (auth.Token.StartsWith("fake_invalid", StringComparison.Ordinal))
        {
            throw new DatabaseProviderException(DeploymentFailure.CredentialRejected, 401, $"{ManagedDatabaseRules.DisplayName(kind)}: invalid token", kind);
        }
    }
}

/// <summary>§9: the database provider for a host: External always; the Fake in Fake mode or for <c>fake</c>; else the platform's.</summary>
public sealed class DatabaseProviderResolver(
    IOptions<DeploymentOptions> options,
    SupabaseDatabaseProvider supabase,
    RenderPostgresProvider render,
    RailwayPostgresProvider railway,
    ExternalDatabaseProvider external) : IDatabaseProviderResolver
{
    private readonly ConcurrentDictionary<string, int> _polls = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<ManagedDatabaseProvider, FakeDatabaseProvider> _fakes = new();

    public bool UsesFake => options.Value.UseFake;

    public IDatabaseProvider ForHost(ManagedDatabaseProvider provider)
    {
        if (provider == ManagedDatabaseProvider.External)
        {
            return external;
        }

        if (UsesFake || provider == ManagedDatabaseProvider.Fake)
        {
            return _fakes.GetOrAdd(provider, p => new FakeDatabaseProvider(p, _polls));
        }

        return provider switch
        {
            ManagedDatabaseProvider.Supabase => supabase,
            ManagedDatabaseProvider.Render => render,
            _ => railway,
        };
    }
}
