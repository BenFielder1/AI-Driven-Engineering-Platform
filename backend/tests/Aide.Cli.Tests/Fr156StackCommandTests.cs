using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-156: `aide stack …`, the stack options of `aide version start`, the listings, `status` and the exit codes.</summary>
public sealed class Fr156StackCommandTests : IDisposable
{
    private const string StackPath = $"/api/projects/{Samples.ProjectId}/stack";
    private const string InstanceId = "0199f2a0-9999-7000-8000-000000000001";

    private const string Composition = $$"""
        {"catalogVersion":"2026.10.1","catalogHash":"h1","frontend":{"pack":"fe-react-vite","path":"web"},"backend":{"pack":"be-express","path":"api"},
         "data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-gitlab","instanceId":"{{InstanceId}}","selfManaged":true},
         "deploy":{"frontendHost":"deploy-netlify","backendHost":"deploy-render","databaseHost":null,"local":"none"},"originModel":"proxy",
         "derived":{"nextjsMode":null,"agents":[],"originModelOptions":["proxy"],"specSections":[],"envNames":[],"gates":[]},
         "caveats":[{"layer":"frontendHost","option":"deploy-netlify","rule":"R9","text":"Netlify needs a deploy key."}]}
        """;

    private const string Stack = $$"""
        {"releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","version":"1.3.0","versionStatus":"in_development","composition":{{Composition}},
         "catalogVersion":"2026.10.1","catalogHash":"h1","currentCatalogVersion":"2026.10.1","currentCatalogHash":"h1","locked":true,"lockedReason":"plan_locked",
         "stackChange":{"baseHasCode":false,"layersChanged":[],"isStackChange":false},
         "legacyPreset":{"presetId":"p","presetName":"P","presetVersion":"1","label":"Preset p@1 (retired, migrated)"},"repoVcs":"vcs-gitlab"}
        """;

    private const string EmptyStack = Samples.EmptyStack;

    private const string Catalog = """
        {"version":"2026.10.1","contentHash":"h1",
         "layers":[{"id":"frontend","name":"Frontend","description":"d"},{"id":"data","name":"Data","description":"d"}],
         "choices":[
           {"id":"frontend","layer":"frontend","name":"Frontend","options":[{"id":"fe-nextjs","name":"Next.js","description":"d","chips":[],"pack":"fe-nextjs","packVersion":"1.0.0","source":null,"modes":[],"mode":null}]},
           {"id":"data","layer":"data","name":"Data","options":[{"id":"data-supabase","name":"Supabase (BaaS)","description":"d","chips":[],"pack":"data-supabase","packVersion":"1.0.0","source":null,"modes":[{"id":"baas","name":"BaaS","description":"d"},{"id":"host","name":"Host","description":"d"}],"mode":"baas"}]}],
         "rules":[],"recommendations":[]}
        """;

    private const string Detection = """
        {"ref":"main","commit":"0123456789abcdef","suggestions":[{"choice":"frontend","option":"fe-vue","path":"frontend","mode":null,"evidence":["frontend/package.json (vue, vite)"]}],
         "conflicts":[{"choice":"backend","candidates":[{"option":"be-express","path":"api","evidence":["api/package.json"]},{"option":"be-nestjs","path":"svc","evidence":["svc/package.json"]}]}],"truncated":true}
        """;

    private const string MigrationRun = """
        {"id":"0199f2a0-8888-7000-8000-000000000001","environment":"production","managedDatabaseId":"0199f2a0-8888-7000-8000-000000000002","provider":"supabase","releaseId":null,
         "releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","gitSha":"abcdef0123456789","status":"failed","files":[{"version":"1","name":"init","ms":3},{"version":"2","name":"tags","ms":4}],
         "totalMs":7,"errorCode":"MIGRATION_FAILED","errorMessage":"boom","advisorFindings":[],"startedAt":"2026-10-17T10:00:00Z","finishedAt":"2026-10-17T10:00:01Z"}
        """;

