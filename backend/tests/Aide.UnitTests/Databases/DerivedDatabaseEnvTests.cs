using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Databases;

/// <summary>FR-145, FR-152–FR-154, §4.22: the database rows of the derived environment variables.</summary>
public class DerivedDatabaseEnvTests
{
    private const string Session = "postgresql://postgres.abcd:pw@aws-0-eu.pooler.supabase.com:5432/postgres";
    private const string Transaction = "postgresql://postgres.abcd:pw@aws-0-eu.pooler.supabase.com:6543/postgres";

    private static readonly DeploymentEnvironment[] Both = [DeploymentEnvironment.Preview, DeploymentEnvironment.Production];
    private static readonly DeploymentComponent[] FrontendOnly = [DeploymentComponent.Frontend];
    private static readonly DeploymentComponent[] BackendOnly = [DeploymentComponent.Backend];

    private static DerivedEnvDatabase Supabase(DeploymentEnvironment environment, string reference = "abcd") => new(
        environment,
        ManagedDatabaseProvider.Supabase,
        Session,
        Transaction,
        $"https://{reference}.supabase.co",
        $"sb_publishable_{environment.ToString().ToLowerInvariant()}",
        $"sb_secret_{environment.ToString().ToLowerInvariant()}");

    private static DerivedEnvVar Var(IReadOnlyList<DerivedEnvVar> vars, string key) => Assert.Single(vars, v => v.Key == key);

    [Fact]
    public void FR145_Vue_with_Supabase_BaaS_derives_public_VITE_SUPABASE_URL_and_publishable_key_for_both_environments_and_no_secret()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-vue", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "VITE_", [], [Supabase(DeploymentEnvironment.Production), Supabase(DeploymentEnvironment.Preview, "efgh")]));

