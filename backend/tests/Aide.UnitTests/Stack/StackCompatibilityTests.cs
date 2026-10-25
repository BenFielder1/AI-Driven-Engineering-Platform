using Aide.Domain.Common;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-113–FR-116, FR-120, §4.17: the compatibility rules R1–R12 on the shipped catalog, the ten worked examples, the
/// FR-114/FR-115/FR-116 acceptance examples, and an exhaustive check that what <c>/options</c> offers is what
/// <c>/validate</c> accepts.
/// </summary>
public class StackCompatibilityTests
{
    private const string Fe = StackChoices.Frontend;
    private const string Be = StackChoices.Backend;
    private const string Data = StackChoices.Data;
    private const string Vcs = StackChoices.Vcs;
    private const string FeHost = StackChoices.FrontendHost;
    private const string BeHost = StackChoices.BackendHost;
    private const string DbHost = StackChoices.DatabaseHost;
    private const string Local = StackChoices.Local;
    private const string Origin = StackChoices.OriginModel;

    private static readonly string[] Spas = ["fe-react-vite", "fe-vue", "fe-angular", "fe-vanilla"];
    private static readonly string[] Backends = ["be-aspnet", "be-spring-boot", "be-fastapi", "be-express", "be-nestjs"];
    private static readonly string[] PostgresOrms = ["data-efcore-postgres", "data-jpa-flyway", "data-sqlalchemy", "data-prisma", "data-drizzle"];

    // ── R1–R12 ───────────────────────────────────────────────────────────

