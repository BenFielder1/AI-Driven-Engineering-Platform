using System.Text.Json;
using Aide.Domain.Review;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackResolverTests;

namespace Aide.UnitTests.Review;

/// <summary>FR-246 (§4.42): stable finding ids.</summary>
public class FindingIdTests
{
    [Theory]
    [InlineData(FindingIds.Dependency, "@eslint/eslintrc", "finding:dependency:eslint-eslintrc")]
    [InlineData(FindingIds.Endpoint, "/api/todos/{id}", "finding:endpoint:api-todos-id")]
    [InlineData(FindingIds.Endpoint, "/robots.txt", "finding:endpoint:robots-txt")]
    [InlineData(FindingIds.Endpoint, "/", "finding:endpoint:root")]
    [InlineData(FindingIds.Dependency, "Microsoft.EntityFrameworkCore", "finding:dependency:microsoft-entityframeworkcore")]
    public void FR246_S4_42_Table_examples(string kind, string subject, string expected) =>
        Assert.Equal(expected, FindingIds.Of(kind, subject));

    [Fact]
    public void FR246_S4_42_Secret_stack_and_vercel_subjects()
    {
        Assert.Equal("finding:secret:frontend-src-config-ts-12", FindingIds.ForSecret("frontend/src/config.ts", 12));
        Assert.Equal("finding:stack:container-non-root-backend-dockerfile", FindingIds.ForStack(StackReviewChecks.ContainerNonRoot, "backend/Dockerfile"));
        Assert.Equal("finding:stack:health-endpoints-repo", FindingIds.ForStack(StackReviewChecks.HealthEndpoints, null));
        Assert.Equal("finding:vercel:deploy-branch-control-frontend-vercel-json", FindingIds.ForVercel(StackReviewChecks.DeployBranchControl, "frontend/vercel.json"));
    }

    [Fact]
    public void FR246_S4_42_The_slug_is_cut_to_60_characters_and_empty_is_root()
    {
        var id = FindingIds.Of(FindingIds.Endpoint, "/" + new string('a', 59) + "/bbbb");

        Assert.Equal("finding:endpoint:" + new string('a', 59), id);
        Assert.Equal(60, FindingIds.Slug(new string('x', 80)).Length);
        Assert.Equal("root", FindingIds.Slug("/// ---"));
    }

    [Fact]
    public void FR246_Acceptance_dependency_and_endpoint_findings_get_the_same_ids_every_cycle()
    {
        IReadOnlyList<ReviewFinding> Cycle() =>
        [
            .. DeterministicChecks.DependencyAllowlist(["react"], ["@eslint/eslintrc"]),
            .. DeterministicChecks.EndpointCoverage(["/api/todos/{id}"], []),
        ];

        Assert.Equal(["finding:dependency:eslint-eslintrc", "finding:endpoint:api-todos-id"], Cycle().Select(f => f.FindingId));
        Assert.Equal(Cycle().Select(f => f.FindingId), Cycle().Select(f => f.FindingId));
        Assert.All(Cycle(), f => Assert.Null(f.FailureId));
    }

    [Fact]
    public void FR246_A_secret_scan_hit_has_a_path_and_line_id()
    {
        var finding = Assert.Single(DeterministicChecks.SecretScan([new SourceFile("frontend/src/config.ts", "// config\nconst key = 'AKIAIOSFODNN7EXAMPLE';")]));

        Assert.Equal("finding:secret:frontend-src-config-ts-2", finding.FindingId);
    }

    [Fact]
    public void FR246_Stack_findings_carry_their_check_key_and_path_or_repo()
    {
        var resolution = Resolve(AspnetSpa(local: "compose"));

        var findings = StackReviewChecks.Run(resolution, new StackReviewInput([new SourceFile("backend/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n")]));

        Assert.Contains(findings, f => f.FindingId == "finding:stack:container-non-root-backend-dockerfile");
        Assert.All(findings, f => Assert.StartsWith("finding:stack:", f.FindingId, StringComparison.Ordinal));
        Assert.All(findings.Where(f => f.Path is null), f => Assert.EndsWith("-repo", f.FindingId, StringComparison.Ordinal));
    }

