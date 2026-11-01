using Aide.Domain.Stack;

namespace Aide.UnitTests.Stack;

/// <summary>FR-118, §4.18 "Detection": the pure stack detector over a repo's file list and manifests.</summary>
public class StackDetectorTests
{
    private const string Fe = StackChoices.Frontend;
    private const string Be = StackChoices.Backend;
    private const string Data = StackChoices.Data;
    private const string Vcs = StackChoices.Vcs;
    private const string FeHost = StackChoices.FrontendHost;
    private const string BeHost = StackChoices.BackendHost;
    private const string DbHost = StackChoices.DatabaseHost;

    /// <summary>A repo file: a path and, for a manifest, its content (<c>null</c>: the file's presence only).</summary>
    private static (string Path, string? Content) F(string path, string? content = null) => (path, content);

    private static string Npm(string? dependencies = null, string? devDependencies = null) =>
        $$"""{ "name": "app", "dependencies": { {{dependencies}} }, "devDependencies": { {{devDependencies}} } }""";

    private static string Csproj(string references, bool web = false) =>
        $"""
        <Project Sdk="{(web ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk")}">
          <ItemGroup>
            {references}
          </ItemGroup>
        </Project>
        """;

    /// <summary>Runs both steps as a caller would: list the manifests to read, "fetch" them, detect.</summary>
    private static StackDetection Detect(string vcsHost, params (string Path, string? Content)[] files)
    {
        var paths = files.Select(f => f.Path).ToList();
        var available = files.Where(f => f.Content is not null).ToDictionary(f => f.Path, f => f.Content!, StringComparer.Ordinal);
        var manifests = StackDetector.ManifestsToRead(paths)
            .Where(available.ContainsKey)
            .ToDictionary(p => p, p => available[p], StringComparer.Ordinal);
        return StackDetector.Detect(paths, manifests, vcsHost);
    }

    private static StackDetection Detect(params (string Path, string? Content)[] files) => Detect("github", files);

    private static StackDetectedChoice Suggested(StackDetection detection, string choice) =>
        detection.Suggestion(choice) ?? throw new Xunit.Sdk.XunitException($"No suggestion for {choice}.");

    private static void AssertSuggested(StackDetection detection, string choice, string option, string? path = null)
    {
        var suggestion = Suggested(detection, choice);
        Assert.Equal(option, suggestion.Option);
        Assert.Equal(path, suggestion.Path);
        Assert.NotEmpty(suggestion.Evidence);
    }

    // ── Acceptance example ───────────────────────────────────────────────

    [Fact]
    public void FR118_Vue_frontend_with_Supabase_and_no_backend_is_fe_vue_at_frontend_Backend_None_and_Supabase_BaaS()
    {
        var detection = Detect(
            F("README.md"),
            F("frontend/package.json", Npm("\"vue\": \"^3.5.13\", \"@supabase/supabase-js\": \"^2.49.0\"", "\"vite\": \"^6.2.0\"")),
            F("frontend/index.html"),
            F("frontend/src/main.ts"),
            F("supabase/config.toml"),
            F("supabase/migrations/20260101000000_init.sql"));

        AssertSuggested(detection, Fe, "fe-vue", "frontend");
        Assert.Equal(["`vue` 3.5 in `frontend/package.json`", "`vite` 6.2 in `frontend/package.json`"], Suggested(detection, Fe).Evidence);
        AssertSuggested(detection, Be, StackIds.None);
        var data = Suggested(detection, Data);
        Assert.Equal(("data-supabase", "baas"), (data.Option, data.Mode));
        Assert.Equal(["`supabase/config.toml`", "`supabase/migrations/`"], data.Evidence);
        AssertSuggested(detection, DbHost, "supabase");
        AssertSuggested(detection, Vcs, "vcs-github");
        Assert.Null(detection.Suggestion(FeHost));
        Assert.Null(detection.Suggestion(BeHost));
        Assert.Empty(detection.Conflicts);
    }

    // ── Frontend ─────────────────────────────────────────────────────────