    [Fact]
    public void FR113_R1_Frontend_and_backend_cant_both_be_None()
    {
        var none = Option(Evaluate(Sel(frontend: "none")), Be, "none");
        Assert.Equal(StackOptionState.Forbidden, none.State);
        Assert.Equal(new StackReason("R1", "Frontend and Backend can't both be None"), Assert.Single(none.Reasons));
        Assert.All(Backends, b => Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel(frontend: "none")), Be, b).State));

        Assert.Contains(Violations(Sel("none", "none", vcs: "vcs-github")), v => v is { Layer: Be, Option: "none", Rule: "R1" });
    }

    [Fact]
    public void FR113_R2_The_Nextjs_mode_is_full_stack_without_a_backend_and_ui_with_one()
    {
        Assert.Null(Evaluate(Sel("fe-nextjs")).NextjsMode);
        Assert.Equal(StackIds.FullStack, Evaluate(Sel("fe-nextjs", "none")).NextjsMode);
        Assert.Equal(StackIds.Ui, Evaluate(Sel("fe-nextjs", "be-fastapi")).NextjsMode);
        Assert.Null(Evaluate(Sel("fe-vue", "none")).NextjsMode);

        Assert.Equal(StackIds.FullStack, Valid(Sel("fe-nextjs", "none", "data-prisma", "vcs-github")).Derived.NextjsMode);
        Assert.Equal(StackIds.Ui, Valid(Sel("fe-nextjs", "be-express", vcs: "vcs-github")).Derived.NextjsMode);
        Assert.Null(Valid(Sel("fe-vue", "be-express", vcs: "vcs-github")).Derived.NextjsMode);
    }

    public static TheoryData<string, string, string, bool> R3Cases()
    {
        var allowed = new Dictionary<string, string[]>
        {
            ["data-efcore-postgres"] = ["be-aspnet"],
            ["data-efcore-sqlserver"] = ["be-aspnet"],
            ["data-jpa-flyway"] = ["be-spring-boot"],
            ["data-sqlalchemy"] = ["be-fastapi"],
            ["data-prisma"] = ["be-express", "be-nestjs"],
            ["data-drizzle"] = ["be-express", "be-nestjs"],
            ["data-supabase"] = ["none"],
        };
        var data = new TheoryData<string, string, string, bool>();
        foreach (var (pack, backends) in allowed)
        {
            foreach (var backend in Backends.Append("none"))
            {
                data.Add("fe-react-vite", backend, pack, backends.Contains(backend));
            }
        }

        // Next.js full-stack takes Prisma and Drizzle; a Next.js UI with a backend follows the backend.
        data.Add("fe-nextjs", "none", "data-prisma", true);
        data.Add("fe-nextjs", "none", "data-drizzle", true);
        data.Add("fe-nextjs", "none", "data-supabase", true);
        data.Add("fe-nextjs", "none", "data-sqlalchemy", false);
        data.Add("fe-nextjs", "be-fastapi", "data-prisma", false);
        data.Add("fe-nextjs", "be-nestjs", "data-drizzle", true);
        data.Add("none", "be-aspnet", "none", true);
        data.Add("fe-react-vite", "none", "none", true);
        return data;
    }

    [Theory]
    [MemberData(nameof(R3Cases))]
    public void FR113_R3_Each_data_pack_works_only_with_its_backends(string frontend, string backend, string data, bool allowed)
    {
        var option = Option(Evaluate(Sel(frontend, backend)), Data, data);

        Assert.Equal(allowed, option.State != StackOptionState.Forbidden);
        if (!allowed)
        {
            Assert.Equal("R3", Assert.Single(option.Reasons).Rule);
            Assert.Contains(Violations(Sel(frontend, backend, data, "vcs-github")), v => v is { Layer: Data, Rule: "R3" } && v.Option == data);
        }
    }

    [Fact]
    public void FR113_R3_Reason_texts_name_what_the_data_pack_needs()
    {
        var data = Evaluate(Sel("fe-vue", "be-aspnet"));
        Assert.Equal("Prisma needs Express, NestJS or Next.js full-stack", Option(data, Data, "data-prisma").Reasons.Single().Text);
        Assert.Equal("Drizzle needs Express, NestJS or Next.js full-stack", Option(data, Data, "data-drizzle").Reasons.Single().Text);
        Assert.Equal("SQLAlchemy needs FastAPI", Option(data, Data, "data-sqlalchemy").Reasons.Single().Text);
        Assert.Equal("JPA + Flyway needs Spring Boot", Option(data, Data, "data-jpa-flyway").Reasons.Single().Text);
        Assert.Equal("Supabase (BaaS) needs Backend None", Option(data, Data, "data-supabase").Reasons.Single().Text);
        Assert.Equal("EF Core + PostgreSQL needs ASP.NET Core", Option(Evaluate(Sel("fe-vue", "be-express")), Data, "data-efcore-postgres").Reasons.Single().Text);
    }

    [Fact]
    public void FR113_R4_EF_Core_SQL_Server_is_a_caveat()
    {
        var option = Option(Evaluate(Sel("fe-react-vite", "be-aspnet")), Data, "data-efcore-sqlserver");

        Assert.Equal(StackOptionState.Caveat, option.State);
        Assert.Equal(new StackReason("R4", "No managed SQL Server on the catalog's platforms: use an External connection string (e.g. Azure SQL) or local only."), Assert.Single(option.Reasons));
    }

    [Fact]
    public void FR113_R5_Every_VCS_option_is_allowed_for_any_earlier_choice()
    {
        foreach (var (fe, be, data) in new[] { ("fe-nextjs", "none", "data-prisma"), ("none", "be-aspnet", "data-efcore-sqlserver"), ("fe-vue", "none", "data-supabase") })
        {
            Assert.All(Evaluate(Sel(fe, be, data)).Choice(Vcs).Options, o => Assert.Equal(StackOptionState.Allowed, o.State));
        }
    }

    [Fact]
    public void FR113_R6_Frontend_host_by_frontend_and_Nextjs_mode()
    {
        foreach (var spa in Spas)
        {
            Assert.All(Evaluate(Sel(spa, "none", "none", "vcs-github")).Choice(FeHost).Options, o => Assert.Equal(StackOptionState.Allowed, o.State));
        }

        var fullStack = Evaluate(Sel("fe-nextjs", "none", "data-prisma", "vcs-github"));
        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-render", "deploy-railway", "none"],
            fullStack.Choice(FeHost).Options.Where(o => o.State == StackOptionState.Allowed).Select(o => o.Option));
        Assert.Equal(new StackReason("R6", "Cloudflare's Next.js adapter: some Node APIs and middleware are limited"), Option(fullStack, FeHost, "deploy-cloudflare").Reasons.Single());
        Assert.Equal(StackOptionState.Caveat, Option(fullStack, FeHost, "deploy-cloudflare").State);
        Assert.Equal(new StackReason("R6", "Next.js full-stack needs a server"), Option(fullStack, FeHost, "deploy-github-pages").Reasons.Single());
        Assert.Equal(StackOptionState.Forbidden, Option(fullStack, FeHost, "deploy-github-pages").State);

        // GitHub Pages: static export in ui mode, or with Supabase BaaS.
        foreach (var selection in new[] { Sel("fe-nextjs", "be-express", "none", "vcs-github"), Sel("fe-nextjs", "none", "data-supabase", "vcs-github") })
        {
            var pages = Option(Evaluate(selection), FeHost, "deploy-github-pages");
            Assert.Equal(StackOptionState.Caveat, pages.State);
            Assert.Equal(new StackReason("R6", "Static export: no server actions, route handlers or SSR"), pages.Reasons.Single());
        }

        // No frontend: only None.
        Assert.Equal(["none"], Selectable(Evaluate(Sel("none", "be-aspnet", "none", "vcs-github")), FeHost));
    }

    [Fact]
    public void FR113_R7_Backend_host_by_backend()
    {
        foreach (var backend in new[] { "be-aspnet", "be-spring-boot" })
        {
            var e = Evaluate(Sel("fe-react-vite", backend, "none", "vcs-github"));
            Assert.Equal(["deploy-render", "deploy-railway", "none"], Selectable(e, BeHost));
        }

        Assert.Equal("Vercel can't run ASP.NET Core", Option(Evaluate(Sel("fe-react-vite", "be-aspnet", "none", "vcs-github")), BeHost, "deploy-vercel").Reasons.Single().Text);
        Assert.Equal("Vercel can't run Spring Boot", Option(Evaluate(Sel("fe-react-vite", "be-spring-boot", "none", "vcs-github")), BeHost, "deploy-vercel").Reasons.Single().Text);

        foreach (var backend in new[] { "be-fastapi", "be-express", "be-nestjs" })
        {
            var e = Evaluate(Sel("fe-react-vite", backend, "none", "vcs-github"));
            Assert.Equal(["deploy-vercel", "deploy-render", "deploy-railway", "none"], Selectable(e, BeHost));
            Assert.Equal(new StackReason("R7", "Serverless functions: no background work, pooled DB connections"), Option(e, BeHost, "deploy-vercel").Reasons.Single());
            Assert.Equal(StackOptionState.Caveat, Option(e, BeHost, "deploy-vercel").State);
            Assert.Equal("Netlify can't host backends: not covered by the deploy pack", Option(e, BeHost, "deploy-netlify").Reasons.Single().Text);
            Assert.Equal(StackOptionState.Forbidden, Option(e, BeHost, "deploy-cloudflare").State);
            Assert.Equal(StackOptionState.Forbidden, Option(e, BeHost, "deploy-github-pages").State);
        }

        Assert.Equal(["none"], Selectable(Evaluate(Sel("fe-nextjs", "none", "data-prisma", "vcs-github")), BeHost));
    }

    [Fact]
    public void FR113_R8_Database_host_by_data_pack_and_platform()
    {
        foreach (var orm in PostgresOrms)
        {
            var backend = orm switch
            {
                "data-efcore-postgres" => "be-aspnet",
                "data-jpa-flyway" => "be-spring-boot",
                "data-sqlalchemy" => "be-fastapi",
                _ => "be-express",
            };
            var onRender = Evaluate(Sel("fe-react-vite", backend, orm, "vcs-github", "deploy-render", "deploy-render"));
            Assert.Equal(StackOptionState.Allowed, Option(onRender, DbHost, "render-postgres").State);
            Assert.Equal(StackOptionState.Allowed, Option(onRender, DbHost, "supabase").State);
            Assert.Equal(StackOptionState.Allowed, Option(onRender, DbHost, "external").State);
            Assert.Equal(StackOptionState.Allowed, Option(onRender, DbHost, "none").State);
            var railway = Option(onRender, DbHost, "railway-postgres");
            Assert.Equal(StackOptionState.Caveat, railway.State);
            Assert.Equal(new StackReason("R8", "Database on a different platform from the backend: higher latency, public connection"), railway.Reasons.Single());
        }

        // Next.js full-stack counts as the app on the same platform.
        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-nextjs", "none", "data-drizzle", "vcs-github", "deploy-railway", "none")), DbHost, "railway-postgres").State);

        Assert.Equal(["external", "none"], Selectable(Evaluate(Sel("fe-react-vite", "be-aspnet", "data-efcore-sqlserver", "vcs-github", "none", "deploy-render")), DbHost));
        Assert.Equal(["supabase"], Selectable(Evaluate(Sel("fe-vue", "none", "data-supabase", "vcs-github", "deploy-vercel", "none")), DbHost));
        Assert.Equal(["none"], Selectable(Evaluate(Sel("fe-vue", "be-express", "none", "vcs-github", "deploy-vercel", "deploy-render")), DbHost));
    }

    [Fact]
    public void FR113_R9_Each_platform_must_build_from_the_repos_Git_host()
    {
        IReadOnlyList<string> Hosts(string vcs, bool selfManaged = false) =>
            Selectable(Evaluate(Sel("fe-react-vite", "none", "none", vcs, selfManaged: selfManaged)), FeHost);

        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-cloudflare", "deploy-github-pages", "deploy-render", "deploy-railway", "none"], Hosts("vcs-github"));
        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-cloudflare", "deploy-render", "none"], Hosts("vcs-gitlab"));
        Assert.Equal(["deploy-netlify", "none"], Hosts("vcs-gitlab", selfManaged: true));
        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-render", "none"], Hosts("vcs-bitbucket"));

        var gitlab = Evaluate(Sel("fe-react-vite", "none", "none", "vcs-gitlab"));
        Assert.Equal(new StackReason("R9", "Railway only builds from GitHub"), Option(gitlab, FeHost, "deploy-railway").Reasons.Single());
        Assert.Equal(new StackReason("R9", "Linked with a deploy key and webhook that AIDE adds to the repo"), Option(gitlab, FeHost, "deploy-netlify").Reasons.Single());
        Assert.Equal(StackOptionState.Caveat, Option(gitlab, FeHost, "deploy-netlify").State);
        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-react-vite", "none", "none", "vcs-github")), FeHost, "deploy-netlify").State);
        Assert.Contains(new StackReason("R9", "Only Netlify builds from self-managed GitLab"),
            Option(Evaluate(Sel("fe-react-vite", "none", "none", "vcs-gitlab", selfManaged: true)), FeHost, "deploy-vercel").Reasons);

        // Backend hosts follow the same rule.
        Assert.Equal(["deploy-vercel", "deploy-render", "none"], Selectable(Evaluate(Sel("fe-react-vite", "be-express", "none", "vcs-bitbucket", "none")), BeHost));
    }

    public static TheoryData<string, string, string, string, string[], string?> R10Cases() => new()
    {
        { "fe-nextjs", "none", "deploy-vercel", "none", ["same-origin"], "same-origin" },
        { "fe-vanilla", "none", "deploy-github-pages", "none", ["same-origin"], "same-origin" },
        { "fe-react-vite", "be-express", "deploy-railway", "deploy-railway", ["same-origin", "cross-origin"], "same-origin" },
        { "fe-react-vite", "be-express", "deploy-netlify", "deploy-render", ["proxy", "cross-origin"], "proxy" },
        { "fe-vue", "be-express", "deploy-cloudflare", "deploy-render", ["proxy", "cross-origin"], "proxy" },
        { "fe-angular", "be-express", "deploy-render", "deploy-render", ["proxy", "cross-origin"], "proxy" },
        { "fe-nextjs", "be-express", "deploy-vercel", "deploy-railway", ["proxy", "cross-origin"], "proxy" },
        { "fe-nextjs", "be-express", "deploy-railway", "deploy-railway", ["proxy", "cross-origin"], "proxy" },
        { "fe-react-vite", "be-express", "deploy-vercel", "deploy-render", ["cross-origin"], "cross-origin" },
        { "fe-react-vite", "be-express", "deploy-github-pages", "deploy-render", ["cross-origin"], "cross-origin" },
        { "fe-react-vite", "be-express", "none", "deploy-render", ["same-origin"], "same-origin" },
        { "none", "be-express", "none", "deploy-render", [], null },
    };

    [Theory]
    [MemberData(nameof(R10Cases))]
    public void FR113_R10_Origin_model_by_frontend_backend_and_hosts(string frontend, string backend, string frontendHost, string backendHost, string[] allowed, string? defaultModel)
    {
        var data = backend == "none" && frontend == "fe-nextjs" ? "data-prisma" : "none";
        var e = Evaluate(Sel(frontend, backend, data, "vcs-github", frontendHost, backendHost, "none", "none"));

        Assert.Equal(allowed, e.Choice(Origin).Options.Where(o => o.State == StackOptionState.Allowed).Select(o => o.Option));
        Assert.Equal(defaultModel, e.Choice(Origin).Default);
        Assert.All(e.Choice(Origin).Options.Where(o => o.State == StackOptionState.Forbidden), o => Assert.Equal("R10", o.Reasons.Single().Rule));

        var composition = Valid(Sel(frontend, backend, data, "vcs-github", frontendHost, backendHost, data == "none" ? null : "none"));
        Assert.Equal(defaultModel, composition.OriginModel);
        Assert.Equal(allowed, composition.Derived.OriginModelOptions);
    }

    [Fact]
    public void FR113_R11_A_database_host_with_nothing_deployed_to_reach_it_is_a_caveat()
    {
        var local = Option(Evaluate(Sel("fe-react-vite", "be-express", "data-prisma", "vcs-github", "deploy-netlify", "none")), DbHost, "external");
        Assert.Equal(StackOptionState.Caveat, local.State);
        Assert.Equal(new StackReason("R11", "Database only used by the local dev loop"), local.Reasons.Single());

        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-react-vite", "be-express", "data-prisma", "vcs-github", "none", "deploy-render")), DbHost, "external").State);
        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", "deploy-vercel", "none")), DbHost, "supabase").State);
        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-vue", "none", "data-supabase", "vcs-github", "deploy-netlify", "none")), DbHost, "supabase").State);
        Assert.Equal(StackOptionState.Caveat, Option(Evaluate(Sel("fe-vue", "none", "data-supabase", "vcs-github", "none", "none")), DbHost, "supabase").State);
    }

    [Fact]
    public void FR113_R12_The_Compose_dev_loop_is_offered_and_the_default_with_a_backend_or_a_database()
    {
        var withBackend = Evaluate(Sel("fe-react-vite", "be-express", "none", "vcs-github", "none", "none", "none"));
        Assert.Equal(StackIds.Compose, withBackend.Choice(Local).Default);
        Assert.Equal(StackOptionState.Allowed, Option(withBackend, Local, "compose").State);

        Assert.Equal(StackIds.Compose, Evaluate(Sel("fe-vue", "none", "data-supabase", "vcs-github", "none", "none", "supabase")).Choice(Local).Default);

        var staticSite = Evaluate(Sel("fe-vanilla", "none", "none", "vcs-github", "deploy-netlify", "none", "none"));
        Assert.Equal(StackIds.None, staticSite.Choice(Local).Default);
        Assert.Equal(new StackReason("R12", "The Compose dev loop needs a backend or a database"), Option(staticSite, Local, "compose").Reasons.Single());

        Assert.Equal(StackIds.Compose, Valid(Sel("fe-react-vite", "be-express", vcs: "vcs-github")).Deploy.Local);
        Assert.Equal(StackIds.None, Valid(Sel("fe-react-vite", "be-express", vcs: "vcs-github", local: "none")).Deploy.Local);
        Assert.Equal(StackIds.None, Valid(Sel("fe-vanilla", "none", vcs: "vcs-github")).Deploy.Local);
    }

    // ── Worked examples (§4.17) ──────────────────────────────────────────

    [Fact]
    public void FR113_Example1_ReactVite_AspNet_EfPostgres_gitlab_com_Netlify_Render_RenderPostgres_is_allowed_with_the_Netlify_caveat_and_proxy()
    {
        var c = Valid(Sel("fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-gitlab", "deploy-netlify", "deploy-render", "render-postgres"));

        Assert.Equal(new StackCaveat(FeHost, "deploy-netlify", "R9", "Linked with a deploy key and webhook that AIDE adds to the repo"), Assert.Single(c.Caveats));
        Assert.Equal("proxy", c.OriginModel);
        Assert.Equal(new StackDeploy("deploy-netlify", "deploy-render", "render-postgres", StackIds.Compose), c.Deploy);
        Assert.Equal(new StackComponent("fe-react-vite", "frontend"), c.Frontend);
        Assert.Equal(new StackComponent("be-aspnet", "backend"), c.Backend);
        Assert.Equal(new StackDataLayer("data-efcore-postgres", null), c.Data);
        Assert.Equal((Catalog.Version, Catalog.ContentHash), (c.CatalogVersion, c.CatalogHash));
    }

    [Fact]
    public void FR113_Example2_With_backend_host_Vercel_is_rejected_by_R7()
    {
        var v = Violations(Sel("fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-gitlab", "deploy-netlify", "deploy-vercel", "render-postgres"));

        Assert.Equal(new StackViolation(BeHost, "deploy-vercel", "R7", "Vercel can't run ASP.NET Core"), Assert.Single(v));
    }

    [Fact]
    public void FR113_Example3_Vue_Supabase_Bitbucket_on_Cloudflare_is_rejected_by_R9_and_Vercel_Netlify_Render_are_offered()
    {
        var v = Violations(Sel("fe-vue", "none", "data-supabase", "vcs-bitbucket", "deploy-cloudflare"));
        Assert.Equal(new StackViolation(FeHost, "deploy-cloudflare", "R9", "Cloudflare can't build Bitbucket repos"), Assert.Single(v));

        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-render", "none"], Selectable(Evaluate(Sel("fe-vue", "none", "data-supabase", "vcs-bitbucket")), FeHost));

        // Supabase BaaS: a left-out database host is Supabase.
        Assert.Equal("supabase", Valid(Sel("fe-vue", "none", "data-supabase", "vcs-bitbucket", "deploy-vercel")).Deploy.DatabaseHost);
    }

    [Fact]
    public void FR113_Example4_Nextjs_full_stack_Prisma_on_GitHub_Pages_is_rejected_by_R6_and_Vercel_with_Supabase_host_is_allowed()
    {
        var v = Violations(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", "deploy-github-pages"));
        Assert.Equal(new StackViolation(FeHost, "deploy-github-pages", "R6", "Next.js full-stack needs a server"), Assert.Single(v));

        var c = Valid(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", "deploy-vercel", databaseHost: "supabase"));
        Assert.Empty(c.Caveats);
        Assert.Equal(("supabase", "same-origin", StackIds.FullStack), (c.Deploy.DatabaseHost, c.OriginModel, c.Derived.NextjsMode));
    }

    [Fact]
    public void FR113_Example5_FastAPI_SQLAlchemy_on_Railway_is_allowed_with_no_origin_model()
    {
        var c = Valid(Sel("none", "be-fastapi", "data-sqlalchemy", "vcs-github", backendHost: "deploy-railway", databaseHost: "railway-postgres"));

        Assert.Null(c.OriginModel);
        Assert.Empty(c.Derived.OriginModelOptions);
        Assert.Empty(c.Caveats);
        Assert.Null(c.Frontend);
        Assert.Null(c.Deploy.FrontendHost);
    }

    [Fact]
    public void FR113_Example6_Angular_Spring_self_managed_GitLab_on_Render_is_rejected_by_R9_and_the_backend_host_offers_only_None()
    {
        var v = Violations(Sel("fe-angular", "be-spring-boot", "data-jpa-flyway", "vcs-gitlab", backendHost: "deploy-render", selfManaged: true));
        Assert.Equal(new StackViolation(BeHost, "deploy-render", "R9", "Only Netlify builds from self-managed GitLab"), Assert.Single(v));

        Assert.Equal(["none"], Selectable(Evaluate(Sel("fe-angular", "be-spring-boot", "data-jpa-flyway", "vcs-gitlab", selfManaged: true)), BeHost));
    }

    [Fact]
    public void FR113_Example7_AspNet_SqlServer_on_Railway_with_External_is_allowed_with_the_R4_caveat()
    {
        var c = Valid(Sel("fe-react-vite", "be-aspnet", "data-efcore-sqlserver", "vcs-github", backendHost: "deploy-railway", databaseHost: "external"));

        var caveat = Assert.Single(c.Caveats);
        Assert.Equal((Data, "data-efcore-sqlserver", "R4"), (caveat.Layer, caveat.Option, caveat.Rule));
        Assert.Equal("external", c.Deploy.DatabaseHost);
    }

    [Fact]
    public void FR113_Example8_Vanilla_on_GitHub_Pages_is_allowed_and_same_origin()
    {
        var c = Valid(Sel("fe-vanilla", "none", "none", "vcs-github", "deploy-github-pages"));

        Assert.Equal("same-origin", c.OriginModel);
        Assert.Null(c.Data);
        Assert.Null(c.Backend);
    }

    [Fact]
    public void FR113_Example9_ReactVite_Nest_Drizzle_Vercel_Render_Supabase_is_allowed_cross_origin_only_with_Supabase_host_mode()
    {
        var selection = Sel("fe-react-vite", "be-nestjs", "data-drizzle", "vcs-github", "deploy-vercel", "deploy-render", "supabase");
        var c = Valid(selection);

        Assert.Equal("cross-origin", c.OriginModel);
        Assert.Equal(["cross-origin"], c.Derived.OriginModelOptions);
        Assert.Equal("supabase", c.Deploy.DatabaseHost);
        Assert.Null(c.Data!.Mode);

        Assert.Equal(new StackViolation(Origin, "proxy", "R10", Catalog.Rules.OriginModel.First(r => r.Default == "cross-origin" && r.Text.Contains("Vercel", StringComparison.Ordinal)).Text),
            Assert.Single(Violations(selection with { OriginModel = "proxy" })));
    }

    [Fact]
    public void FR113_Example10_Nextjs_Express_Prisma_gitlab_com_Vercel_Railway_is_rejected_by_R9()
    {
        var v = Violations(Sel("fe-nextjs", "be-express", "data-prisma", "vcs-gitlab", "deploy-vercel", "deploy-railway"));

        Assert.Equal(new StackViolation(BeHost, "deploy-railway", "R9", "Railway only builds from GitHub"), Assert.Single(v));
    }

    // ── FR-114, FR-115, FR-116, FR-120 acceptance ────────────────────────

    [Fact]
    public void FR114_With_React_Vite_every_backend_is_allowed()
    {
        Assert.All(Evaluate(Sel("fe-react-vite")).Choice(Be).Options, o => Assert.Equal(StackOptionState.Allowed, o.State));
    }

    [Fact]
    public void FR114_With_AspNet_only_EF_Core_Postgres_EF_Core_SqlServer_as_a_caveat_and_None_are_selectable()
    {
        var e = Evaluate(Sel("fe-react-vite", "be-aspnet"));

        Assert.Equal(["data-efcore-postgres", "data-efcore-sqlserver", "none"], Selectable(e, Data));
        Assert.Equal(StackOptionState.Caveat, Option(e, Data, "data-efcore-sqlserver").State);
        Assert.StartsWith("No managed SQL Server on the catalog's platforms", Option(e, Data, "data-efcore-sqlserver").Reasons.Single().Text, StringComparison.Ordinal);
        Assert.Equal(StackOptionState.Forbidden, Option(e, Data, "data-prisma").State);
        Assert.Equal("Prisma needs Express, NestJS or Next.js full-stack", Option(e, Data, "data-prisma").Reasons.Single().Text);
    }

    [Fact]
    public void FR114_Changing_the_backend_from_FastAPI_to_AspNet_invalidates_SQLAlchemy_with_its_reason()
    {
        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy")), Data, "data-sqlalchemy").State);

        var e = Evaluate(Sel("fe-react-vite", "be-aspnet", "data-sqlalchemy", "vcs-github"));
        var data = e.Choice(Data);
        Assert.Equal("data-sqlalchemy", data.Selected);
        var sqlalchemy = Option(e, Data, "data-sqlalchemy");
        Assert.Equal(StackOptionState.Invalidated, sqlalchemy.State);
        Assert.Equal(new StackReason("R3", "SQLAlchemy needs FastAPI"), sqlalchemy.Reasons.Single());

        // Later choices are evaluated as if the data layer were cleared: no database host decided yet.
        Assert.All(e.Choice(DbHost).Options, o => Assert.NotEqual(StackOptionState.Invalidated, o.State));
    }

    [Fact]
    public void FR115_ReactVite_on_Netlify_and_AspNet_on_Render_offers_Render_Postgres_Supabase_External_with_Railway_as_a_caveat_and_proxy()
    {
        var e = Evaluate(Sel("fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", "deploy-netlify", "deploy-render"));

        Assert.Equal(StackOptionState.Allowed, Option(e, DbHost, "render-postgres").State);
        Assert.Equal(StackOptionState.Allowed, Option(e, DbHost, "supabase").State);
        Assert.Equal(StackOptionState.Allowed, Option(e, DbHost, "external").State);
        Assert.Equal(StackOptionState.Caveat, Option(e, DbHost, "railway-postgres").State);
        Assert.Equal("Database on a different platform from the backend: higher latency, public connection", Option(e, DbHost, "railway-postgres").Reasons.Single().Text);

        var origin = Evaluate(Sel("fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", "deploy-netlify", "deploy-render", "render-postgres", "compose"));
        Assert.Equal("proxy", origin.Choice(Origin).Default);
    }

    [Fact]
    public void FR115_The_backend_host_defaults_to_the_frontend_host_when_that_platform_can_run_the_backend()
    {
        Assert.Equal("deploy-render", Evaluate(Sel("fe-react-vite", "be-aspnet", "none", "vcs-github", "deploy-render")).Choice(BeHost).Default);
        Assert.Null(Evaluate(Sel("fe-react-vite", "be-aspnet", "none", "vcs-github", "deploy-netlify")).Choice(BeHost).Default);
        Assert.Equal("deploy-vercel", Evaluate(Sel("fe-nextjs", "be-fastapi", "none", "vcs-github", "deploy-vercel")).Choice(BeHost).Default);
    }

    [Fact]
    public void FR116_Vue_AspNet_Prisma_is_STACK_INCOMPATIBLE_with_rule_R3()
    {
        var v = Violations(Sel("fe-vue", "be-aspnet", "data-prisma", "vcs-github"));

        Assert.Equal(new StackViolation(Data, "data-prisma", "R3", "Prisma needs Express, NestJS or Next.js full-stack"), Assert.Single(v));
        var error = StackCompatibility.IncompatibleError(v);
        Assert.Equal(ErrorCodes.StackIncompatible, error.Code);
        Assert.Equal(["R3: Prisma needs Express, NestJS or Next.js full-stack"], error.Details);
        Assert.Equal(400, ErrorCodes.StatusCodeFor(ErrorCodes.StackIncompatible));
    }

    [Fact]
    public void FR120_Without_a_backend_and_with_Supabase_BaaS_the_specialists_exclude_the_backend_engineer()
    {
        var c = Valid(Sel("fe-react-vite", "none", "data-supabase", "vcs-github", "deploy-vercel"));

        Assert.Equal(
            [StackAgents.Scaffolder, StackAgents.DataEngineer, StackAgents.FrontendEngineer, StackAgents.TestEngineer, StackAgents.CiEngineer, StackAgents.DeployEngineer, StackAgents.DocsWriter],
            c.Derived.Agents);
        Assert.Equal(new StackDataLayer("data-supabase", "baas"), c.Data);
    }

    [Fact]
    public void FR120_The_deploy_engineer_needs_a_host_or_the_local_dev_loop()
    {
        var c = Valid(Sel("fe-vanilla", "none", "none", "vcs-github"));

        Assert.Equal([StackAgents.Scaffolder, StackAgents.FrontendEngineer, StackAgents.TestEngineer, StackAgents.CiEngineer, StackAgents.DocsWriter], c.Derived.Agents);
        Assert.Contains(StackAgents.DeployEngineer, Valid(Sel("none", "be-express", vcs: "vcs-github")).Derived.Agents);
    }

    // ── Input validation ─────────────────────────────────────────────────

    [Fact]
    public void FR116_Unknown_ids_malformed_paths_and_a_missing_vcs_are_VALIDATION_errors()
    {
        var unknown = StackCompatibility.Evaluate(Sel("fe-svelte"), Catalog);
        Assert.Equal(ErrorCodes.Validation, unknown.Error!.Code);
        Assert.StartsWith("frontend.pack: 'fe-svelte' is not an option of Frontend", unknown.Error.Details.Single(), StringComparison.Ordinal);

        var noVcs = StackCompatibility.Validate(Sel("fe-vue", "be-express"), Catalog);
        Assert.Equal("vcs.pack", noVcs.InputErrors.Single().Field);

        var escape = StackCompatibility.Validate(Sel("fe-vue", "be-express", vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-vue", "../web") }, Catalog);
        Assert.Equal("frontend.path", escape.InputErrors.Single().Field);

        var same = StackCompatibility.Validate(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-vue", "app"), Backend = new StackComponentPick("be-express", "./app/") }, Catalog);
        Assert.Equal("backend.path", same.InputErrors.Single().Field);

        var mode = StackCompatibility.Validate(Sel("fe-vue", "none", vcs: "vcs-github") with { Data = new StackDataPick("data-supabase", "host") }, Catalog);
        Assert.Equal("data.mode", mode.InputErrors.Single().Field);

        var selfManaged = StackCompatibility.Validate(Sel("fe-vue", "none", vcs: "vcs-github", selfManaged: true), Catalog);
        Assert.Equal("vcs.selfManaged", selfManaged.InputErrors.Single().Field);
    }

    [Fact]
    public void FR119_Layer_paths_are_normalised_and_default_from_the_pack()
    {
        Assert.Equal("apps/web", StackCompatibility.NormalisePath(" ./apps\\web/ ", out _));
        Assert.Equal(".", StackCompatibility.NormalisePath("./", out _));
        Assert.Equal(".", StackCompatibility.NormalisePath("", out _));
        Assert.Null(StackCompatibility.NormalisePath("/srv/app", out var absolute));
        Assert.NotNull(absolute);
        Assert.Null(StackCompatibility.NormalisePath("api/../../x", out _));
        Assert.Null(StackCompatibility.NormalisePath("C:/repo", out _));

        var c = Valid(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-nextjs", "."), Backend = new StackComponentPick("none") });
        Assert.Equal(new StackComponent("fe-nextjs", "."), c.Frontend);
        Assert.Equal(new StackComponent("be-fastapi", "backend"), Valid(Sel("none", "be-fastapi", vcs: "vcs-github")).Backend);
    }

    // ── /options ⇔ /validate ─────────────────────────────────────────────

    [Fact]
    public void FR116_Every_composition_the_options_allow_validates_and_every_forbidden_option_is_rejected_with_its_rule()
    {
        var leaves = 0;
        var rejected = 0;
        var vcsVariants = new (string Pack, bool SelfManaged)[] { ("vcs-github", false), ("vcs-gitlab", false), ("vcs-gitlab", true), ("vcs-bitbucket", false) };

        void Walk(int depth, StackSelection selection)
        {
            var evaluation = Evaluate(selection);
            Assert.DoesNotContain(evaluation.Choices.SelectMany(c => c.Options), o => o.State == StackOptionState.Invalidated);
            if (depth == StackChoices.Order.Count)
            {
                var composition = Valid(selection);
                Assert.Equal(selection.OriginModel, composition.OriginModel);
                var expectedCaveats = evaluation.Choices
                    .Where(c => c.Selected is not null)
                    .SelectMany(c => c.Options.Where(o => o.Option == c.Selected && o.State == StackOptionState.Caveat).SelectMany(o => o.Reasons.Select(r => new StackCaveat(c.Choice, o.Option, r.Rule, r.Text))));
                Assert.Equal(expectedCaveats, composition.Caveats);
                leaves++;
                return;
            }

            var choice = StackChoices.Order[depth];
            var options = evaluation.Choice(choice).Options;
            if (choice == Origin && options.All(o => o.State == StackOptionState.Forbidden))
            {
                Walk(depth + 1, selection); // API only: no origin model.
                return;
            }

            foreach (var option in options)
            {
                var variants = choice == Vcs
                    ? vcsVariants.Where(v => v.Pack == option.Option).Select(v => With(selection, choice, option.Option, v.SelfManaged))
                    : new[] { With(selection, choice, option.Option, false) };
                foreach (var next in variants)
                {
                    if (option.State == StackOptionState.Forbidden)
                    {
                        var violations = Violations(Complete(next));
                        Assert.Contains(violations, v => v.Layer == choice && v.Option == option.Option && option.Reasons.Any(r => r.Rule == v.Rule && r.Text == v.Reason));
                        rejected++;
                    }
                    else
                    {
                        Walk(depth + 1, next);
                    }
                }
            }
        }

        Walk(0, new StackSelection());

        Assert.True(leaves > 1000, $"only {leaves} valid compositions");
        Assert.True(rejected > 1000, $"only {rejected} rejected compositions");
    }

    private static StackSelection With(StackSelection s, string choice, string option, bool selfManaged)
    {
        var deploy = s.Deploy ?? new StackDeployPick();
        return choice switch
        {
            Fe => s with { Frontend = new StackComponentPick(option) },
            Be => s with { Backend = new StackComponentPick(option) },
            Data => s with { Data = new StackDataPick(option) },
            Vcs => s with { Vcs = new StackVcsPick(option, SelfManaged: selfManaged) },
            FeHost => s with { Deploy = deploy with { FrontendHost = option } },
            BeHost => s with { Deploy = deploy with { BackendHost = option } },
            DbHost => s with { Deploy = deploy with { DatabaseHost = option } },
            Local => s with { Deploy = deploy with { Local = option } },
            _ => s with { OriginModel = option },
        };
    }

    /// <summary>A forbidden choice is rejected whatever comes after it: leave the rest out, but name a VCS (required).</summary>
    private static StackSelection Complete(StackSelection s) => s.Vcs is null ? s with { Vcs = new StackVcsPick("vcs-github") } : s;
}
