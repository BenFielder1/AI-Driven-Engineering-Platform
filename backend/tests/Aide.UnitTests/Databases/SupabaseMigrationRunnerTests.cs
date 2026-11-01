using System.Reflection;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aide.UnitTests.Databases;

/// <summary>FR-153, §4.21: the Supabase migration runner with a fake working copy and a fake SQL session (no database).</summary>
public class SupabaseMigrationRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SupabaseMigrationRunner Runner, FakeExecutor Executor) Runner(IEnumerable<string> listed, Dictionary<string, string> readable, params RecordedMigration[] recorded)
    {
        var executor = new FakeExecutor(recorded);
        return (new SupabaseMigrationRunner(ProxyFor([.. listed], readable), executor, NullLogger<SupabaseMigrationRunner>.Instance), executor);
    }

    private static MigrationRunRequest Request() => new(Guid.CreateVersion7(), "abc1234def", "Host=unused", null, null, null);

    [Fact]
    public async Task FR153_An_unreadable_pending_file_fails_the_run_before_anything_is_applied_and_names_the_file()
    {
        var (runner, executor) = Runner(
            ["supabase/migrations/0001_a.sql", "supabase/migrations/0002_big.sql"],
            new(StringComparer.Ordinal) { ["supabase/migrations/0001_a.sql"] = "select 1;" });

        var result = await runner.RunAsync(Request(), Ct);

        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationFailed), (result.Status, result.ErrorCode));
        Assert.Contains("supabase/migrations/0002_big.sql", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(result.Applied);
        Assert.Empty(result.Diverged);
        Assert.Empty(executor.Applied);
        Assert.DoesNotContain("-- unreadable", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR153_An_unreadable_recorded_file_fails_clearly_and_is_not_diverged()
    {
        var (runner, executor) = Runner(["supabase/migrations/0001_a.sql"], new(StringComparer.Ordinal), new RecordedMigration("0001", "a", ["select 1"]));

        var result = await runner.RunAsync(Request(), Ct);

        Assert.Equal(ErrorCodes.MigrationFailed, result.ErrorCode);
        Assert.Empty(result.Diverged);
        Assert.Contains("is recorded in the database but couldn't be read", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    [Fact]
    public async Task FR153_Readable_pending_files_are_applied_in_order()
    {
        var (runner, executor) = Runner(
            ["supabase/migrations/0002_b.sql", "supabase/migrations/0001_a.sql"],
            new(StringComparer.Ordinal) { ["supabase/migrations/0001_a.sql"] = "select 1;", ["supabase/migrations/0002_b.sql"] = "select 2;" });

        var result = await runner.RunAsync(Request(), Ct);

        Assert.Equal(MigrationRunStatus.Succeeded, result.Status);
        Assert.Equal(["0001", "0002"], executor.Applied);
    }

    [Fact]
    public async Task FR153_A_failed_file_stores_the_SQLSTATE_and_first_line_without_the_detail_or_row_data()
    {
        var failure = new PostgresException(
            "duplicate key value violates unique constraint \"orders_pkey\"\nsecond line with 'secret-row-value'",
            "ERROR",
            "ERROR",
            "23505",
            detail: "Key (email)=(someone@example.com) already exists.",
            hint: "row hint 'secret-row-value'");
        var runner = new SupabaseMigrationRunner(
            ProxyFor(["supabase/migrations/0001_a.sql"], new(StringComparer.Ordinal) { ["supabase/migrations/0001_a.sql"] = "insert into orders values (1);" }),
            new FakeExecutor([]) { Failure = failure },
            NullLogger<SupabaseMigrationRunner>.Instance);

        var result = await runner.RunAsync(Request(), Ct);

        Assert.Equal(ErrorCodes.MigrationFailed, result.ErrorCode);
        Assert.Equal("0001_a.sql failed (SQLSTATE 23505): duplicate key value violates unique constraint \"orders_pkey\"", result.ErrorMessage);
        Assert.DoesNotContain("someone@example.com", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-row-value", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FR152_The_connection_and_the_run_request_print_no_URL_password_or_key()
    {
        var connection = new ManagedDatabaseConnection
        {
            Url = "postgresql://u:pw-secret@host:5432/db",
            ExternalUrl = "postgresql://u:pw-secret@ext:5432/db",
            PublishableKey = "sb_publishable_x",
            SecretKey = "sb_secret_x",
            SupabaseRef = "abcd",
            Name = "orders-db",
        };
        var request = new MigrationRunRequest(Guid.CreateVersion7(), "abc", "Host=h;Password=pw-secret", null, null, null);

        foreach (var text in new[] { connection.ToString(), request.ToString() })
        {
            Assert.DoesNotContain("pw-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sb_secret_", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sb_publishable_", text, StringComparison.Ordinal);
        }

        Assert.Contains("orders-db", connection.ToString(), StringComparison.Ordinal);
    }

    private static IWorkingCopies ProxyFor(IReadOnlyList<string> listed, Dictionary<string, string> readable)
    {
        var copies = DispatchProxy.Create<IWorkingCopies, ListedWorkingCopy>();
        ((ListedWorkingCopy)(object)copies).Paths = listed;
        ((ListedWorkingCopy)(object)copies).Texts = readable;
        return copies;
    }

    private sealed class FakeExecutor(IReadOnlyList<RecordedMigration> recorded) : IMigrationSqlExecutor, IMigrationSession
    {
        public List<string> Applied { get; } = [];

        public NpgsqlException? Failure { get; init; }

        public Task<IMigrationSession> OpenAsync(string connectionString, CancellationToken cancellationToken) => Task.FromResult<IMigrationSession>(this);

        public Task<IReadOnlyList<RecordedMigration>> EnsureHistoryAsync(CancellationToken cancellationToken) => Task.FromResult(recorded);

        public Task ApplyAsync(MigrationFile file, IReadOnlyList<string> statements, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Applied.Add(file.Version);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A working copy at a commit that lists <see cref="Paths"/> but can read only <see cref="Texts"/> (the rest are binary or too large).</summary>
public class ListedWorkingCopy : DispatchProxy
{
    public IReadOnlyList<string> Paths { get; set; } = [];

    public Dictionary<string, string> Texts { get; set; } = new(StringComparer.Ordinal);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IWorkingCopies.ListPathsAtCommitAsync) => Task.FromResult<IReadOnlyList<string>?>(Paths),
        nameof(IWorkingCopies.ReadTextAtCommitAsync) => Task.FromResult<IReadOnlyDictionary<string, string>>(
            ((IReadOnlyList<string>)args![2]!).Where(Texts.ContainsKey).ToDictionary(p => p, p => Texts[p], StringComparer.Ordinal)),
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