    private const string DeploymentSettings = """
        {"targets":[],"env":{"publicPrefix":null,"variables":[]},"canManage":true,"fakeMode":false,"originModel":{"model":null,"packModel":null,"options":[],"explanation":null},
         "databases":[{"environment":"preview","provider":"render","status":"ready","hostOption":"render-postgres","inComposition":true,"needsConnection":false,"binding":null,"canBind":true,
           "resourceId":"r1","name":"todo-preview","region":"oregon","plan":"free","expiresAt":"2026-11-01T00:00:00Z","paused":true,"canRestore":false,"resourceUrl":"https://render.test/r1",
           "connectionSummary":"host only","missingPermissions":[],"provisioning":null,"warnings":["Expires soon."],"updatedAt":null}]}
        """;

    private const string StateWithUrls = """
        {"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"execution","status":"building","checkpoint":4,"releasedVersion":null,"developmentVersion":"0.1.0","executionPaused":false,
         "artifacts":[],"execution":{"total":0,"pending":0,"inProgress":0,"completed":0,"rejected":0,"blocked":0},
         "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
         "pendingGates":[],"tokensUsed":0,"tokenBudget":null,"executionStatus":"idle","pullRequest":null,"latestRelease":null,"latestDeployment":null,
         "productionUrls":[{"component":"frontend","provider":"netlify","url":"https://todo.netlify.app","deploymentId":null},{"component":"backend","provider":"render","url":null,"deploymentId":null}]}
        """;

    private const string StartedVersion = """
        {"releaseVersion":{"id":"0199f2a0-6666-7000-8000-000000000001","version":"1.3.0","status":"in_development","tag":null,"baseTag":null,"branchName":null,"pullRequestUrl":null,
          "checkpoints":{"current":1,"max":1},"presetId":null,"presetVersion":null,"presetName":null,"startedBy":null,"createdAt":"2026-10-17T10:00:00Z","releasedAt":null},
         "state":{"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"refinement","status":"active","checkpoint":1,"releasedVersion":null,"developmentVersion":"1.3.0","executionPaused":false,
          "artifacts":[],"execution":{"total":0,"pending":0,"inProgress":0,"completed":0,"rejected":0,"blocked":0},
          "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
          "pendingGates":[],"tokensUsed":0,"tokenBudget":null,"executionStatus":"idle","pullRequest":null,"latestRelease":null}}
        """;

    private readonly CliTestHarness _cli = new();

    public Fr156StackCommandTests() => _cli.LoggedIn();

    public void Dispose() => _cli.Dispose();