    [Fact]
    public void FR246_Duplicate_finding_ids_get_2_and_3_in_report_order_and_failure_ids_are_untouched()
    {
        ReviewFinding F(string id) => new(ReviewCategory.Architecture, Severity.Major, id) { FindingId = id };
        var failure = new ReviewFinding(ReviewCategory.Testing, Severity.Major, "x") { FailureId = "failure:check:build" };

        var result = FindingIds.Disambiguate([F("finding:dependency:zod"), failure, F("finding:dependency:zod"), F("finding:dependency:lodash"), F("finding:dependency:zod")]);

        Assert.Equal(
            ["finding:dependency:zod", null, "finding:dependency:zod-2", "finding:dependency:lodash", "finding:dependency:zod-3"],
            result.Select(f => f.FindingId));
        Assert.Equal("failure:check:build", result[1].FailureId);
    }

    [Fact]
    public void FR246_The_prompt_shows_each_finding_with_its_id_and_failures_and_unnumbered_findings_as_before()
    {
        var dependency = new ReviewFinding(ReviewCategory.Architecture, Severity.Major, "Dependency '@eslint/eslintrc' is not in the specification's architecture.", "frontend/package.json")
        {
            FindingId = "finding:dependency:eslint-eslintrc",
        };
        var old = new ReviewFinding(ReviewCategory.Security, Severity.Critical, "Possible secret committed", "src/key.ts");
        var failure = new ReviewFinding(ReviewCategory.Testing, Severity.Major, "required check build failed") { FailureId = "failure:check:build" };

        var text = ReviewCode.RenderFindings([dependency, old, failure], allowlistChecked: true, endpointCount: 0);

        Assert.Contains(
            "- finding:dependency:eslint-eslintrc [architecture/major] Dependency '@eslint/eslintrc' is not in the specification's architecture. (frontend/package.json)\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("\n- [security/critical] Possible secret committed (src/key.ts)", text, StringComparison.Ordinal);
        Assert.Contains("- [testing/major] failure id failure:check:build (CI check): required check build failed\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR246_The_finding_id_is_stored_additively_and_old_rows_read_back_without_one()
    {
        var withId = new ReviewFinding(ReviewCategory.Architecture, Severity.Major, "m") { FindingId = "finding:dependency:zod" };

        Assert.Contains("\"findingId\":\"finding:dependency:zod\"", JsonSerializer.Serialize(withId, JsonSerializerOptions.Web), StringComparison.Ordinal);
        Assert.DoesNotContain("findingId", JsonSerializer.Serialize(withId with { FindingId = null }, JsonSerializerOptions.Web), StringComparison.Ordinal);
        var old = JsonSerializer.Deserialize<ReviewFinding>("""{"category":"Architecture","severity":"Major","message":"m","path":null,"source":"Deterministic"}""", WebWithEnums);
        Assert.Null(old!.FindingId);
    }

    private static readonly JsonSerializerOptions WebWithEnums = new(JsonSerializerOptions.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

/// <summary>FR-251 (§4.44): spec endpoint paths are read whole; catch-all and fallback coverage.</summary>
public class SpecEndpointPathTests
{
    private static string Spec(string body) => $"# Spec\n\n## API Contracts\n{body}\n\n## Other\n";

    [Fact]
    public void FR251_S4_44_Catch_all_and_constrained_paths_are_read_whole()
    {
        var spec = Spec("""
            - GET /{any*}
            - GET /{*any}
            - GET /[...slug]
            - GET /*
            - `/docs/[...slug]` serves the docs.
            - GET /api/items/{id:int}
            Files: the route /files/*.
            - **/api/todos** (bold)
            """);

        Assert.Equal(
            ["/{any*}", "/{*any}", "/[...slug]", "/*", "/docs/[...slug]", "/api/items/{id:int}", "/files/*", "/api/todos"],
            SpecReading.EndpointPaths(spec));
    }

    [Fact]
    public void FR251_S4_44_A_path_with_an_unclosed_bracket_is_dropped_never_reported()
    {
        var read = SpecReading.ReadEndpointPaths(Spec("- GET /api/{id and /api/todos\n- GET /docs/[slug\n- GET /x/<name"));

        Assert.Equal(["/api/todos"], read.Paths);
        Assert.Equal(["/api/{id", "/docs/[slug", "/x/<name"], read.Dropped);
    }

    [Fact]
    public void FR251_The_0_6_0_paths_still_read_the_same() =>
        Assert.Equal(["/api/todos", "/api/todos/{id}", "/health"], SpecReading.EndpointPaths(Spec("| GET | /api/todos |\n| GET | /api/todos/{id}. |\nSee https://example.com/docs/api. Health: `/health`, ok?")));

    [Theory]
    [InlineData("users", RouteSegmentKind.Literal)]
    [InlineData("robots.txt", RouteSegmentKind.Literal)]
    [InlineData(":id", RouteSegmentKind.Param)]
    [InlineData(":id?", RouteSegmentKind.Param)]
    [InlineData("{id}", RouteSegmentKind.Param)]
    [InlineData("{id:int}", RouteSegmentKind.Param)]
    [InlineData("{id?}", RouteSegmentKind.Param)]
    [InlineData("<id>", RouteSegmentKind.Param)]
    [InlineData("<int:id>", RouteSegmentKind.Param)]
    [InlineData("[id]", RouteSegmentKind.Param)]
    [InlineData("*", RouteSegmentKind.CatchAll)]
    [InlineData("**", RouteSegmentKind.CatchAll)]
    [InlineData("{*any}", RouteSegmentKind.CatchAll)]
    [InlineData("{**any}", RouteSegmentKind.CatchAll)]
    [InlineData("{any*}", RouteSegmentKind.CatchAll)]
    [InlineData("{any+}", RouteSegmentKind.CatchAll)]
    [InlineData(":path*", RouteSegmentKind.CatchAll)]
    [InlineData(":path+", RouteSegmentKind.CatchAll)]
    [InlineData("[...slug]", RouteSegmentKind.CatchAll)]
    [InlineData("[[...slug]]", RouteSegmentKind.OptionalCatchAll)]
    [InlineData("(.*)", RouteSegmentKind.CatchAll)]
    public void FR251_S4_44_Segment_kinds(string segment, RouteSegmentKind kind) =>
        Assert.Equal(kind, RoutePaths.Classify(segment).Kind);

    [Fact]
    public void FR251_Acceptance_a_root_catch_all_is_covered_by_a_Next_catch_all_page()
    {
        foreach (var path in new[] { "/{any*}", "/{*any}", "/[...slug]", "/*" })
        {
            Assert.Empty(DeterministicChecks.EndpointCoverage([path], [], ["frontend/src/app/[...slug]/page.tsx"]));
        }
    }

    [Fact]
    public void FR251_Acceptance_a_root_catch_all_is_covered_by_a_vercel_SPA_rewrite()
    {
        SourceFile[] files = [new("frontend/vercel.json", """{"rewrites":[{"source":"/(.*)","destination":"/index.html"}]}""")];

        Assert.Empty(DeterministicChecks.EndpointCoverage(["/{any*}"], files));
    }

    [Fact]
    public void FR251_Acceptance_without_a_catch_all_or_fallback_the_finding_names_the_whole_path()
    {
        SourceFile[] files =
        [
            new("src/app.ts", "/* routes */\napp.get('/api/todos', list);\nres.header('Access-Control-Allow-Origin', '*');"),
            new("vercel.json", """{"headers":[{"source":"/(.*)","headers":[]}]}"""),
        ];

        var finding = Assert.Single(DeterministicChecks.EndpointCoverage(["/{any*}"], files, ["src/app/page.tsx"]));

        Assert.Equal("Endpoint '/{any*}' from the specification was not found in the code.", finding.Message);
        Assert.Equal("finding:endpoint:any", finding.FindingId);
    }

    [Theory]
    [InlineData("backend/Program.cs", "app.MapFallbackToFile(\"index.html\");")]
    [InlineData("backend/Program.cs", "app.MapFallback(() => Results.NotFound());")]
    [InlineData("backend/Program.cs", "app.MapGet(\"/{*path}\", Spa);")]
    [InlineData("src/router.tsx", "{ path: '*', element: <NotFound /> }")]
    [InlineData("src/App.tsx", "<Route path=\"*\" element={<NotFound />} />")]
    [InlineData("server.js", "app.get('/*', (req, res) => res.sendFile(index));")]
    [InlineData("server.js", "app.all('/(.*)', handler);")]
    [InlineData("netlify.toml", "[[redirects]]\n  from = \"/*\"\n  to = \"/index.html\"\n  status = 200\n")]
    [InlineData("public/_redirects", "/*    /index.html   200\n")]
    [InlineData("wrangler.toml", "[assets]\nnot_found_handling = \"single-page-application\"\n")]
    [InlineData("wrangler.jsonc", "{ \"assets\": { \"not_found_handling\": \"single-page-application\" } }")]
    [InlineData("vercel.json", "{\"rewrites\":[{\"source\":\"/:path*\",\"destination\":\"/\"}]}")]
    public void FR251_S4_44_A_root_catch_all_is_covered_by_any_fallback_in_a_file(string path, string content) =>
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/{any*}"], [new SourceFile(path, content)]));

    [Theory]
    [InlineData("next.config.ts", "async headers() { return [{ source: '/(.*)', headers: securityHeaders }]; }")]
    [InlineData("WebConfig.java", "registry.addMapping(\"/**\").allowedOrigins(\"*\");")]
    [InlineData("src/index.ts", "app.use('*', cors());\napp.use('/*', logger());")]
    [InlineData("vercel.json", "{\"headers\":[{\"source\":\"/:path*\",\"headers\":[]}]}")]
    public void FR251_A_catch_all_string_outside_a_route_position_is_not_a_fallback(string path, string content) =>
        Assert.Single(DeterministicChecks.EndpointCoverage(["/{any*}"], [new SourceFile(path, content)]));

    [Fact]
    public void FR251_S4_44_Parenthesised_and_sentence_paths_read_whole()
    {
        Assert.Equal(["/files/(.*)", "/api/todos"], SpecReading.EndpointPaths(Spec("- GET /files/(.*)\n- Lists todos (see /api/todos).")));
        Assert.Equal(RouteSegmentKind.CatchAll, RoutePaths.Segments("/files/(.*)")[1].Kind);
    }

    [Theory]
    [InlineData("app/[...x]/page.tsx")]
    [InlineData("src/app/[[...x]]/page.tsx")]
    [InlineData("frontend/src/app/not-found.tsx")]
    [InlineData("pages/404.tsx")]
    public void FR251_S4_44_A_root_catch_all_is_covered_by_a_fallback_page(string path) =>
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/*"], [], [path]));

    [Fact]
    public void FR251_S4_44_A_prefixed_catch_all_needs_a_catch_all_under_its_prefix()
    {
        SourceFile[] spa = [new("vercel.json", """{"rewrites":[{"source":"/(.*)","destination":"/index.html"}]}""")];

        Assert.Empty(DeterministicChecks.EndpointCoverage(["/docs/[...slug]"], [], ["app/docs/[...slug]/page.mdx"]));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/docs/[...slug]"], [new SourceFile("server.js", "app.get('/docs/*', docs);")]));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/files/{*path}"], [new SourceFile("Program.cs", "app.MapGet(\"/files/{**path}\", Get);")]));
        Assert.Single(DeterministicChecks.EndpointCoverage(["/docs/[...slug]"], spa, ["app/blog/[...slug]/page.mdx", "app/docs/page.mdx"]));
    }
}

/// <summary>FR-252 (§4.45): file-based routes and static files count for endpoint coverage.</summary>
public class FileRouteTests
{
    [Theory]
    [InlineData("frontend/src/app/robots.ts", "/robots.txt")]
    [InlineData("app/(marketing)/pricing/page.tsx", "/pricing")]
    [InlineData("app/api/todos/[id]/route.ts", "/api/todos/{param}")]
    [InlineData("app/docs/[...slug]/page.mdx", "/docs/{catch-all}")]
    [InlineData("app/_lib/x/page.tsx", null)]
    [InlineData("pages/api/todos/[id].ts", "/api/todos/{param}")]
    [InlineData("pages/index.tsx", "/")]
    [InlineData("public/robots.txt", "/robots.txt")]
    [InlineData("src/routes/blog/[slug]/+page.svelte", "/blog/{param}")]
    public void FR252_S4_45_Worked_examples(string file, string? route) =>
        Assert.Equal(route, FileRoutes.Of(file)?.ToString());

    [Theory]
    [InlineData("app/sitemap.ts", "/sitemap.xml")]
    [InlineData("app/sitemap.xml", "/sitemap.xml")]
    [InlineData("app/robots.txt", "/robots.txt")]
    [InlineData("app/manifest.ts", "/manifest.webmanifest")]
    [InlineData("app/manifest.json", "/manifest.json")]
    [InlineData("app/favicon.ico", "/favicon.ico")]
    [InlineData("app/icon.png", "/icon")]
    [InlineData("app/apple-icon.tsx", "/apple-icon")]
    [InlineData("app/blog/opengraph-image.tsx", "/blog/opengraph-image")]
    [InlineData("app/twitter-image.jpg", "/twitter-image")]
    [InlineData("app/@modal/login/page.tsx", "/login")]
    [InlineData("app/docs/[[...slug]]/page.tsx", "/docs/{catch-all?}")]
    [InlineData("app/(app)/app/page.tsx", "/app")]
    [InlineData("app/components/header.tsx", null)]
    [InlineData("app/layout.tsx", null)]
    [InlineData("src/pages/about.tsx", "/about")]
    [InlineData("pages/blog/index.jsx", "/blog")]
    [InlineData("pages/_app.tsx", null)]
    [InlineData("pages/_document.tsx", null)]
    [InlineData("pages/_error.tsx", null)]
    [InlineData("frontend/public/images/logo.svg", "/images/logo.svg")]
    [InlineData("src/routes/+page.svelte", "/")]
    [InlineData("src/routes/(app)/api/items/+server.ts", "/api/items")]
    [InlineData("node_modules/next/dist/pages/_app.js", null)]
    public void FR252_S4_45_App_Router_metadata_Pages_Router_static_and_SvelteKit_rules(string file, string? route) =>
        Assert.Equal(route, FileRoutes.Of(file)?.ToString());

    [Fact]
    public void FR252_Acceptance_robots_txt_is_covered_by_app_robots_ts_or_public_robots_txt()
    {
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/robots.txt"], [new SourceFile("src/app/robots.ts", "export default function robots() { return { rules: [] }; }")]));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/robots.txt"], [], ["public/robots.txt"]));
        Assert.Single(DeterministicChecks.EndpointCoverage(["/robots.txt"], [], ["src/app/page.tsx"]));
    }

    [Fact]
    public void FR252_Acceptance_pages_api_todos_id_covers_the_spec_param_path() =>
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/api/todos/{id}"], [], ["pages/api/todos/[id].ts"]));

    [Fact]
    public void FR252_S4_45_A_spec_literal_matches_only_the_same_literal_and_a_spec_param_matches_a_param_or_literal()
    {
        string[] tree = ["app/api/todos/[id]/route.ts", "app/api/users/me/route.ts"];

        Assert.Single(DeterministicChecks.EndpointCoverage(["/api/todos/active"], [], tree));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/api/users/{id}"], [], tree));
        Assert.Single(DeterministicChecks.EndpointCoverage(["/api/todos/{id}/tags"], [], tree));
    }

    [Fact]
    public void FR252_A_file_catch_all_covers_only_catch_all_spec_paths_with_its_prefix()
    {
        Assert.Single(DeterministicChecks.EndpointCoverage(["/docs/a/b"], [], ["app/docs/[...slug]/page.mdx"]));
        Assert.Single(DeterministicChecks.EndpointCoverage(["/docs"], [], ["app/docs/[[...slug]]/page.mdx"]));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/docs/[...slug]"], [], ["app/docs/[[...slug]]/page.mdx"]));
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/docs/{*path}"], [], ["app/docs/[...slug]/page.mdx"]));
    }

    [Fact]
    public void FR252_A_BFF_proxy_catch_all_does_not_cover_api_endpoints()
    {
        var findings = DeterministicChecks.EndpointCoverage(["/api/todos", "/api/todos/{id}"], [], ["frontend/src/app/api/[...path]/route.ts"]);

        Assert.Equal(["finding:endpoint:api-todos", "finding:endpoint:api-todos-id"], findings.Select(f => f.FindingId));
    }

    [Fact]
    public void FR252_A_root_optional_catch_all_does_not_cover_every_path_only_a_root_catch_all()
    {
        string[] tree = ["app/[[...slug]]/page.tsx"];

        Assert.Equal(2, DeterministicChecks.EndpointCoverage(["/pricing", "/api/todos/{id}"], [], tree).Count);
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/{*any}"], [], tree));
    }

    [Fact]
    public void FR252_S4_45_Trailing_slash_and_query_string_are_ignored() =>
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/pricing/", "/pricing?ref=home"], [], ["app/(marketing)/pricing/page.tsx"]));

    [Fact]
    public void FR252_The_0_6_0_text_match_still_covers_a_path() =>
        Assert.Empty(DeterministicChecks.EndpointCoverage(["/api/projects/:id"], [new SourceFile("api/projects.cs", "app.MapGet(\"/api/projects/{id:guid}\", Get);")]));
}

/// <summary>FR-253 (§4.46): only runtime dependencies are checked.</summary>
public class RuntimeDependencyTests
{
    private static IReadOnlyList<string> Used(string path, string content) => DependencyManifests.Used([new SourceFile(path, content)]);

    [Fact]
    public void FR253_Acceptance_a_devDependency_raises_no_finding_and_a_runtime_one_still_does()
    {
        var used = Used("frontend/package.json", """{"dependencies":{"lodash":"^4"},"devDependencies":{"@eslint/eslintrc":"^3"}}""");

        var finding = Assert.Single(DeterministicChecks.DependencyAllowlist(["react"], used));
        Assert.Equal("finding:dependency:lodash", finding.FindingId);
    }

    [Fact]
    public void FR253_S4_46_package_json_checks_dependencies_peer_and_optional_but_not_dev() =>
        Assert.Equal(
            ["react", "react-dom", "fsevents"],
            Used("package.json", """{"dependencies":{"react":"19"},"devDependencies":{"vitest":"3"},"peerDependencies":{"react-dom":"19"},"optionalDependencies":{"fsevents":"2"}}"""));

    [Theory]
    [InlineData("requirements.txt", true)]
    [InlineData("requirements-prod.txt", true)]
    [InlineData("requirements-dev.txt", false)]
    [InlineData("requirements_test.txt", false)]
    [InlineData("requirements-tests.txt", false)]
    [InlineData("requirements-lint.txt", false)]
    [InlineData("requirements-docs.txt", false)]
    [InlineData("api/requirements.dev.txt", false)]
    [InlineData("requirements-doc.txt", false)]
    [InlineData("requirements-docker.txt", true)]
    [InlineData("requirements-latest.txt", true)]
    public void FR253_S4_46_requirements_files_named_dev_test_lint_or_doc_are_skipped(string path, bool read) =>
        Assert.Equal(read ? ["flask"] : [], Used(path, "flask==3.0\n"));

    [Fact]
    public void FR253_S4_46_pyproject_checks_project_and_poetry_dependencies_only()
    {
        const string Pyproject = """
            [project]
            name = "api"
            dependencies = [
                "fastapi>=0.115",
                "uvicorn[standard]>=0.30",  # server
            ]

            [project.optional-dependencies]
            postgres = ["psycopg[binary]"]
            dev = ["pytest"]
            tests = ["coverage"]
            lint = ["ruff"]
            docs = ["mkdocs"]

            [dependency-groups]
            dev = ["mypy"]

            [tool.poetry.dependencies]
            python = "^3.12"
            httpx = "^0.27"

            [tool.poetry.group.dev.dependencies]
            black = "^24"
            """;

        Assert.Equal(["fastapi", "uvicorn", "psycopg", "httpx"], Used("pyproject.toml", Pyproject));
    }

    [Fact]
    public void FR253_S4_46_poetry_multi_line_constraints_are_one_dependency()
    {
        const string Pyproject = """
            [tool.poetry.dependencies]
            python = "^3.12"
            numpy = [
                {version = "1.24", python = "<3.9"},
                {version = "1.26", python = ">=3.9"}
            ]
            requests = { version = "2.32", extras = ["socks"] }
            httpx = "^0.27"
            """;

        Assert.Equal(["numpy", "requests", "httpx"], Used("pyproject.toml", Pyproject));
    }

    [Fact]
    public void FR253_S4_46_csproj_skips_private_assets_and_test_projects()
    {
        const string Project = """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Npgsql" Version="9" />
                <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="9" PrivateAssets="all" />
                <PackageReference Include="StyleCop.Analyzers" Version="1">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build</IncludeAssets>
                </PackageReference>
                <PackageReference Include="Serilog">
                  <Version>4</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """;

        Assert.Equal(["Npgsql", "Serilog"], Used("backend/src/App.Api/App.Api.csproj", Project));
        Assert.Empty(Used("backend/src/App.Api/App.Api.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));
        Assert.Empty(Used("backend/tests/App.Integration/App.Integration.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));
        Assert.Empty(Used("backend/src/App.UnitTests/App.UnitTests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));
    }

    [Fact]
    public void FR253_S4_46_Cargo_checks_dependencies_but_not_dev_or_build_dependencies() =>
        Assert.Equal(
            ["serde"],
            Used("Cargo.toml", "[package]\nname = \"x\"\n[dependencies]\nserde = \"1\"\n[dev-dependencies]\ncriterion = \"0.5\"\n[build-dependencies]\ncc = \"1\"\n"));

    [Fact]
    public void FR253_S4_46_Gemfile_skips_gems_in_development_and_test_groups()
    {
        const string Gemfile = """
            source "https://rubygems.org"
            gem "rails", "~> 8.0"
            group :development, :test do
              gem "rspec-rails"
              platforms :mri do
                gem "debug"
              end
              gem "rubocop"
            end
            group :test do
              gem "capybara"
            end
            group :production do
              gem "pg"
            end
            gem "puma"
            """;

        Assert.Equal(["rails", "pg", "puma"], Used("Gemfile", Gemfile));
    }

    [Fact]
    public void FR253_S4_46_pom_skips_test_scope()
    {
        const string Pom = """
            <project><dependencies>
              <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency>
              <dependency>
                <groupId>org.junit.jupiter</groupId>
                <artifactId>junit-jupiter</artifactId>
                <scope>test</scope>
              </dependency>
              <dependency><groupId>org.postgresql</groupId><artifactId>postgresql</artifactId><scope>runtime</scope></dependency>
            </dependencies></project>
            """;

        Assert.Equal(["spring-boot-starter-web", "postgresql"], Used("pom.xml", Pom));
    }

    [Fact]
    public void FR253_S4_46_go_mod_require_is_unchanged() =>
        Assert.Equal(
            ["github.com/gin-gonic/gin", "golang.org/x/text"],
            Used("go.mod", "module x\n\nrequire (\n\tgithub.com/gin-gonic/gin v1.9.0\n)\nrequire golang.org/x/text v0.3.0\n"));
}