    [Fact]
    public void FR118_Nextjs_at_the_root_is_fe_nextjs_at_dot_even_with_vite_for_vitest()
    {
        var detection = Detect(
            F("package.json", Npm("\"next\": \"^16.1.0\", \"react\": \"^19.2.0\", \"@prisma/client\": \"^6.3.0\"", "\"vite\": \"^7.0.0\", \"vitest\": \"^3.2.0\", \"prisma\": \"^6.3.0\"")),
            F("next.config.ts"),
            F("prisma/schema.prisma"),
            F("src/app/page.tsx"));

        AssertSuggested(detection, Fe, "fe-nextjs", ".");
        Assert.Equal(["`next` 16.1 in `package.json`", "`next.config.ts`"], Suggested(detection, Fe).Evidence);
        AssertSuggested(detection, Be, StackIds.None);
        AssertSuggested(detection, Data, "data-prisma");
        Assert.Empty(detection.Conflicts);
    }

    [Fact]
    public void FR118_Angular_needs_angular_core_and_angular_json()
    {
        var angular = Detect(F("web/package.json", Npm("\"@angular/core\": \"^20.1.0\"")), F("web/angular.json"));
        AssertSuggested(angular, Fe, "fe-angular", "web");
        Assert.Contains("`web/angular.json`", Suggested(angular, Fe).Evidence);

        var nearMiss = Detect(F("web/package.json", Npm("\"@angular/core\": \"^20.1.0\"")));
        Assert.Null(nearMiss.Suggestion(Fe));
    }

    [Fact]
    public void FR118_React_needs_vite_for_fe_react_vite()
    {
        AssertSuggested(Detect(F("package.json", Npm("\"react\": \"^19.2.0\"", "\"vite\": \"^7.0.0\""))), Fe, "fe-react-vite", ".");
        Assert.Null(Detect(F("package.json", Npm("\"react\": \"^19.2.0\", \"react-scripts\": \"5.0.1\""))).Suggestion(Fe));
    }

    [Fact]
    public void FR118_An_index_html_with_no_framework_dependency_is_fe_vanilla()
    {
        var vanilla = Detect(F("site/index.html"), F("site/app.js"), F("site/package.json", Npm(devDependencies: "\"eslint\": \"^9.0.0\"")));
        AssertSuggested(vanilla, Fe, "fe-vanilla", "site");
        Assert.Equal(["`site/index.html` with no framework dependency"], Suggested(vanilla, Fe).Evidence);

        Assert.Null(Detect(F("public/index.html"), F("docs/index.html")).Suggestion(Fe));
        Assert.Null(Detect(F("index.html"), F("package.json", Npm("\"preact\": \"^10.0.0\""))).Suggestion(Fe));
    }

    [Fact]
    public void FR118_Conflicting_frontends_leave_the_frontend_empty_with_both_shown()
    {
        var detection = Detect(
            F("web/package.json", Npm("\"react\": \"^19.2.0\"", "\"vite\": \"^7.0.0\"")),
            F("admin/package.json", Npm("\"@angular/core\": \"^20.1.0\"")),
            F("admin/angular.json"));

        Assert.Null(detection.Suggestion(Fe));
        var conflict = detection.Conflict(Fe);
        Assert.NotNull(conflict);
        Assert.Equal(
            [("fe-angular", "admin"), ("fe-react-vite", "web")],
            conflict.Candidates.Select(c => (c.Option, c.Path)).Order().ToList());
        Assert.All(conflict.Candidates, c => Assert.NotEmpty(c.Evidence));
        Assert.Contains("`@angular/core` 20.1 in `admin/package.json`", conflict.Candidates.Single(c => c.Option == "fe-angular").Evidence);
    }

    // ── Backend and data ─────────────────────────────────────────────────

    [Fact]
    public void FR118_AspNet_with_EF_Core_Postgres_in_backend_is_be_aspnet_at_backend()
    {
        var detection = Detect(
            F("backend/Aide.slnx"),
            F("backend/src/Api/Api.csproj", Csproj("""<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.1" />""", web: true)),
            F("backend/src/Infrastructure/Infrastructure.csproj", Csproj("""<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />""")),
            F("backend/tests/Api.Tests/Api.Tests.csproj", Csproj("""<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />""", web: true)),
            F("frontend/package.json", Npm("\"react\": \"^19.2.0\"", "\"vite\": \"^7.0.0\"")));

        AssertSuggested(detection, Be, "be-aspnet", "backend");
        Assert.Equal(
            ["`Microsoft.NET.Sdk.Web` in `backend/src/Api/Api.csproj`", "`Microsoft.AspNetCore.OpenApi` 10.0 in `backend/src/Api/Api.csproj`"],
            Suggested(detection, Be).Evidence);
        AssertSuggested(detection, Data, "data-efcore-postgres");
        Assert.Equal(["`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0 in `backend/src/Infrastructure/Infrastructure.csproj`"], Suggested(detection, Data).Evidence);
        AssertSuggested(detection, Fe, "fe-react-vite", "frontend");
        Assert.Empty(detection.Conflicts);
    }