    private static void AssertJson(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    private static HttpResponseMessage Incompatible(string rule, string layer, string option, string reason) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            $$"""
            {"title":"These stack choices don't work together: {{reason}}.","status":400,"errorCode":"STACK_INCOMPATIBLE","traceId":"t",
             "details":["{{rule}}: {{reason}}"],"violations":[{"layer":"{{layer}}","option":"{{option}}","rule":"{{rule}}","reason":"{{reason}}"}]}
            """,
            Encoding.UTF8,
            "application/problem+json"),
    };

    /// <summary>GET /stack answers <paramref name="current"/>; every other request answers <paramref name="other"/>.</summary>
    private void RespondWithStack(string current, Func<HttpResponseMessage> other) =>
        _cli.Respond(r => r.Method == HttpMethod.Get ? FakeHttpHandler.Json(HttpStatusCode.OK, current) : other());

    // --- aide stack set: the spec's worked example and the exit codes ---

    [Fact]
    public async Task FR156_stack_set_with_an_incompatible_composition_exits_4_and_prints_the_R3_violation()
    {
        RespondWithStack(EmptyStack, () => Incompatible("R3", "data", "data-prisma", "Prisma needs Express, NestJS or Next.js full-stack"));

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--backend", "be-aspnet", "--data", "data-prisma");

        Assert.Equal(4, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Contains("[STACK_INCOMPATIBLE]", run.Error, StringComparison.Ordinal);
        Assert.Contains("- R3 (data: data-prisma): Prisma needs Express, NestJS or Next.js full-stack", run.Error, StringComparison.Ordinal);
        Assert.Equal(3, run.Error.Split("Prisma needs Express").Length); // once in the title, once in the violation: details aren't repeated
        var put = _cli.Requests[^1];
        Assert.Equal((HttpMethod.Put, StackPath), (put.Method, put.Uri.AbsolutePath));
        AssertJson("""{"backend":{"pack":"be-aspnet"},"data":{"pack":"data-prisma"}}""", put.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "STACK_LOCKED", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "STACK_INCOMPATIBLE", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "VCS_MISMATCH", ExitCodes.Error)]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION", ExitCodes.Error)]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHORIZED", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", ExitCodes.Error)]
    public async Task FR156_stack_set_errors_follow_the_exit_codes(HttpStatusCode status, string errorCode, int expected)
    {
        RespondWithStack(Stack, () => FakeHttpHandler.Problem(status, errorCode, "Failed"));

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--backend", "be-nestjs");

        Assert.Equal(expected, run.ExitCode);
        Assert.Contains($"[{errorCode}]", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STACK_INCOMPATIBLE")]
    [InlineData("STACK_LOCKED")]
    [InlineData("LLM_PROVIDER_UNAVAILABLE")]
    public void FR156_stack_and_llm_error_codes_exit_4_whatever_the_status(string errorCode)
    {
        Assert.Equal(ExitCodes.Conflict, ExitCodes.ForProblem(HttpStatusCode.BadRequest, errorCode));
        Assert.Equal(ExitCodes.Conflict, ExitCodes.ForProblem(HttpStatusCode.Conflict, errorCode));
    }

    // --- aide stack set: partial flags merge with the current composition ---

    [Fact]
    public async Task FR156_stack_set_keeps_the_layers_it_does_not_name_with_their_paths_instance_and_origin()
    {
        RespondWithStack(Stack, () => FakeHttpHandler.Json(HttpStatusCode.OK, Stack));

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--database-host", "render-postgres");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal([HttpMethod.Get, HttpMethod.Put], _cli.Requests.Select(r => r.Method));
        AssertJson(
            $$"""
            {"frontend":{"pack":"fe-react-vite","path":"web"},"backend":{"pack":"be-express","path":"api"},"data":{"pack":"data-supabase","mode":"baas"},
             "vcs":{"pack":"vcs-gitlab","instanceId":"{{InstanceId}}","selfManaged":true},
             "deploy":{"frontendHost":"deploy-netlify","backendHost":"deploy-render","databaseHost":"render-postgres","local":"none"},"originModel":"proxy"}
            """,
            _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR156_stack_set_gives_a_changed_layer_its_defaults_and_resets_the_origin_model()
    {
        RespondWithStack(Stack, () => FakeHttpHandler.Json(HttpStatusCode.OK, Stack));

        var run = await _cli.RunAsync(
            "stack", "set", Samples.ProjectId, "--backend", "be-nestjs", "--data", "data-prisma", "--vcs", "vcs-github", "--frontend-host", "none");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """
            {"frontend":{"pack":"fe-react-vite","path":"web"},"backend":{"pack":"be-nestjs"},"data":{"pack":"data-prisma"},"vcs":{"pack":"vcs-github"},
             "deploy":{"frontendHost":"none","backendHost":"deploy-render","local":"none"}}
            """,
            _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR156_stack_set_warns_when_the_origin_model_changes()
    {
        var after = Stack.Replace("\"originModel\":\"proxy\"", "\"originModel\":\"cross-origin\"", StringComparison.Ordinal);
        RespondWithStack(Stack, () => FakeHttpHandler.Json(HttpStatusCode.OK, after));

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--backend-host", "deploy-railway");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("warning: the origin model changed from 'proxy' to 'cross-origin'.", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR156_stack_set_keeping_the_origin_model_prints_no_warning()
    {
        RespondWithStack(Stack, () => FakeHttpHandler.Json(HttpStatusCode.OK, Stack));

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--database-host", "render-postgres");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.DoesNotContain("origin model", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR156_stack_set_sends_every_flag_and_prints_the_new_stack()
    {
        RespondWithStack(EmptyStack, () => FakeHttpHandler.Json(HttpStatusCode.OK, Stack));

        var run = await _cli.RunAsync(
            "stack", "set", Samples.ProjectId,
            "--frontend", "fe-vue", "--backend", "none", "--data", "data-supabase:baas", "--vcs", "vcs-bitbucket",
            "--frontend-host", "deploy-vercel", "--backend-host", "none", "--database-host", "none", "--local", "compose");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """
            {"frontend":{"pack":"fe-vue"},"backend":{"pack":"none"},"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-bitbucket"},
             "deploy":{"frontendHost":"deploy-vercel","backendHost":"none","databaseHost":"none","local":"compose"}}
            """,
            _cli.Requests[1].Body);
        Assert.Matches(@"Frontend:\s+fe-react-vite at web", run.Out);
    }

    [Fact]
    public async Task FR156_stack_set_sends_the_host_data_mode_and_prints_the_api_refusal()
    {
        RespondWithStack(EmptyStack, () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"title":"Invalid","status":400,"errorCode":"VALIDATION","errors":{"data.mode":["must be 'baas' for Supabase (BaaS) (Supabase host mode is chosen as the database host)."]}}""",
                Encoding.UTF8,
                "application/problem+json"),
        });

        var run = await _cli.RunAsync("stack", "set", Samples.ProjectId, "--data", "data-supabase:host");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        AssertJson("""{"data":{"pack":"data-supabase","mode":"host"}}""", _cli.Requests[1].Body);
        Assert.Contains("Supabase host mode is chosen as the database host", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stack", "set", Samples.ProjectId)]
    [InlineData("stack", "set", "not-a-guid", "--backend", "be-aspnet")]
    [InlineData("stack", "set", Samples.ProjectId, "--local", "docker")]
    [InlineData("stack", "set", Samples.ProjectId, "--data", "data-supabase:other")]
    [InlineData("stack", "set", Samples.ProjectId, "--data", ":baas")]
    [InlineData("stack", "set", Samples.ProjectId, "--backend", " ")]
    [InlineData("stack", "show")]
    [InlineData("stack", "show", "not-a-guid")]
    [InlineData("stack", "detect")]
    [InlineData("stack", "detect", "acme/todo-app")]
    [InlineData("stack", "detect", "https://github.com/acme")]
    [InlineData("stack", "detect", "https://git.example.com/acme/todo-app")]
    [InlineData("stack", "detect", "https://github.com/acme/todo-app", "--provider", "svn")]
    [InlineData("stack")]
    [InlineData("deployments", Samples.ProjectId, "--component", "database")]
    [InlineData("deployments", Samples.ProjectId, "--env", "staging")]
    [InlineData("databases")]
    [InlineData("databases", "not-a-guid")]
    [InlineData("migrations", "not-a-guid")]
    public async Task FR156_bad_arguments_exit_2_without_calling_the_api(params string[] args)
    {
        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    // --- aide stack catalog / show ---

    [Fact]
    public async Task FR156_stack_catalog_lists_layers_and_options()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Catalog));

        var run = await _cli.RunAsync("stack", "catalog");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("/api/stack/catalog", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Contains("Stack catalog 2026.10.1", run.Out, StringComparison.Ordinal);
        Assert.Contains("Frontend (frontend)", run.Out, StringComparison.Ordinal);
        Assert.Contains("fe-nextjs", run.Out, StringComparison.Ordinal);
        Assert.Matches(@"data-supabase\s+Supabase \(BaaS\)\s+baas, host", run.Out);
    }

    [Fact]
    public async Task FR156_stack_catalog_json_prints_the_api_response()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Catalog));

        var run = await _cli.RunAsync("stack", "catalog", "--json");

        AssertJson(Catalog, run.Out);
    }

    [Fact]
    public async Task FR156_stack_show_prints_each_layer_the_lock_the_caveats_and_the_retired_preset()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Stack));

        var run = await _cli.RunAsync("stack", "show", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(StackPath, Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Matches(@"Locked:\s+yes \(plan_locked\)", run.Out);
        Assert.Matches(@"Backend:\s+be-express at api", run.Out);
        Assert.Matches(@"Data:\s+data-supabase \(baas\)", run.Out);
        Assert.Matches(@"VCS:\s+vcs-gitlab", run.Out);
        Assert.Matches(@"Database host:\s+none", run.Out);
        Assert.Contains("Caveat R9 (frontendHost: deploy-netlify): Netlify needs a deploy key.", run.Out, StringComparison.Ordinal);
        Assert.Contains("Preset p@1 (retired, migrated)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR156_stack_show_without_a_composition_says_so()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, EmptyStack));

        var run = await _cli.RunAsync("stack", "show", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("No stack composition is pinned", run.Out, StringComparison.Ordinal);
    }

    // --- aide stack detect ---

    [Theory]
    [InlineData("https://github.com/acme/todo-app", "github_app", "acme/todo-app")]
    [InlineData("https://github.com/acme/todo-app.git", "github_app", "acme/todo-app")]
    [InlineData("https://gitlab.com/acme/web/todo-app/", "gitlab", "acme/web/todo-app")]
    [InlineData("https://bitbucket.org/acme/todo-app", "bitbucket", "acme/todo-app")]
    public async Task FR156_stack_detect_takes_the_provider_and_repository_from_the_url(string url, string provider, string repo)
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Detection));

        var run = await _cli.RunAsync("stack", "detect", url);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        Assert.Equal((HttpMethod.Post, "/api/stack/detect"), (post.Method, post.Uri.AbsolutePath));
        AssertJson($$$"""{"git":{"provider":"{{{provider}}}","repo":"{{{repo}}}"}}""", post.Body);
    }

    [Fact]
    public async Task FR156_stack_detect_prints_suggestions_conflicts_and_truncation()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Detection));

        var run = await _cli.RunAsync("stack", "detect", "https://github.com/acme/todo-app");

        Assert.Contains("Detected at main (0123456)", run.Out, StringComparison.Ordinal);
        Assert.Matches(@"frontend\s+fe-vue\s+frontend\s+-\s+frontend/package\.json \(vue, vite\)", run.Out);
        Assert.Contains("Conflict in backend (choose one): be-express at api, be-nestjs at svc", run.Out, StringComparison.Ordinal);
        Assert.Contains("too many files", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR156_stack_detect_self_managed_gitlab_names_the_provider_instance_connection_and_ref()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Detection));

        var run = await _cli.RunAsync(
            "stack", "detect", "https://git.example.com/acme/todo-app", "--provider", "gitlab", "--instance", InstanceId, "--connection", InstanceId, "--ref", "develop");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            $$$"""{"git":{"provider":"gitlab","instanceId":"{{{InstanceId}}}","repo":"acme/todo-app","ref":"develop","connectionId":"{{{InstanceId}}}"}}""",
            Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR156_stack_detect_github_pat_reads_the_token_from_the_environment_and_only_for_that_provider()
    {
        _cli.Environment["AIDE_GIT_TOKEN"] = "ghp_secret";
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Detection));

        await _cli.RunAsync("stack", "detect", "acme/todo-app", "--provider", "github_pat");
        await _cli.RunAsync("stack", "detect", "https://github.com/acme/todo-app");

        AssertJson("""{"git":{"provider":"github_pat","repo":"acme/todo-app","token":"ghp_secret"}}""", _cli.Requests[0].Body);
        AssertJson("""{"git":{"provider":"github_app","repo":"acme/todo-app"}}""", _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR156_stack_detect_local_passes_the_directory_as_it_is()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Detection));

        await _cli.RunAsync("stack", "detect", "todo-app", "--provider", "local");

        AssertJson("""{"git":{"provider":"local","repo":"todo-app"}}""", Assert.Single(_cli.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "GIT_NOT_CONNECTED", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", ExitCodes.Error)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.BadGateway, "UPSTREAM_ERROR", ExitCodes.Error)]
    public async Task FR156_stack_detect_errors_follow_the_exit_codes(HttpStatusCode status, string errorCode, int expected)
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Failed"));

        var run = await _cli.RunAsync("stack", "detect", "https://github.com/acme/todo-app");

        Assert.Equal(expected, run.ExitCode);
    }

    // --- aide version start ---

    [Fact]
    public async Task FR156_version_start_merges_the_stack_flags_into_the_current_composition()
    {
        RespondWithStack(Stack, () => FakeHttpHandler.Json(HttpStatusCode.Created, StartedVersion));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "-r", "Add tags", "--backend", "be-nestjs");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(StackPath, _cli.Requests[0].Uri.AbsolutePath);
        AssertJson(
            $$$"""
            {"version":"1.3.0","request":"Add tags","stack":{"frontend":{"pack":"fe-react-vite","path":"web"},"backend":{"pack":"be-nestjs"},"data":{"pack":"data-supabase","mode":"baas"},
             "vcs":{"pack":"vcs-gitlab","instanceId":"{{{InstanceId}}}","selfManaged":true},"deploy":{"frontendHost":"deploy-netlify","backendHost":"deploy-render","local":"none"} } }
            """,
            _cli.Requests[1].Body);
    }

    [Fact]
    public void FR156_Origin_model_warnings_for_version_start_and_stack_set()
    {
        Assert.Equal("warning: the origin model 'proxy' was reset to the default for this stack.", Aide.Cli.Commands.StackFlags.OriginChange("proxy", null));
        Assert.Null(Aide.Cli.Commands.StackFlags.OriginChange("proxy", "proxy"));
        Assert.Null(Aide.Cli.Commands.StackFlags.OriginChange(null, "proxy"));
    }

    [Fact]
    public async Task FR156_version_start_accepts_the_stack_prefixed_flags()
    {
        RespondWithStack(EmptyStack, () => FakeHttpHandler.Json(HttpStatusCode.Created, StartedVersion));

        var run = await _cli.RunAsync(
            "version", "start", Samples.ProjectId, "1.3.0", "-r", "Add tags",
            "--stack-frontend", "fe-vue", "--stack-vcs", "vcs-github", "--stack-frontend-host", "deploy-vercel", "--stack-data", "data-supabase:baas", "--stack-local", "none");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """
            {"version":"1.3.0","request":"Add tags","stack":{"frontend":{"pack":"fe-vue"},"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-github"},
             "deploy":{"frontendHost":"deploy-vercel","local":"none"}}}
            """,
            _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR156_version_start_incompatible_stack_exits_4_and_prints_the_violation()
    {
        RespondWithStack(EmptyStack, () => Incompatible("R9", "frontendHost", "deploy-cloudflare", "Cloudflare can't build Bitbucket repos"));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "-r", "x", "--frontend-host", "deploy-cloudflare");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("- R9 (frontendHost: deploy-cloudflare): Cloudflare can't build Bitbucket repos", run.Error, StringComparison.Ordinal);
    }

    // --- Listings ---

    [Theory]
    [InlineData("?environment=preview&component=frontend", "--component", "frontend", "--env", "preview")]
    [InlineData("?component=backend", "--component", "backend")]
    [InlineData("?environment=production", "--env", "production")]
    [InlineData("")]
    public async Task FR156_deployments_filters_by_component_and_environment(string query, params string[] flags)
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "[]"));

        var run = await _cli.RunAsync(["deployments", Samples.ProjectId, .. flags]);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/deployments{query}", Assert.Single(_cli.Requests).Uri.PathAndQuery);
    }

    [Fact]
    public async Task FR156_deployments_shows_the_component()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """
            [{"id":"0199f2a0-7777-7000-8000-000000000001","target":"production","provider":"render","status":"ready","url":"https://api.test","gitRef":"main","gitSha":"0123456789",
              "releaseVersion":"1.2.0","errorCode":null,"trigger":"manual","triggeredBy":null,"createdAt":"2026-10-17T10:00:00Z","readyAt":null,"component":"backend"}]
            """));

        var run = await _cli.RunAsync("deployments", Samples.ProjectId);

        Assert.Matches(@"COMPONENT", run.Out);
        Assert.Matches(@"production\s+backend\s+1\.2\.0", run.Out);
    }

    [Fact]
    public async Task FR156_databases_lists_each_environment_and_its_warnings()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, DeploymentSettings));

        var run = await _cli.RunAsync("databases", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/settings/deployment", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Matches(@"preview\s+render\s+ready \(paused\)\s+todo-preview\s+oregon\s+free\s+2026-11-01 00:00:00Z\s+https://render\.test/r1", run.Out);
        Assert.Contains("preview: Expires soon.", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR156_databases_json_prints_only_the_databases()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, DeploymentSettings));

        var run = await _cli.RunAsync("databases", Samples.ProjectId, "--json");

        AssertJson(JsonNode.Parse(DeploymentSettings)!["databases"]!.ToJsonString(), run.Out);
    }

    [Fact]
    public async Task FR156_databases_with_none_says_so()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"databases":[]}"""));

        var run = await _cli.RunAsync("databases", Samples.ProjectId);

        Assert.Equal((ExitCodes.Success, "No databases."), (run.ExitCode, run.Out.Trim()));
    }

    [Fact]
    public async Task FR156_migrations_lists_each_run()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, $"[{MigrationRun}]"));

        var run = await _cli.RunAsync("migrations", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/migrations", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Matches(@"production\s+supabase\s+abcdef0\s+failed \(MIGRATION_FAILED\)\s+2\s+7 ms\s+2026-10-17 10:00:00Z", run.Out);
    }

    [Fact]
    public async Task FR156_migrations_with_none_says_so()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "[]"));

        var run = await _cli.RunAsync("migrations", Samples.ProjectId);

        Assert.Equal((ExitCodes.Success, "No migration runs."), (run.ExitCode, run.Out.Trim()));
    }

    // --- aide status ---

    private void RespondWithStatus(string migrations) => _cli.Respond(r =>
    {
        var path = r.RequestUri!.AbsolutePath;
        return FakeHttpHandler.Json(
            HttpStatusCode.OK,
            path.EndsWith("/state", StringComparison.Ordinal) ? StateWithUrls
            : path.EndsWith("/stack", StringComparison.Ordinal) ? Stack
            : path.EndsWith("/migrations", StringComparison.Ordinal) ? migrations
            : Samples.Project);
    });

    [Fact]
    public async Task FR156_status_includes_the_composition_production_urls_and_latest_migration()
    {
        RespondWithStatus($"[{MigrationRun}]");

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Stack:\s+frontend fe-react-vite, backend be-express, data data-supabase \(baas\), vcs vcs-gitlab, frontend host deploy-netlify, backend host deploy-render, database host none, local none", run.Out);
        Assert.Matches(@"Production frontend:\s+https://todo\.netlify\.app \(netlify\)", run.Out);
        Assert.Matches(@"Production backend:\s+- \(render\)", run.Out);
        Assert.Matches(@"Latest migration:\s+production failed \(MIGRATION_FAILED\) abcdef0, 2 files, 7 ms \(2026-10-17 10:00:00Z\)", run.Out);
    }

    [Fact]
    public async Task FR156_status_with_nothing_deployed_shows_dashes()
    {
        _cli.Respond(r => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.StatusBody(r, Samples.State, Samples.Project)));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Stack:\s+not chosen", run.Out);
        Assert.Matches(@"Production URLs:\s+-", run.Out);
        Assert.Matches(@"Latest migration:\s+-", run.Out);
    }

    [Fact]
    public async Task FR156_status_json_adds_the_stack_and_the_latest_migration_run_to_the_state()
    {
        RespondWithStatus($"[{MigrationRun},{MigrationRun}]");

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var output = JsonNode.Parse(run.Out)!;
        Assert.Equal("execution", output["phase"]!.GetValue<string>());
        Assert.Equal("https://todo.netlify.app", output["productionUrls"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("fe-react-vite", output["stack"]!["composition"]!["frontend"]!["pack"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MigrationRun), output["latestMigrationRun"]));
    }

    [Fact]
    public async Task FR156_status_json_without_runs_has_a_null_latest_migration_run()
    {
        RespondWithStatus("[]");

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json");

        var output = JsonNode.Parse(run.Out)!.AsObject();
        Assert.True(output.ContainsKey("latestMigrationRun"));
        Assert.Null(output["latestMigrationRun"]);
    }

    [Fact]
    public async Task FR156_help_lists_the_new_commands_and_the_exit_code_4_errors()
    {
        var root = await _cli.RunAsync("--help");
        var set = await _cli.RunAsync("stack", "set", "--help");

        foreach (var command in new[] { "stack", "databases", "migrations" })
        {
            Assert.Contains(command, root.Out, StringComparison.Ordinal);
        }

        Assert.Contains("STACK_INCOMPATIBLE", root.Out, StringComparison.Ordinal);
        Assert.Contains("STACK_LOCKED", root.Out, StringComparison.Ordinal);
        Assert.Contains("LLM_PROVIDER_UNAVAILABLE", root.Out, StringComparison.Ordinal);
        foreach (var flag in new[] { "--frontend", "--backend", "--data", "--vcs", "--frontend-host", "--backend-host", "--database-host", "--local" })
        {
            Assert.Contains(flag, set.Out, StringComparison.Ordinal);
        }
    }
}
