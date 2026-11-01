using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Databases;

/// <summary>FR-152, FR-153, §4.21: the managed database rules (pure).</summary>
public class ManagedDatabaseRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ Supabase create body

    [Fact]
    public void FR152_Supabase_create_sends_region_selection_specific_and_never_the_deprecated_fields()
    {
        var json = JsonSerializer.Serialize(ManagedDatabaseRules.SupabaseCreateBody("todo", "acme-org", "pw", "eu-west-2"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(["db_pass", "name", "organization_slug", "region_selection"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("specific", "eu-west-2"), (root.GetProperty("region_selection").GetProperty("type").GetString(), root.GetProperty("region_selection").GetProperty("code").GetString()));
        Assert.False(root.TryGetProperty("plan", out _));
        Assert.False(root.TryGetProperty("region", out _));
        Assert.False(root.TryGetProperty("organization_id", out _));
    }

    [Theory]
    [InlineData("americas")]
    [InlineData("emea")]
    [InlineData("apac")]
    public void FR152_Supabase_create_sends_a_smart_group_for_americas_emea_and_apac(string group)
    {
        var selection = (IReadOnlyDictionary<string, string>)ManagedDatabaseRules.SupabaseCreateBody("todo", "acme", "pw", group)["region_selection"];

        Assert.Equal(("smartGroup", group), (selection["type"], selection["code"]));
    }

    // ------------------------------------------------------------------ states

    [Theory]
    [InlineData("ACTIVE_HEALTHY", ManagedDatabaseStatus.Available, true)]
    [InlineData("COMING_UP", ManagedDatabaseStatus.Provisioning, false)]
    [InlineData("UNKNOWN", ManagedDatabaseStatus.Provisioning, false)]
    [InlineData("RESTORING", ManagedDatabaseStatus.Provisioning, false)]
    [InlineData("INACTIVE", ManagedDatabaseStatus.Paused, true)]
    [InlineData("INIT_FAILED", ManagedDatabaseStatus.Error, true)]
    [InlineData("REMOVED", ManagedDatabaseStatus.Removed, true)]
    [InlineData(null, ManagedDatabaseStatus.Provisioning, false)]
    public void FR152_Supabase_project_status_maps_to_AIDE_states(string? status, ManagedDatabaseStatus expected, bool terminal) =>
        Assert.Equal(new DatabaseStateMapping(expected, terminal), ManagedDatabaseRules.SupabaseState(status));

    [Fact]
    public void FR152_Supabase_is_healthy_only_when_db_pooler_auth_and_rest_all_are()
    {
        var all = new List<(string, string?, bool?)> { ("db", "ACTIVE_HEALTHY", null), ("pooler", null, true), ("auth", "ACTIVE_HEALTHY", true), ("rest", "ACTIVE_HEALTHY", null) };
        Assert.True(ManagedDatabaseRules.SupabaseHealthy(all));
        Assert.False(ManagedDatabaseRules.SupabaseHealthy([.. all.Take(3)]));
        Assert.False(ManagedDatabaseRules.SupabaseHealthy([.. all.Take(3), ("rest", "COMING_UP", false)]));
    }

    [Fact]
    public void FR152_Render_Postgres_is_expiring_within_7_days_of_expiresAt()
    {
        Assert.Equal(new DatabaseStateMapping(ManagedDatabaseStatus.Available, true), ManagedDatabaseRules.RenderState("available", Now.AddDays(30), Now));
        Assert.Equal(new DatabaseStateMapping(ManagedDatabaseStatus.Expiring, true), ManagedDatabaseRules.RenderState("available", Now.AddDays(6), Now));
        Assert.Equal(new DatabaseStateMapping(ManagedDatabaseStatus.Provisioning, false), ManagedDatabaseRules.RenderState("creating", null, Now));
        Assert.Equal(new DatabaseStateMapping(ManagedDatabaseStatus.Paused, true), ManagedDatabaseRules.RenderState("suspended", null, Now));
        Assert.Equal(ManagedDatabaseStatus.Expiring, ManagedDatabaseRules.Effective(ManagedDatabaseStatus.Available, Now.AddDays(7), Now));
        Assert.Equal(ManagedDatabaseStatus.Available, ManagedDatabaseRules.Effective(ManagedDatabaseStatus.Available, null, Now));
    }

    [Fact]
    public void FR152_Provisioning_times_out_after_10_minutes()
    {
        Assert.False(ManagedDatabaseRules.TimedOut(Now, Now.AddMinutes(9).AddSeconds(59)));
        Assert.True(ManagedDatabaseRules.TimedOut(Now, Now.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), ManagedDatabaseRules.PollInterval);
    }

    // ------------------------------------------------------------------ connection URLs and names

    [Fact]
    public void FR153_Pooler_URLs_use_the_pooler_host_with_session_5432_and_transaction_6543_and_encode_the_password()
    {
        Assert.Equal(
            "postgresql://postgres.abcd:p%40ss%2Fw@aws-0-eu-west-2.pooler.supabase.com:5432/postgres",
            ManagedDatabaseRules.PoolerUrl("aws-0-eu-west-2.pooler.supabase.com", "postgres.abcd", "p@ss/w", ManagedDatabaseRules.SupabaseSessionPort));
        Assert.EndsWith(":6543/postgres", ManagedDatabaseRules.PoolerUrl("h", "u", "p", ManagedDatabaseRules.SupabaseTransactionPort), StringComparison.Ordinal);
        Assert.Equal("postgresql://u:p@h:6543/postgres?pgbouncer=true", ManagedDatabaseRules.WithPgbouncer("postgresql://u:p@h:6543/postgres"));
        Assert.Equal("postgresql://u:p@h/db?sslmode=require&pgbouncer=true", ManagedDatabaseRules.WithPgbouncer("postgresql://u:p@h/db?sslmode=require"));
        Assert.Equal("https://abcd.supabase.co", ManagedDatabaseRules.SupabaseProjectUrl("abcd"));
    }

    [Fact]
    public void FR152_Render_preview_database_is_named_dash_preview_and_names_come_from_the_project()
    {
        Assert.Equal("todo-db-preview", ManagedDatabaseRules.RenderPreviewName("todo-db"));
        Assert.Equal("todo-db-preview", ManagedDatabaseRules.RenderPreviewName("todo-db-preview"));
        Assert.Equal("my-todo-app", ManagedDatabaseRules.ResourceName("My Todo App!", DeploymentEnvironment.Production));
        Assert.Equal("my-todo-app-preview", ManagedDatabaseRules.ResourceName("My Todo App!", DeploymentEnvironment.Preview));
        Assert.Equal("aide-db", ManagedDatabaseRules.ResourceName("???", DeploymentEnvironment.Production));
        Assert.Equal("my_todo_app", ManagedDatabaseRules.RenderDatabaseName("my-todo-app"));
    }

    [Fact]
    public void FR152_Railway_apps_reference_the_Postgres_service_DATABASE_URL()
    {
        Assert.Equal("${{Postgres.DATABASE_URL}}", ManagedDatabaseRules.RailwayReference(null));
        Assert.Equal("${{db.DATABASE_URL}}", ManagedDatabaseRules.RailwayReference("db"));
    }

    [Fact]
    public void FR152_The_card_summary_never_shows_a_user_or_password()
    {
        var external = new ManagedDatabaseConnection { Url = "postgresql://admin:s3cret@db.example.com:5433/app" };
        var supabase = new ManagedDatabaseConnection { SupabaseRef = "abcd", PoolerHost = "aws-0-eu.pooler.supabase.com", PoolerUser = "postgres.abcd" };
        var ado = new ManagedDatabaseConnection { Url = "Server=sql.example.com;Database=app;User Id=sa;Password=s3cret" };

        Assert.Equal("db.example.com:5433/app", ManagedDatabaseRules.Summary(ManagedDatabaseProvider.External, external));
        Assert.Equal("aws-0-eu.pooler.supabase.com:5432/postgres (session pooler)", ManagedDatabaseRules.Summary(ManagedDatabaseProvider.Supabase, supabase));
        Assert.Equal("sql.example.com", ManagedDatabaseRules.Summary(ManagedDatabaseProvider.External, ado));
        Assert.Equal("${{Postgres.DATABASE_URL}}", ManagedDatabaseRules.Summary(ManagedDatabaseProvider.Railway, new ManagedDatabaseConnection()));
    }

    // ------------------------------------------------------------------ PUT validation

    [Fact]
    public void FR152_The_provider_must_be_the_compositions_database_host()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-vue", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var wrong = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Render, composition, false, Guid.NewGuid(), DatabaseSetupMode.Create, null, null, null);
        var right = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Supabase, composition, false, Guid.NewGuid(), DatabaseSetupMode.Create, "eu-west-2", null, null);
        var fake = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Fake, composition, true, null, DatabaseSetupMode.Create, null, null, null);

        Assert.Equal(ErrorCodes.StackIncompatible, wrong.Error?.Code);
        Assert.True(right.IsSuccess);
        Assert.True(fake.IsSuccess);
    }

    [Fact]
    public void FR152_External_takes_a_postgres_URI_or_an_ADO_string_and_needs_no_connection()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "external"));

        Assert.True(ManagedDatabaseRules.Validate(ManagedDatabaseProvider.External, composition, false, null, DatabaseSetupMode.External, null, null, "postgresql://u:p@h:5432/db").IsSuccess);
        Assert.True(ManagedDatabaseRules.Validate(ManagedDatabaseProvider.External, composition, false, null, DatabaseSetupMode.External, null, null, "Server=h;Database=d;User Id=u;Password=p").IsSuccess);
        Assert.Equal(ErrorCodes.Validation, ManagedDatabaseRules.Validate(ManagedDatabaseProvider.External, composition, false, null, DatabaseSetupMode.External, null, null, "mysql://h/db").Error?.Code);
        Assert.Equal(ErrorCodes.Validation, ManagedDatabaseRules.Validate(ManagedDatabaseProvider.External, composition, false, null, DatabaseSetupMode.Create, null, null, null).Error?.Code);
    }

    [Fact]
    public void FR152_A_platform_database_needs_a_connection_one_mode_and_a_Supabase_region()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-vue", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var noConnection = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Supabase, composition, false, null, DatabaseSetupMode.Link, null, "abcd", null);
        var noMode = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Supabase, composition, false, Guid.NewGuid(), null, null, null, null);
        var badRegion = ManagedDatabaseRules.Validate(ManagedDatabaseProvider.Supabase, composition, false, Guid.NewGuid(), DatabaseSetupMode.Create, "mars-1", null, null);

        Assert.Contains(noConnection.Error!.Details, d => d.StartsWith("connectionId:", StringComparison.Ordinal));
        Assert.Contains(noMode.Error!.Details, d => d.StartsWith("create:", StringComparison.Ordinal));
        Assert.Contains(badRegion.Error!.Details, d => d.StartsWith("create.region:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Auth URLs

    [Fact]
    public void FR153_Auth_site_url_is_the_frontend_production_URL_and_the_allow_list_adds_previews_and_localhost()
    {
        var vercel = new DerivedEnvTarget(DeploymentComponent.Frontend, DeploymentProvider.Vercel, "todo", "https://todo.vercel.app", "https://todo-git-x.vercel.app");

        var urls = SupabaseAuthRules.AuthUrls(vercel, DeploymentEnvironment.Production, "fe-vue");

        Assert.Equal("https://todo.vercel.app", urls!.Value.SiteUrl);
        Assert.Equal(
            "https://todo.vercel.app/**,https://todo-git-x.vercel.app/**,https://todo-*.vercel.app/**,http://localhost:5173/**",
            urls.Value.AllowList);
        Assert.Null(SupabaseAuthRules.AuthUrls(null, DeploymentEnvironment.Production));
        Assert.Equal(3000, SupabaseAuthRules.DevPort("fe-nextjs"));
    }
}