    [Theory]
    [InlineData("backend/src/Api/Api.csproj", null, "backend")]
    [InlineData("src/Api/Api.csproj", "Api.sln", ".")]
    [InlineData("server/Api.csproj", null, "server")]
    [InlineData("services/api/Api/Api.csproj", "services/api/Api.sln", "services/api")]
    public void FR118_A_dotnet_backend_path_is_its_solution_folder(string project, string? solution, string path)
    {
        (string, string?)[] files = solution is null
            ? [F(project, Csproj(string.Empty, web: true))]
            : [F(project, Csproj(string.Empty, web: true)), F(solution)];
        AssertSuggested(Detect(files), Be, "be-aspnet", path);
    }

    [Fact]
    public void FR118_FastAPI_with_Alembic_and_SQLAlchemy_is_be_fastapi_and_data_sqlalchemy()
    {
        var detection = Detect(
            F("api/pyproject.toml", """
                [project]
                name = "api"
                requires-python = ">=3.13"
                dependencies = [
                    "fastapi[standard]>=0.115.2",
                    "sqlalchemy[asyncio]>=2.0.36",
                    "alembic>=1.14",
                ]
                """),
            F("api/alembic.ini"),
            F("api/alembic/env.py"));

        AssertSuggested(detection, Be, "be-fastapi", "api");
        Assert.Equal(["`fastapi` 0.115 in `api/pyproject.toml`"], Suggested(detection, Be).Evidence);
        AssertSuggested(detection, Data, "data-sqlalchemy");
        Assert.Equal(["`alembic` 1.14 in `api/pyproject.toml`", "`sqlalchemy` 2.0 in `api/pyproject.toml`"], Suggested(detection, Data).Evidence);
        AssertSuggested(detection, Fe, StackIds.None);
    }

    [Fact]
    public void FR118_FastAPI_in_requirements_or_Poetry_and_SQLAlchemy_without_Alembic_is_no_data_pack()
    {
        var requirements = Detect(F("requirements.txt", "# app\nFastAPI==0.115.0\nSQLAlchemy==2.0.36\nuvicorn[standard]\n"));
        AssertSuggested(requirements, Be, "be-fastapi", ".");
        Assert.Null(requirements.Suggestion(Data));

        var poetry = Detect(
            F("backend/pyproject.toml", "[tool.poetry.dependencies]\npython = \"^3.13\"\nfastapi = {version = \"^0.115.0\", extras = [\"standard\"]}\nsqlalchemy = \"^2.0\"\n"),
            F("backend/alembic/env.py"));
        AssertSuggested(poetry, Be, "be-fastapi", "backend");
        Assert.Equal(["`fastapi` 0.115 in `backend/pyproject.toml`"], Suggested(poetry, Be).Evidence);
        Assert.Equal(["`backend/alembic/`", "`sqlalchemy` 2.0 in `backend/pyproject.toml`"], Suggested(poetry, Data).Evidence);

        Assert.Null(Detect(F("requirements.txt", "fastapi-users==14.0\n")).Suggestion(Be));
    }

    [Fact]
    public void FR118_Nest_with_Prisma_is_be_nestjs_not_express_and_data_prisma()
    {
        var detection = Detect(
            F("package.json", Npm(devDependencies: "\"turbo\": \"^2.0.0\"")),
            F("apps/api/package.json", Npm("\"@nestjs/core\": \"^11.1.0\", \"express\": \"^5.1.0\", \"@prisma/client\": \"^6.3.0\"", "\"prisma\": \"^6.3.0\"")),
            F("apps/web/package.json", Npm("\"react\": \"^19.2.0\"", "\"vite\": \"^7.0.0\"")));

        AssertSuggested(detection, Be, "be-nestjs", "apps/api");
        Assert.Equal(["`@nestjs/core` 11.1 in `apps/api/package.json`"], Suggested(detection, Be).Evidence);
        AssertSuggested(detection, Data, "data-prisma");
        Assert.Equal(["`prisma` 6.3 in `apps/api/package.json`", "`@prisma/client` 6.3 in `apps/api/package.json`"], Suggested(detection, Data).Evidence);
        AssertSuggested(detection, Fe, "fe-react-vite", "apps/web");
        Assert.Empty(detection.Conflicts);
    }

