using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Aide.Api.Features.Databases;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Git;
using Aide.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aide.IntegrationTests.Databases;

/// <summary>
/// FR-153, §4.21, D-V4-12: AIDE's Supabase BaaS migration runner against a real PostgreSQL (Testcontainers), and the
/// read-only migration run endpoints.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MigrationRunTests(AideApiFactory factory)
{
    private const string First = "create table orders (id bigint primary key);\nalter table orders enable row level security;\n";
    private const string Second = "create table items (id bigint primary key, order_id bigint references orders (id));\nalter table items enable row level security;\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SupabaseMigrationRunner Runner(Dictionary<string, string> files)
    {
        var copies = DispatchProxy.Create<IWorkingCopies, WorkingCopiesAtCommit>();
        ((WorkingCopiesAtCommit)(object)copies).Files = files;
        return new SupabaseMigrationRunner(copies, new NpgsqlMigrationSqlExecutor(), NullLogger<SupabaseMigrationRunner>.Instance);
    }

    private static MigrationRunRequest Request(string connectionString, IDatabaseProvider? advisors = null) =>
        new(Guid.CreateVersion7(), "abc1234def", connectionString, advisors, advisors is null ? null : new DatabaseAuth("fake", null), advisors is null ? null : "abcd");

    private static async Task<List<(string Version, string? Name, string[] Statements)>> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select version, name, statements from supabase_migrations.schema_migrations order by version", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string?, string[])>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }

        return rows;
    }

    [Fact]
    public async Task FR153_The_runner_creates_the_CLI_history_applies_pending_files_in_order_and_a_rerun_applies_nothing()
    {
        var database = await factory.CreateEmptyDatabaseAsync(Ct);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["supabase/migrations/20260102000000_items.sql"] = Second,
            ["supabase/migrations/20260101000000_orders.sql"] = First,
            ["supabase/config.toml"] = "project_id = \"todo\"",
        };
        var fake = new FakeDatabaseProvider(ManagedDatabaseProvider.Supabase, new ConcurrentDictionary<string, int>(StringComparer.Ordinal));

        var first = await Runner(files).RunAsync(Request(database, fake), Ct);
        var again = await Runner(files).RunAsync(Request(database), Ct);

        Assert.Equal((MigrationRunStatus.Succeeded, (string?)null), (first.Status, first.ErrorCode));
        Assert.Equal(["20260101000000", "20260102000000"], first.Applied.Select(a => a.Version).ToArray());
        Assert.Equal("rls_disabled_in_public", Assert.Single(first.Advisors).Name);
        Assert.Equal((MigrationRunStatus.Succeeded, 0), (again.Status, again.Applied.Count));
        var history = await HistoryAsync(database);
        Assert.Equal([("20260101000000", "orders"), ("20260102000000", "items")], history.Select(h => (h.Version, h.Name)).ToArray());
        Assert.Equal([First], history[0].Statements);
    }

    [Fact]
    public async Task FR153_A_recorded_file_that_changed_is_MIGRATION_HISTORY_DIVERGED_and_nothing_is_applied()
    {
        var database = await factory.CreateEmptyDatabaseAsync(Ct);
        await Runner(new(StringComparer.Ordinal) { ["supabase/migrations/0001_orders.sql"] = First }).RunAsync(Request(database), Ct);

        var diverged = await Runner(new(StringComparer.Ordinal)
        {
            ["supabase/migrations/0001_orders.sql"] = First + "alter table orders add column note text;",
            ["supabase/migrations/0002_items.sql"] = Second,
        }).RunAsync(Request(database), Ct);

        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationHistoryDiverged), (diverged.Status, diverged.ErrorCode));
        Assert.Equal(("0001", "changed"), (diverged.Diverged[0].Version, diverged.Diverged[0].Reason));
        Assert.Empty(diverged.Applied);
        Assert.Single(await HistoryAsync(database));
    }

    [Fact]
    public async Task FR153_A_failing_file_rolls_back_alone_with_MIGRATION_FAILED_and_earlier_files_stay_applied()
    {
        var database = await factory.CreateEmptyDatabaseAsync(Ct);

        var result = await Runner(new(StringComparer.Ordinal)
        {
            ["supabase/migrations/0001_orders.sql"] = First,
            ["supabase/migrations/0002_broken.sql"] = "create table broken (id bigint);\nselect * from no_such_table;",
        }).RunAsync(Request(database), Ct);

        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationFailed), (result.Status, result.ErrorCode));
        Assert.StartsWith("0002_broken.sql failed (SQLSTATE ", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(["0001"], result.Applied.Select(a => a.Version).ToArray());
        Assert.Equal(["0001"], (await HistoryAsync(database)).Select(h => h.Version).ToArray());
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Ct);
        await using var exists = new NpgsqlCommand("select to_regclass('public.broken') is null", connection);
        Assert.True((bool)(await exists.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task FR153_Members_list_and_read_migration_runs_with_files_durations_and_errors()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var versionId = await DeploymentHarness.VersionIdAsync(factory, id);
        var database = new ManagedDatabase { ProjectId = id, Environment = DeploymentEnvironment.Preview, Provider = ManagedDatabaseProvider.Supabase, Status = ManagedDatabaseStatus.Available, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var older = new DatabaseMigrationRun
        {
            ProjectId = id,
            ManagedDatabaseId = database.Id,
            ReleaseVersionId = versionId,
            GitSha = "aaa111",
            Environment = DeploymentEnvironment.Preview,
            Status = MigrationRunStatus.Succeeded,
            Applied = [new AppliedMigration("0001", "orders", 120), new AppliedMigration("0002", "items", 30)],
            AdvisorFindings = [new AdvisorFinding("rls_disabled_in_public", "ERROR", "RLS Disabled in Public", null, null)],
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
        };
        var newer = new DatabaseMigrationRun
        {
            ProjectId = id,
            ManagedDatabaseId = database.Id,
            ReleaseVersionId = versionId,
            GitSha = "bbb222",
            Environment = DeploymentEnvironment.Preview,
            Status = MigrationRunStatus.Failed,
            ErrorCode = ErrorCodes.MigrationHistoryDiverged,
            ErrorMessage = "0001_orders changed after it was applied",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.ManagedDatabases.Add(database);
            db.DatabaseMigrationRuns.AddRange(older, newer);
            await db.SaveChangesAsync(Ct);
        });

        var runs = await ProjectApi.ReadAsync<List<MigrationRunDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/migrations", UriKind.Relative), Ct));
        var one = await ProjectApi.ReadAsync<MigrationRunDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/migrations/{older.Id}", UriKind.Relative), Ct));
        var otherVersion = await ProjectApi.ReadAsync<List<MigrationRunDto>>(
            await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/migrations?releaseVersionId={Guid.NewGuid()}", UriKind.Relative), Ct));

        Assert.Equal([newer.Id, older.Id], runs.Select(r => r.Id).ToArray());
        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationHistoryDiverged), (runs[0].Status, runs[0].ErrorCode));
        Assert.Equal((150L, 2, "aaa111", ManagedDatabaseProvider.Supabase), (one.TotalMs, one.Files.Count, one.GitSha, one.Provider!.Value));
        Assert.Equal("rls_disabled_in_public", Assert.Single(one.AdvisorFindings).Name);
        Assert.Empty(otherVersion);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/migrations/{Guid.NewGuid()}", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"/api/projects/{id}/migrations", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }
}

/// <summary>The working copy at a commit, as the runner reads it: the given files (every other member is unused).</summary>
public class WorkingCopiesAtCommit : DispatchProxy
{
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IWorkingCopies.ListPathsAtCommitAsync) => Task.FromResult<IReadOnlyList<string>?>([.. Files.Keys.Order(StringComparer.Ordinal)]),
        nameof(IWorkingCopies.ReadTextAtCommitAsync) => Task.FromResult<IReadOnlyDictionary<string, string>>(
            ((IReadOnlyList<string>)args![2]!).Where(Files.ContainsKey).ToDictionary(p => p, p => Files[p], StringComparer.Ordinal)),
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
