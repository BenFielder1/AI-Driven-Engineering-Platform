using System.Collections.Concurrent;
using System.Reflection;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aide.UnitTests.Databases;

/// <summary>
/// FR-153, FR-154, §9: every Fake managed database has its own migration history, per project and environment, as real
/// databases do. Provider, runner and executor are the real ones; only the working copy is stubbed.
/// </summary>
public class FakeDatabaseHistoryTests
{
    private const string Version = "20260101000000";

    private readonly FakeAwareMigrationSqlExecutor _executor = new(new NpgsqlMigrationSqlExecutor());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A database set up like the provisioning job does: created, polled until available, as a runner connection string.</summary>
    private static async Task<string> ConnectionAsync(ManagedDatabaseProvider kind, Guid projectId, DeploymentEnvironment environment, string name)
    {
        var fake = new FakeDatabaseProvider(kind, new ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        var auth = new DatabaseAuth("fake", null);
        var setup = await fake.SetupAsync(
            auth, new DatabaseSetupRequest(projectId, environment, DatabaseSetupMode.Create, name, null, null, null, "pw", DatabaseHostContext.None), Ct);
        var database = new DatabaseRef(setup.ResourceId, environment, name, "pw", null, DatabaseHostContext.None);
        _ = await fake.PollAsync(auth, database, Ct);
        var connection = (await fake.PollAsync(auth, database, Ct)).Connection!;
        return kind == ManagedDatabaseProvider.Supabase
            ? SupabaseMigrationRunner.SessionConnectionString(connection, "pw")
            : MigrationConnections.ToNpgsql(connection.Url)!;
    }

    /// <summary>A run of the project's one generated migration, whose header names the project (so its content differs per project).</summary>
    private Task<MigrationRunResult> RunAsync(string connectionString, string projectName)
    {
        const string path = $"supabase/migrations/{Version}_init.sql";
        var copies = DispatchProxy.Create<IWorkingCopies, ListedWorkingCopy>();
        ((ListedWorkingCopy)(object)copies).Paths = [path];
        ((ListedWorkingCopy)(object)copies).Texts = new(StringComparer.Ordinal) { [path] = $"-- {projectName}\ncreate table notes (id uuid primary key);" };
        var runner = new SupabaseMigrationRunner(copies, _executor, NullLogger<SupabaseMigrationRunner>.Instance);
        return runner.RunAsync(new MigrationRunRequest(Guid.CreateVersion7(), "abc1234def", connectionString, null, null, null), Ct);
    }

    private static void AssertApplied(MigrationRunResult result)
    {
        Assert.Equal((MigrationRunStatus.Succeeded, (string?)null), (result.Status, result.ErrorCode));
        Assert.Equal([Version], result.Applied.Select(a => a.Version));
    }

    [Theory]
    [InlineData(ManagedDatabaseProvider.Supabase)]
    [InlineData(ManagedDatabaseProvider.Render)]
    public async Task FR153_Two_Fake_projects_each_apply_their_own_migrations_on_their_preview_database(ManagedDatabaseProvider kind)
    {
        var first = await ConnectionAsync(kind, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes-first");
        var second = await ConnectionAsync(kind, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes-second");

        AssertApplied(await RunAsync(first, "Notes first"));
        AssertApplied(await RunAsync(second, "Notes second"));
    }

    [Fact]
    public async Task FR153_Two_Fake_projects_with_the_same_name_do_not_share_a_database()
    {
        var first = await ConnectionAsync(ManagedDatabaseProvider.Supabase, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes");
        var second = await ConnectionAsync(ManagedDatabaseProvider.Supabase, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes");

        AssertApplied(await RunAsync(first, "Notes of one owner"));
        AssertApplied(await RunAsync(second, "Notes of another owner"));
    }

    [Fact]
    public async Task FR154_Production_applies_the_files_preview_already_applied_because_its_history_is_separate()
    {
        var projectId = Guid.CreateVersion7();
        var preview = await ConnectionAsync(ManagedDatabaseProvider.Supabase, projectId, DeploymentEnvironment.Preview, "notes-preview");
        var production = await ConnectionAsync(ManagedDatabaseProvider.Supabase, projectId, DeploymentEnvironment.Production, "notes");

        AssertApplied(await RunAsync(preview, "Notes"));
        AssertApplied(await RunAsync(production, "Notes"));
    }

    [Fact]
    public async Task FR153_A_rerun_on_the_same_Fake_database_applies_nothing_and_a_changed_file_still_diverges()
    {
        var database = await ConnectionAsync(ManagedDatabaseProvider.Supabase, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes");

        AssertApplied(await RunAsync(database, "Notes"));
        var rerun = await RunAsync(database, "Notes");
        var changed = await RunAsync(database, "Renamed notes");

        Assert.Equal((MigrationRunStatus.Succeeded, 0), (rerun.Status, rerun.Applied.Count));
        Assert.Equal((MigrationRunStatus.Failed, Aide.Domain.Common.ErrorCodes.MigrationHistoryDiverged), (changed.Status, changed.ErrorCode));
    }

    [Fact]
    public async Task FR148_Resetting_one_Fake_preview_database_leaves_other_databases_history_alone()
    {
        var reset = await ConnectionAsync(ManagedDatabaseProvider.Supabase, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes-reset");
        var other = await ConnectionAsync(ManagedDatabaseProvider.Supabase, Guid.CreateVersion7(), DeploymentEnvironment.Preview, "notes-other");
        AssertApplied(await RunAsync(reset, "Reset"));
        AssertApplied(await RunAsync(other, "Other"));

        await _executor.ResetAsync(reset, supabaseGrants: true, Ct);

        AssertApplied(await RunAsync(reset, "Reset"));
        Assert.Empty((await RunAsync(other, "Other")).Applied);
    }

    [Fact]
    public async Task S4_21_Linking_the_same_Fake_resource_gives_the_same_database_so_separation_can_refuse_it()
    {
        var fake = new FakeDatabaseProvider(ManagedDatabaseProvider.Supabase, new ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        var auth = new DatabaseAuth("fake", null);
        var database = new DatabaseRef("a-linked-project-whose-id-is-long", DeploymentEnvironment.Preview, "linked", "pw", null, DatabaseHostContext.None);
        _ = await fake.PollAsync(auth, database, Ct);

        var preview = (await fake.PollAsync(auth, database, Ct)).Connection!;
        _ = await fake.PollAsync(auth, database with { Environment = DeploymentEnvironment.Production }, Ct);
        var production = (await fake.PollAsync(auth, database with { Environment = DeploymentEnvironment.Production }, Ct)).Connection!;
        var linkedRef = database with { ResourceId = "abcd" };
        _ = await fake.PollAsync(auth, linkedRef, Ct);
        var shortRef = (await fake.PollAsync(auth, linkedRef, Ct)).Connection!;

        Assert.Equal(preview.SupabaseRef, production.SupabaseRef);
        Assert.Equal("abcd", shortRef.SupabaseRef);
    }
}