    [Fact]
    public void FR118_Express_without_Nest_is_be_express_and_drizzle_is_data_drizzle()
    {
        var detection = Detect(F("server/package.json", Npm("\"express\": \"^5.1.0\", \"drizzle-orm\": \"^0.44.0\"")));
        AssertSuggested(detection, Be, "be-express", "server");
        AssertSuggested(detection, Data, "data-drizzle");
    }

    [Fact]
    public void FR118_Spring_Boot_with_Flyway_and_JPA_is_be_spring_boot_and_data_jpa_flyway()
    {
        const string pom = """
            <project>
              <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-data-jpa</artifactId></dependency>
                <dependency><groupId>org.flywaydb</groupId><artifactId>flyway-core</artifactId></dependency>
              </dependencies>
            </project>
            """;
        var detection = Detect(F("backend/pom.xml", pom));
        AssertSuggested(detection, Be, "be-spring-boot", "backend");
        Assert.Equal(["`spring-boot-starter-web` in `backend/pom.xml`"], Suggested(detection, Be).Evidence);
        AssertSuggested(detection, Data, "data-jpa-flyway");

        var noFlyway = Detect(F("build.gradle.kts", "implementation(\"org.springframework.boot:spring-boot-starter-web\")\nimplementation(\"org.springframework.boot:spring-boot-starter-data-jpa\")"));
        AssertSuggested(noFlyway, Be, "be-spring-boot", ".");
        Assert.Null(noFlyway.Suggestion(Data));
    }

    [Fact]
    public void FR118_Conflicting_data_packs_leave_data_empty_with_both_shown()
    {
        var detection = Detect(F("backend/src/Api/Api.csproj", Csproj(
            """
            <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
            """, web: true)));

        Assert.Null(detection.Suggestion(Data));
        Assert.Equal(["data-efcore-postgres", "data-efcore-sqlserver"], detection.Conflict(Data)!.Candidates.Select(c => c.Option));
    }

    [Fact]
    public void FR118_Supabase_config_with_another_data_pack_is_Supabase_as_the_database_host_in_host_mode()
    {
        var detection = Detect(
            F("backend/package.json", Npm("\"express\": \"^5.1.0\", \"drizzle-orm\": \"^0.44.0\"")),
            F("supabase/config.toml"));

        AssertSuggested(detection, Data, "data-drizzle");
        AssertSuggested(detection, DbHost, "supabase");
        Assert.Contains("with another data pack: Supabase in host mode", Suggested(detection, DbHost).Evidence);
    }

    // ── Hosts ────────────────────────────────────────────────────────────

    [Fact]
    public void FR118_Host_files_suggest_the_frontend_and_backend_hosts()
    {
        var split = Detect(
            F("frontend/package.json", Npm("\"react\": \"^19.2.0\"", "\"vite\": \"^7.0.0\"")),
            F("backend/package.json", Npm("\"@nestjs/core\": \"^11.1.0\"")),
            F("netlify.toml"),
            F("render.yaml"));
        AssertSuggested(split, FeHost, "deploy-netlify");
        Assert.Equal(["`netlify.toml`"], Suggested(split, FeHost).Evidence);
        AssertSuggested(split, BeHost, "deploy-render");
        Assert.Empty(split.Conflicts);

        var vercel = Detect(F("package.json", Npm("\"next\": \"16.1.0\"")), F("vercel.json"));
        AssertSuggested(vercel, FeHost, "deploy-vercel");
        Assert.Null(vercel.Suggestion(BeHost));

        var pages = Detect(F("index.html"), F(".github/workflows/pages.yml"), F(".github/workflows/ci.yml"));
        AssertSuggested(pages, Fe, "fe-vanilla", ".");
        AssertSuggested(pages, FeHost, "deploy-github-pages");

        var cloudflare = Detect(F("web/package.json", Npm("\"vue\": \"^3.5.0\"", "\"vite\": \"^7.0.0\"")), F("web/wrangler.jsonc"));
        AssertSuggested(cloudflare, FeHost, "deploy-cloudflare");
        Assert.Equal(["`web/wrangler.jsonc`"], Suggested(cloudflare, FeHost).Evidence);
    }