        Assert.Equal(["VITE_SUPABASE_PUBLISHABLE_KEY", "VITE_SUPABASE_URL"], vars.Select(v => v.Key).ToArray());
        var url = Var(vars, "VITE_SUPABASE_URL");
        Assert.Equal((true, false), (url.Public, url.Sensitive));
        Assert.Equal(FrontendOnly, url.AppliesTo);
        Assert.Equal(Both, url.Values.Keys.Order().ToArray());
        Assert.Equal(("https://abcd.supabase.co", "https://efgh.supabase.co"), (url.Values[DeploymentEnvironment.Production], url.Values[DeploymentEnvironment.Preview]));
        Assert.Equal("sb_publishable_production", Var(vars, "VITE_SUPABASE_PUBLISHABLE_KEY").Values[DeploymentEnvironment.Production]);
        Assert.DoesNotContain(vars, v => v.Values.Values.Any(value => value.StartsWith("sb_secret_", StringComparison.Ordinal)));
        Assert.DoesNotContain(vars, v => v.Key is "DATABASE_URL" or "AIDE_RUN_MIGRATIONS");
    }

    [Fact]
    public void FR145_A_BaaS_preview_without_its_own_project_uses_the_production_project()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-vue", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var url = Var(DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [], [Supabase(DeploymentEnvironment.Production)])), "VITE_SUPABASE_URL");

        Assert.Equal("https://abcd.supabase.co", url.Values[DeploymentEnvironment.Preview]);
    }

    [Fact]
    public void FR153_Nextjs_full_stack_BaaS_with_the_pack_rule_gets_server_only_SUPABASE_URL_and_a_sensitive_secret_key()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-nextjs", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "NEXT_PUBLIC_", [], [Supabase(DeploymentEnvironment.Production)], new DerivedEnvDatabaseFacts(false, false, SupabaseServerKeys: true)));

        Assert.Equal(
            ["NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY", "NEXT_PUBLIC_SUPABASE_URL", "SUPABASE_SECRET_KEY", "SUPABASE_URL"],
            vars.Select(v => v.Key).ToArray());
        var server = Var(vars, "SUPABASE_URL");
        var secret = Var(vars, "SUPABASE_SECRET_KEY");
        Assert.Equal((false, false), (server.Public, server.Sensitive));
        Assert.Equal((false, true, "sb_secret_production"), (secret.Public, secret.Sensitive, secret.Values[DeploymentEnvironment.Production]));
    }

    [Fact]
    public void FR153_A_static_Vue_frontend_never_gets_the_server_Supabase_keys_even_when_the_secret_is_declared()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel("fe-vue", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "VITE_", [], [Supabase(DeploymentEnvironment.Production)], new DerivedEnvDatabaseFacts(false, false, SupabaseServerKeys: true)));

        Assert.Equal(["VITE_SUPABASE_PUBLISHABLE_KEY", "VITE_SUPABASE_URL"], vars.Select(v => v.Key).ToArray());
        Assert.DoesNotContain(vars, v => v.Key is "SUPABASE_SECRET_KEY" or "SUPABASE_URL");
        Assert.DoesNotContain(vars, v => v.Values.Values.Any(value => value.StartsWith("sb_secret_", StringComparison.Ordinal)));
    }

    [Fact]
    public void FR152_A_derived_database_prints_no_URL_or_key()
    {
        var text = Supabase(DeploymentEnvironment.Production).ToString();

        Assert.DoesNotContain("pw@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sb_secret_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sb_publishable_", text, StringComparison.Ordinal);
        Assert.Contains("Production", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR154_Nextjs_Prisma_on_Vercel_with_Supabase_host_mode_uses_the_transaction_pooler_with_pgbouncer_and_migrates_production_only()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-nextjs", "none", "data-prisma", "vcs-github", frontendHost: "deploy-vercel", databaseHost: "supabase"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "NEXT_PUBLIC_", [], [Supabase(DeploymentEnvironment.Production)], new DerivedEnvDatabaseFacts(Serverless: true, NeedsDirectUrl: true, false)));

        Assert.Equal(["AIDE_RUN_MIGRATIONS", "DATABASE_URL", "DIRECT_URL"], vars.Select(v => v.Key).ToArray());
        var database = Var(vars, "DATABASE_URL");
        Assert.True(database.Sensitive);
        Assert.Equal(FrontendOnly, database.AppliesTo);
        Assert.Equal(Transaction + "?pgbouncer=true", database.Values[DeploymentEnvironment.Production]);
        Assert.Equal(Transaction + "?pgbouncer=true", database.Values[DeploymentEnvironment.Preview]);
        Assert.Contains("prepared statements off", database.DerivedFrom, StringComparison.Ordinal);
        Assert.Equal(Session, Var(vars, "DIRECT_URL").Values[DeploymentEnvironment.Production]);
        var run = Var(vars, "AIDE_RUN_MIGRATIONS");
        Assert.Equal(("true", "false"), (run.Values[DeploymentEnvironment.Production], run.Values[DeploymentEnvironment.Preview]));
        Assert.DoesNotContain(vars, v => v.Key.Contains("SUPABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void FR154_A_preview_database_turns_AIDE_RUN_MIGRATIONS_on_for_preview()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-nextjs", "none", "data-prisma", "vcs-github", frontendHost: "deploy-vercel", databaseHost: "supabase"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "NEXT_PUBLIC_", [], [Supabase(DeploymentEnvironment.Production), Supabase(DeploymentEnvironment.Preview)], new DerivedEnvDatabaseFacts(true, true, false)));

        Assert.Equal("true", Var(vars, "AIDE_RUN_MIGRATIONS").Values[DeploymentEnvironment.Preview]);
    }

    [Fact]
    public void FR154_A_long_running_backend_on_Supabase_uses_the_session_pooler()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "supabase"));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [], [Supabase(DeploymentEnvironment.Production)]));

        var database = Var(vars, "DATABASE_URL");
        Assert.Equal(Session, database.Values[DeploymentEnvironment.Production]);
        Assert.Equal(BackendOnly, database.AppliesTo);
        Assert.DoesNotContain(vars, v => v.Key is "DIRECT_URL" or "AIDE_RUN_MIGRATIONS");
    }

    [Fact]
    public void FR152_Render_and_Railway_backends_get_the_internal_URL_or_the_Railway_reference_per_environment()
    {
        var render = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "render-postgres"));
        var railway = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-railway", databaseHost: "railway-postgres"));

        var renderUrl = Var(DerivedEnvRules.Derive(new DerivedEnvInput(render, "VITE_", [], [
            new(DeploymentEnvironment.Production, ManagedDatabaseProvider.Render, "postgresql://u:p@dpg-p/todo"),
            new(DeploymentEnvironment.Preview, ManagedDatabaseProvider.Render, "postgresql://u:p@dpg-v/todo")])), "DATABASE_URL");
        var railwayUrl = Var(DerivedEnvRules.Derive(new DerivedEnvInput(railway, "VITE_", [], [
            new(DeploymentEnvironment.Production, ManagedDatabaseProvider.Railway, "${{Postgres.DATABASE_URL}}")])), "DATABASE_URL");

        Assert.Equal(("postgresql://u:p@dpg-p/todo", "postgresql://u:p@dpg-v/todo"), (renderUrl.Values[DeploymentEnvironment.Production], renderUrl.Values[DeploymentEnvironment.Preview]));
        Assert.Equal("${{Postgres.DATABASE_URL}}", railwayUrl.Values[DeploymentEnvironment.Production]);
    }

    [Fact]
    public void FR152_No_database_means_no_database_rows()
    {
        var composition = StackTestCatalog.Valid(StackTestCatalog.Sel(
            "fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "render-postgres"));

        Assert.DoesNotContain(DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [], [])), v => v.Key == "DATABASE_URL");
    }
}
