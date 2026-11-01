using Aide.Domain.Databases;

namespace Aide.UnitTests.Databases;

/// <summary>FR-153, §4.21, D-V4-12: planning a Supabase BaaS migration run.</summary>
public class MigrationPlanningTests
{
    private const string Orders = "create table orders (id bigint primary key);\nalter table orders enable row level security;\n";

    private static (string, string?) File(string name, string sql) => ($"supabase/migrations/{name}", sql);

    private static (string, string?) Unreadable(string name) => ($"supabase/migrations/{name}", null);

    [Fact]
    public void FR153_Pending_files_are_the_unrecorded_versions_in_version_order()
    {
        var plan = MigrationPlanning.Plan(
            [File("20260102000000_b.sql", "select 2;"), File("0003_orders.sql", Orders), File("20260101000000_a.sql", "select 1;")],
            [new RecordedMigration("0003", "orders", [Orders])]);

        Assert.False(plan.HasDiverged);
        Assert.Equal(["20260101000000", "20260102000000"], plan.Pending.Select(f => f.Version).ToArray());
        Assert.Equal(("a", "supabase/migrations/20260101000000_a.sql"), (plan.Pending[0].Name, plan.Pending[0].Path));
    }

    [Fact]
    public void FR153_Version_order_is_numeric_so_a_short_version_sorts_before_a_timestamp()
    {
        var plan = MigrationPlanning.Plan([File("20260101000000_later.sql", ""), File("0010_ten.sql", ""), File("0002_two.sql", "")], []);

        Assert.Equal(["0002", "0010", "20260101000000"], plan.Pending.Select(f => f.Version).ToArray());
    }

    [Fact]
    public void FR153_A_recorded_version_whose_file_changed_is_diverged_and_nothing_is_reapplied()
    {
        var plan = MigrationPlanning.Plan(
            [File("0003_orders.sql", Orders + "alter table orders add column note text;"), File("0004_items.sql", "select 1;")],
            [new RecordedMigration("0003", "orders", [Orders])]);

        var diverged = Assert.Single(plan.Diverged);
        Assert.Equal(("0003", "changed"), (diverged.Version, diverged.Reason));
        Assert.Contains("0003_orders changed after it was applied", MigrationPlanning.Describe(plan.Diverged), StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Pending, f => f.Version == "0003");
    }

    [Fact]
    public void FR153_A_recorded_version_whose_file_disappeared_is_diverged()
    {
        var plan = MigrationPlanning.Plan([File("0004_items.sql", "select 1;")], [new RecordedMigration("0003", "orders", [Orders])]);

        Assert.Equal(("0003", "missing"), (plan.Diverged[0].Version, plan.Diverged[0].Reason));
    }

    [Fact]
    public void FR153_The_CLIs_split_statements_match_the_same_file_and_whitespace_doesnt_count()
    {
        var cli = new RecordedMigration("0003", "orders", ["create table orders (id bigint primary key)", "alter table orders enable row level security"]);
        var reformatted = "create table orders (id bigint primary key);\r\n\r\n  alter table orders enable row level security;";

        Assert.False(MigrationPlanning.Plan([File("0003_orders.sql", reformatted)], [cli]).HasDiverged);
    }

    [Fact]
    public void FR153_A_recorded_row_without_statements_is_only_checked_for_presence()
    {
        var plan = MigrationPlanning.Plan([File("0003_orders.sql", "anything")], [new RecordedMigration("0003", null, [])]);

        Assert.False(plan.HasDiverged);
        Assert.Empty(plan.Pending);
    }

    [Fact]
    public void FR153_Two_files_with_one_version_diverge_and_other_files_are_ignored()
    {
        var plan = MigrationPlanning.Plan(
            [File("0005_a.sql", "select 1;"), File("0005_b.sql", "select 2;"), File("README.md", "x"), ("supabase/migrations/nested/0006_c.sql", "x"), ("supabase/seed.sql", "x")],
            []);

        Assert.Equal(("0005", "duplicate"), (plan.Diverged[0].Version, plan.Diverged[0].Reason));
        Assert.Equal(3, plan.Ignored.Count);
    }

    [Fact]
    public void FR153_An_unreadable_pending_file_is_never_pending_and_is_reported_by_path()
    {
        var plan = MigrationPlanning.Plan([File("0004_a.sql", "select 1;"), Unreadable("0005_big.sql"), File("0006_c.sql", "select 3;")], []);

        Assert.False(plan.HasDiverged);
        Assert.True(plan.HasUnreadable);
        var unreadable = Assert.Single(plan.Unreadable);
        Assert.Equal(("supabase/migrations/0005_big.sql", "0005", false), (unreadable.Path, unreadable.Version, unreadable.Recorded));
        Assert.DoesNotContain(plan.Pending, f => f.Version == "0005");
        var message = MigrationPlanning.DescribeUnreadable(plan.Unreadable);
        Assert.Contains("supabase/migrations/0005_big.sql couldn't be read", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was applied", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR153_An_unreadable_recorded_file_is_neither_diverged_nor_applied()
    {
        var plan = MigrationPlanning.Plan([Unreadable("0003_orders.sql")], [new RecordedMigration("0003", "orders", [Orders])]);

        Assert.False(plan.HasDiverged);
        Assert.Empty(plan.Pending);
        var unreadable = Assert.Single(plan.Unreadable);
        Assert.True(unreadable.Recorded);
        Assert.Contains("is recorded in the database but couldn't be read", MigrationPlanning.DescribeUnreadable(plan.Unreadable), StringComparison.Ordinal);
    }

    [Fact]
    public void FR153_At_most_ten_unreadable_files_are_named()
    {
        var plan = MigrationPlanning.Plan([.. Enumerable.Range(1, 12).Select(i => Unreadable($"{i:0000}_f.sql"))], []);

        var message = MigrationPlanning.DescribeUnreadable(plan.Unreadable);

        Assert.Contains("0010_f.sql", message, StringComparison.Ordinal);
        Assert.DoesNotContain("0011_f.sql", message, StringComparison.Ordinal);
        Assert.Contains("and 2 more", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR153_AIDE_records_the_whole_file_as_its_statement()
    {
        var file = MigrationPlanning.Parse("supabase/migrations/0003_orders.sql", Orders)!;

        Assert.Equal(("0003", "orders"), (file.Version, file.Name));
        Assert.Equal([Orders], MigrationPlanning.Statements(file));
        Assert.Null(MigrationPlanning.Parse("supabase/migrations/orders.sql", Orders));
    }
}