    [Fact]
    public void FR118_Railway_config_as_code_and_legacy_railway_json_suggest_Railway()
    {
        var current = Detect(F("api/package.json", Npm("\"express\": \"^5.1.0\"")), F(".railway/railway.ts"));
        AssertSuggested(current, BeHost, "deploy-railway");
        Assert.Equal(["`.railway/`"], Suggested(current, BeHost).Evidence);

        var legacy = Detect(F("api/package.json", Npm("\"express\": \"^5.1.0\"")), F("api/railway.json"), F("railway.toml"));
        AssertSuggested(legacy, BeHost, "deploy-railway");
        Assert.Equal(
            ["`api/railway.json` (legacy config, deprecated by Railway)", "`railway.toml` (legacy config, deprecated by Railway)"],
            Suggested(legacy, BeHost).Evidence);
        Assert.Null(legacy.Suggestion(FeHost));
    }

    [Fact]
    public void FR118_Two_hosts_for_the_same_layer_conflict()
    {
        var detection = Detect(F("package.json", Npm("\"vue\": \"^3.5.0\"", "\"vite\": \"^7.0.0\"")), F("vercel.json"), F("netlify.toml"));
        Assert.Null(detection.Suggestion(FeHost));
        Assert.Equal(["deploy-netlify", "deploy-vercel"], detection.Conflict(FeHost)!.Candidates.Select(c => c.Option).Order());
    }

    // ── Scope ────────────────────────────────────────────────────────────

    [Fact]
    public void FR118_Ignored_folders_are_never_looked_into()
    {
        (string, string?)[] files =
        [
            F("node_modules/next/package.json", Npm("\"react\": \"^19.2.0\"")),
            F("frontend/node_modules/vite/package.json", Npm("\"vue\": \"^3.5.0\"", "\"vite\": \"^7.0.0\"")),
            F("dist/index.html"),
            F("backend/bin/Debug/Api.csproj", Csproj(string.Empty, web: true)),
            F("backend/obj/Api.csproj", Csproj(string.Empty, web: true)),
            F(".git/config"),
            F("tests/fixtures/package.json", Npm("\"express\": \"^5.0.0\"")),
        ];
        Assert.Empty(StackDetector.ManifestsToRead(files.Select(f => f.Item1)));

        var detection = Detect(files);
        Assert.Equal([Vcs], detection.Suggestions.Select(s => s.Choice));
        Assert.Empty(detection.Conflicts);
    }

    [Fact]
    public void FR118_Manifests_are_read_shallowest_first_up_to_the_cap()
    {
        var files = Enumerable.Range(0, 120).Select(i => $"packages/p{i:D3}/package.json")
            .Append("package.json")
            .Append("a/b/c/package.json")
            .Append("frontend\\package.json")
            .ToList();

        var manifests = StackDetector.ManifestsToRead(files);
        Assert.Equal(StackDetector.MaxManifests, manifests.Count);
        Assert.Equal("package.json", manifests[0]);
        Assert.Equal("frontend/package.json", manifests[1]);
        Assert.DoesNotContain("a/b/c/package.json", manifests);
    }

    [Fact]
    public void FR118_Unreadable_or_unparsable_manifests_are_skipped()
    {
        var detection = StackDetector.Detect(
            ["package.json", "web/package.json", "web/vite.config.ts"],
            new Dictionary<string, string> { ["package.json"] = "{ not json", ["web/package.json"] = "[]" },
            "gitlab");
        Assert.Equal([Vcs], detection.Suggestions.Select(s => s.Choice));
    }

    // ── VCS ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("github", "vcs-github", "the repo is on GitHub")]
    [InlineData("gitlab", "vcs-gitlab", "the repo is on GitLab")]
    [InlineData("gitlab-self-managed", "vcs-gitlab", "the repo is on self-managed GitLab")]
    [InlineData("bitbucket", "vcs-bitbucket", "the repo is on Bitbucket")]
    public void FR118_VCS_is_fixed_to_the_repo_host(string host, string option, string evidence)
    {
        var detection = Detect(host, F(".github/workflows/ci.yml"), F("package.json", Npm("\"next\": \"16.1.0\"")));
        var vcs = Suggested(detection, Vcs);
        Assert.Equal(option, vcs.Option);
        Assert.Equal([evidence], vcs.Evidence);
    }

    [Fact]
    public void FR118_An_unknown_repo_host_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackDetector.Detect([], new Dictionary<string, string>(), "sourcehut"));
    }
}
