using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-194: `aide analysis …`, `aide sources …`, `aide deployments groups`, `--infer-stack`, `status` and the new exit-4 codes.</summary>
public sealed class Fr194CommandTests : IDisposable
{
    private const string ProfileId = "0199f2a0-5555-7000-8000-000000000005";
    private const string ImportId = "0199f2a0-4444-7000-8000-000000000004";
    private const string ConnectionId = "0199f2a0-9999-7000-8000-000000000009";
    private const string AnalysisPath = $"/api/projects/{Samples.ProjectId}/analysis";
    private const string SourcesPath = $"/api/projects/{Samples.ProjectId}/sources";

    private const string Layers = """
        "layers":{
          "frontend":{"option":"fe-react-vite","path":".","mode":null,"status":"inferred","confidence":"medium","technology":null,"evidence":[],"candidates":[]},
          "backend":{"option":null,"path":null,"mode":null,"status":"unknown","confidence":null,"technology":null,"evidence":[],"candidates":[]},
          "data":{"option":null,"path":null,"mode":null,"status":"none","confidence":"medium","technology":null,"evidence":[],"candidates":[]},
          "vcs":{"option":"vcs-github","path":null,"mode":null,"status":"fixed","confidence":"high","technology":null,"evidence":[],"candidates":[]}}
        """;

    private const string Analysis = $$"""
        {"releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","releaseVersion":"0.1.0","phase":"analysis","stackSource":"infer","hasExistingCode":true,
         "baseCommitSha":"0123456789abcdef","summary":{"status":"locked","runId":null,"artifactId":null,"artifactVersion":1},
         "run":{"jobId":"0199f2a0-7777-7000-8000-000000000007","runId":null,"status":"succeeded","trigger":"phase_entered","errorCode":null,"error":null,"warnings":[],
                "artifactId":"{{ProfileId}}","artifactVersion":1,"createdAt":"2026-11-01T10:00:00Z","completedAt":"2026-11-01T10:01:00Z"},
         "profile":{"artifactId":"{{ProfileId}}","version":1,"status":"draft","integrity":"ok","authoredByAgent":true,"agentRunId":null,"createdAt":"2026-11-01T10:01:00Z",
                    "narrative":"n","document":{"schemaVersion":1,"baseCommitSha":"0123456789abcdef","generalist":false,{{Layers}},
                    "hosts":{"frontendHost":null,"backendHost":null,"databaseHost":null,"local":null,"originModel":null,"separateParts":[],"detected":[]},"structure":[],"questions":[]},"errors":[]},
         "needsInput":[{"layer":"backend","status":"unknown","question":"Is there a backend?","technology":null,"candidates":[]}],
         "unresolvedLayers":["backend"],"canApprove":false,"approveBlockedReason":"Answer these before approving: backend.","canContinue":false,
         "suggested":null,"selection":null,"composition":null,"previousComposition":null,"deployment":{"available":false,"reason":"Approve the profile first."} }
        """;

    private const string Artifact = $$"""
        {"id":"{{ProfileId}}","type":"stack_profile","version":1,"parentVersion":null,"content":"x","contentHash":"h","status":"locked","checkpointAtCreation":1,
         "author":null,"approvedBy":null,"approvedAt":null,"rejectionComment":null,"createdAt":"2026-11-01T10:01:00Z","integrity":"ok"}
        """;

    private const string Import = $$"""
        {"id":"{{ImportId}}","releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","provider":"jira","connectionId":"{{ConnectionId}}","status":"completed",
         "errorCode":null,"errorMessage":null,"selection":{"keys":["FAKE-1","FAKE-2"],"query":null,"containerId":"FAKE","filter":null},
         "options":{"includeChildren":true,"includeComments":false,"includeAssignees":false},"itemCount":2,"renderedBytes":300,"items":[],
         "sourceArtifactId":"0199f2a0-8888-7000-8000-000000000008","createdBy":{"id":null,"name":"Ada","isSystem":false},"createdAt":"2026-11-01T10:00:00Z","completedAt":"2026-11-01T10:00:05Z"}
        """;

    private const string FailedImport = """
        {"id":"0199f2a0-4444-7000-8000-000000000044","releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","provider":"linear","connectionId":null,"status":"failed",
         "errorCode":"TRACKER_IMPORT_FAILED","errorMessage":"boom","selection":{"keys":null,"query":null,"containerId":"team:ENG","filter":null},
         "options":{"includeChildren":true,"includeComments":false,"includeAssignees":false},"itemCount":0,"renderedBytes":0,"items":[],
         "sourceArtifactId":null,"createdBy":{"id":null,"name":"Ada","isSystem":false},"createdAt":"2026-11-01T09:00:00Z","completedAt":"2026-11-01T09:00:05Z"}
        """;

    private const string Sources = $$"""
        {"releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","imports":[{{Import}},{{FailedImport}}],
         "currentSnapshot":{"artifactId":"0199f2a0-8888-7000-8000-000000000008","version":1,"contentHash":"abcdef0123456789","createdAt":"2026-11-01T10:00:05Z"},
         "draft":null,"canChange":true}
        """;

    private const string AnalysisState = """
        {"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"analysis","status":"active","checkpoint":1,"releasedVersion":null,"developmentVersion":"0.1.0","executionPaused":false,
         "artifacts":[],"execution":{"total":0,"pending":0,"inProgress":0,"completed":0,"rejected":0,"blocked":0},
         "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
         "pendingGates":[],"tokensUsed":0,"tokenBudget":null,"executionStatus":"idle","pullRequest":null,"latestRelease":null}
        """;

    private const string DeploymentSettings = """
        {"targets":[],"databases":[],"canManage":true,"fakeMode":false,
         "groups":[{"id":"0199f2a0-3333-7000-8000-000000000033","provider":"railway","capability":"project","parts":["frontend","backend","database"],"name":"todo",
           "binding":{"connectionId":"0199f2a0-9999-7000-8000-000000000009","label":"Work","last4":"ab12","accountName":"acme","boundBy":{"id":"0199f2a0-0000-7000-8000-000000000000","name":"Ada"},"status":"valid"},
           "canBind":true,"sharedResourceId":"p1","sharedResourceUrl":"https://railway.test/p1","region":null,"plan":null,"status":"error","statusReason":"PLATFORM_ERROR",
           "partStatuses":[{"part":"frontend","environment":null,"status":"error","statusReason":"PLATFORM_ERROR","resourceId":null},{"part":"backend","environment":null,"status":"ready","statusReason":null,"resourceId":"s1"}],
           "canRetry":true,"updatedAt":null}]}
        """;

    private readonly CliTestHarness _cli = new();

    public Fr194CommandTests() => _cli.LoggedIn();

    public void Dispose() => _cli.Dispose();

    private static void AssertJson(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    private static HttpResponseMessage Incomplete() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(
            """{"title":"Answer these before approving: backend.","status":409,"errorCode":"STACK_PROFILE_INCOMPLETE","traceId":"t","details":["backend"]}""",
            Encoding.UTF8,
            "application/problem+json"),
    };

    /// <summary>GET /analysis answers the analysis; everything else <paramref name="other"/>.</summary>
    private void RespondWithAnalysis(Func<HttpRequestMessage, HttpResponseMessage> other, string analysis = Analysis) =>
        _cli.Respond(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == AnalysisPath ? FakeHttpHandler.Json(HttpStatusCode.OK, analysis) : other(r));

    // --- aide analysis approve: the spec's example ---

    [Fact]
    public async Task FR194_analysis_approve_with_an_unresolved_layer_exits_4_and_lists_the_layer()
    {
        RespondWithAnalysis(_ => Incomplete());

        var run = await _cli.RunAsync("analysis", "approve", Samples.ProjectId);

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Contains("[STACK_PROFILE_INCOMPLETE]", run.Error, StringComparison.Ordinal);
        Assert.Contains("- backend", run.Error, StringComparison.Ordinal);
        var post = _cli.Requests[^1];
        Assert.Equal((HttpMethod.Post, $"/api/projects/{Samples.ProjectId}/artifacts/{ProfileId}/approve"), (post.Method, post.Uri.AbsolutePath));
    }

    [Fact]
    public async Task FR194_analysis_approve_posts_the_comment_and_generalist_to_the_latest_draft()
    {
        RespondWithAnalysis(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Artifact));

        var run = await _cli.RunAsync("analysis", "approve", Samples.ProjectId, "--comment", "ok", "--generalist");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Approved stack profile v1", run.Out, StringComparison.Ordinal);
        AssertJson("""{"comment":"ok","generalist":true}""", _cli.Requests[^1].Body);
    }

    [Fact]
    public async Task FR194_analysis_reject_posts_the_comment_and_says_the_analyst_runs_again()
    {
        RespondWithAnalysis(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Artifact));

        var run = await _cli.RunAsync("analysis", "reject", Samples.ProjectId, "--comment", "It's Django");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = _cli.Requests[^1];
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/{ProfileId}/reject", post.Uri.AbsolutePath);
        AssertJson("""{"comment":"It's Django"}""", post.Body);
        Assert.Contains("the Stack Analyst runs again", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_analysis_approve_without_a_draft_exits_1_without_posting()
    {
        var locked = JsonNode.Parse(Analysis)!.AsObject();
        locked["profile"]!["status"] = "locked";
        RespondWithAnalysis(_ => throw new InvalidOperationException("No decision should be posted."), locked.ToJsonString());

        var run = await _cli.RunAsync("analysis", "approve", Samples.ProjectId);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("not a draft", run.Error, StringComparison.Ordinal);
        Assert.Single(_cli.Requests);
    }

    [Fact]
    public async Task FR194_analysis_show_lists_what_needs_input_the_layers_and_why_approve_is_blocked()
    {
        RespondWithAnalysis(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var run = await _cli.RunAsync("analysis", "show", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Version:\s+0\.1\.0 \(phase analysis, stack inferred\)", run.Out);
        Assert.Matches(@"Profile:\s+v1 draft \(by the Stack Analyst\)", run.Out);
        Assert.Matches(@"Approve:\s+no: Answer these before approving: backend\.", run.Out);
        Assert.Contains("backend (unknown): Is there a backend?", run.Out, StringComparison.Ordinal);
        Assert.Matches(@"frontend\s+fe-react-vite\s+\.\s+-\s+inferred\s+medium", run.Out);
    }

    [Fact]
    public async Task FR194_analysis_show_json_prints_the_api_response()
    {
        RespondWithAnalysis(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var run = await _cli.RunAsync("analysis", "show", Samples.ProjectId, "--json");

        AssertJson(Analysis, run.Out);
    }

    [Fact]
    public async Task FR194_analysis_retry_queues_the_Stack_Analyst()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, """{"jobId":"0199f2a0-7777-7000-8000-000000000077","trigger":"retry","queuedAt":"2026-11-01T10:00:00Z"}"""));

        var run = await _cli.RunAsync("analysis", "retry", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        Assert.Equal((HttpMethod.Post, $"{AnalysisPath}/run"), (post.Method, post.Uri.AbsolutePath));
        Assert.Contains("0199f2a0-7777-7000-8000-000000000077", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "WRONG_PHASE", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "BUDGET_EXCEEDED", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", ExitCodes.Error)]
    public async Task FR194_analysis_retry_errors_follow_the_exit_codes(HttpStatusCode status, string errorCode, int expected)
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Failed"));

        var run = await _cli.RunAsync("analysis", "retry", Samples.ProjectId);

        Assert.Equal(expected, run.ExitCode);
        Assert.Contains($"[{errorCode}]", run.Error, StringComparison.Ordinal);
    }

    // --- exit codes ---

    [Theory]
    [InlineData("STACK_PROFILE_INCOMPLETE")]
    [InlineData("STACK_INFER_NO_CODE")]
    [InlineData("SOURCE_ITEMS_TOO_LARGE")]
    public void FR194_new_error_codes_exit_4_whatever_the_status(string errorCode)
    {
        Assert.Equal(ExitCodes.Conflict, ExitCodes.ForProblem(HttpStatusCode.BadRequest, errorCode));
        Assert.Equal(ExitCodes.Conflict, ExitCodes.ForProblem(HttpStatusCode.Conflict, errorCode));
    }

    // --- --infer-stack ---

    [Fact]
    public async Task FR194_project_create_infer_stack_sends_stackSource_infer_with_the_import()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project.Replace("\"currentPhase\":\"refinement\"", "\"currentPhase\":\"analysis\"", StringComparison.Ordinal)));

        var run = await _cli.RunAsync(
            "project", "create", "--name", "Todo", "--idea", "Add tags", "--no-input", "--infer-stack",
            "--import-version", "1.1.0", "--released-tag", "v1.0.0", "--local-path", "todo");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """{"name":"Todo","idea":"Add tags","git":{"provider":"local","localSourcePath":"todo"},"stackSource":"infer","source":"import","import":{"developmentVersion":"1.1.0","releasedTag":"v1.0.0"}}""",
            Assert.Single(_cli.Requests).Body);
        Assert.Contains($"Next: tevrin analysis show {Samples.ProjectId}", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_project_create_infer_stack_on_a_repository_without_code_exits_4()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.BadRequest, "STACK_INFER_NO_CODE", "This repository has no code to analyse. Choose a stack instead."));

        var run = await _cli.RunAsync("project", "create", "--name", "Todo", "--idea", "Add tags", "--no-input", "--infer-stack");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("[STACK_INFER_NO_CODE]", run.Error, StringComparison.Ordinal);
        AssertJson("""{"name":"Todo","idea":"Add tags","git":{"provider":"local"},"stackSource":"infer"}""", Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR194_project_create_import_options_need_import_version()
    {
        var run = await _cli.RunAsync("project", "create", "--name", "Todo", "--idea", "x", "--no-input", "--released-tag", "v1.0.0");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR194_version_start_infer_stack_sends_stackSource_infer()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, """
            {"releaseVersion":{"id":"0199f2a0-6666-7000-8000-000000000001","version":"1.1.0","status":"in_development","tag":null,"baseTag":"v1.0.0","branchName":null,"pullRequestUrl":null,
              "checkpoints":{"current":1,"max":1},"presetId":null,"presetVersion":null,"presetName":null,"startedBy":null,"createdAt":"2026-11-01T10:00:00Z","releasedAt":null},
             "state":{"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"analysis","status":"active","checkpoint":1,"releasedVersion":"1.0.0","developmentVersion":"1.1.0","executionPaused":false,
              "artifacts":[],"execution":{"total":0,"pending":0,"inProgress":0,"completed":0,"rejected":0,"blocked":0},
              "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
              "pendingGates":[],"tokensUsed":0,"tokenBudget":null,"executionStatus":"idle","pullRequest":null,"latestRelease":null}}
            """));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.1.0", "--request", "Move to React", "--infer-stack");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        AssertJson("""{"version":"1.1.0","request":"Move to React","stackSource":"infer"}""", post.Body);
        Assert.Contains("phase analysis", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_version_start_infer_stack_with_stack_options_is_a_usage_error()
    {
        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.1.0", "--request", "x", "--infer-stack", "--frontend", "fe-vue");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    // --- aide sources ---

    [Fact]
    public async Task FR194_sources_list_shows_each_import_and_the_snapshot()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Sources));

        var run = await _cli.RunAsync("sources", "list", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(SourcesPath, Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Matches($@"{ImportId}\s+jira\s+completed\s+2\s+FAKE: FAKE-1,FAKE-2\s+Ada", run.Out);
        Assert.Matches(@"linear\s+failed \(TRACKER_IMPORT_FAILED\)\s+0\s+team:ENG: all matching", run.Out);
        Assert.Contains("Snapshot: source_items v1 (abcdef012345)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_sources_import_with_keys_posts_the_selection_and_comments()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, Import.Replace("\"completed\"", "\"queued\"", StringComparison.Ordinal)));

        var run = await _cli.RunAsync("sources", "import", Samples.ProjectId, "--connection", ConnectionId, "--tracker", "jira", "--keys", "PROJ-1, PROJ-2,,PROJ-1", "--comments");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        Assert.Equal((HttpMethod.Post, SourcesPath), (post.Method, post.Uri.AbsolutePath));
        AssertJson(
            $$"""{"provider":"jira","connectionId":"{{ConnectionId}}","selection":{"keys":["PROJ-1","PROJ-2"]},"options":{"includeChildren":true,"includeComments":true,"includeAssignees":false} }""",
            post.Body);
        Assert.Contains($"Queued import {ImportId} from jira (queued)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_sources_import_with_a_query_and_container()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, Import));

        var run = await _cli.RunAsync("sources", "import", Samples.ProjectId, "--connection", ConnectionId, "--tracker", "github", "--query", "label:mvp", "--container", "acme/todo");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            $$"""{"provider":"github","connectionId":"{{ConnectionId}}","selection":{"query":"label:mvp","containerId":"acme/todo"},"options":{"includeChildren":true,"includeComments":false,"includeAssignees":false} }""",
            Assert.Single(_cli.Requests).Body);
    }

    [Theory]
    [InlineData]
    [InlineData("--keys", "A-1", "--query", "x")]
    [InlineData("--keys", " , ")]
    public async Task FR194_sources_import_needs_exactly_one_of_keys_and_query(params string[] selection)
    {
        var run = await _cli.RunAsync(["sources", "import", Samples.ProjectId, "--connection", ConnectionId, "--tracker", "jira", .. selection]);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR194_sources_import_refuses_an_unknown_tracker()
    {
        var run = await _cli.RunAsync("sources", "import", Samples.ProjectId, "--connection", ConnectionId, "--tracker", "trello", "--keys", "A-1");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "SOURCE_ITEMS_TOO_LARGE", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "WRONG_PHASE", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "TRACKER_NOT_CONNECTED", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Forbidden, "CREDENTIAL_NOT_OWNER", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION", ExitCodes.Error)]
    public async Task FR194_sources_import_errors_follow_the_exit_codes(HttpStatusCode status, string errorCode, int expected)
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Failed"));

        var run = await _cli.RunAsync("sources", "import", Samples.ProjectId, "--connection", ConnectionId, "--tracker", "jira", "--keys", "A-1");

        Assert.Equal(expected, run.ExitCode);
        Assert.Contains($"[{errorCode}]", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_sources_reimport_and_remove_call_the_import()
    {
        _cli.Respond(r => r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : FakeHttpHandler.Json(HttpStatusCode.Accepted, Import));

        var reimport = await _cli.RunAsync("sources", "reimport", Samples.ProjectId, ImportId);
        var remove = await _cli.RunAsync("sources", "remove", Samples.ProjectId, ImportId);

        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (reimport.ExitCode, remove.ExitCode));
        Assert.Equal((HttpMethod.Post, $"{SourcesPath}/{ImportId}/reimport"), (_cli.Requests[0].Method, _cli.Requests[0].Uri.AbsolutePath));
        Assert.Equal((HttpMethod.Delete, $"{SourcesPath}/{ImportId}"), (_cli.Requests[1].Method, _cli.Requests[1].Uri.AbsolutePath));
        Assert.Contains($"Removed import {ImportId}", remove.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_sources_remove_in_a_later_phase_exits_4()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.Conflict, "WRONG_PHASE", "Items can't change now"));

        var run = await _cli.RunAsync("sources", "remove", Samples.ProjectId, ImportId);

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
    }

    [Fact]
    public async Task FR194_sources_check_shows_each_items_drift()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, $$"""
            {"items":[{"importId":"{{ImportId}}","provider":"jira","key":"FAKE-1","title":"Epic","url":"https://t/1","drift":"changed"},
                      {"importId":"{{ImportId}}","provider":"jira","key":"FAKE-2","title":"Story","url":"https://t/2","drift":"unchanged"}],
             "snapshotContentHash":"h","checkedAt":"2026-11-01T10:00:00Z"}
            """));

        var run = await _cli.RunAsync("sources", "check", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal((HttpMethod.Post, $"{SourcesPath}/check"), (Assert.Single(_cli.Requests).Method, _cli.Requests[0].Uri.AbsolutePath));
        Assert.Matches(@"FAKE-1\s+jira\s+changed\s+Epic", run.Out);
        Assert.Contains("1 of 2 changed or removed", run.Out, StringComparison.Ordinal);
    }

    // --- aide deployments groups ---

    [Fact]
    public async Task FR194_deployments_groups_lists_each_group_and_its_parts()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, DeploymentSettings));

        var run = await _cli.RunAsync("deployments", "groups", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/settings/deployment", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Matches(@"railway\s+frontend, backend, database\s+error \(PLATFORM_ERROR\)\s+todo\s+Work \(connected by Ada\)\s+https://railway\.test/p1", run.Out);
        Assert.Contains("railway frontend: error (PLATFORM_ERROR)", run.Out, StringComparison.Ordinal);
        Assert.Contains("retry it in Settings", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR194_deployments_groups_json_prints_only_the_groups_and_none_says_so()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, DeploymentSettings));
        var json = await _cli.RunAsync("deployments", "groups", Samples.ProjectId, "--json");
        AssertJson(JsonNode.Parse(DeploymentSettings)!["groups"]!.ToJsonString(), json.Out);

        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"groups":[]}"""));
        var none = await _cli.RunAsync("deployments", "groups", Samples.ProjectId);
        Assert.Equal((ExitCodes.Success, "No deployment groups."), (none.ExitCode, none.Out.Trim()));
    }

    [Fact]
    public async Task FR194_deployments_without_groups_still_lists_deployments()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "[]"));

        var run = await _cli.RunAsync("deployments", Samples.ProjectId);

        Assert.Equal((ExitCodes.Success, $"/api/projects/{Samples.ProjectId}/deployments"), (run.ExitCode, Assert.Single(_cli.Requests).Uri.AbsolutePath));
    }

    // --- aide status ---

    [Fact]
    public async Task FR194_status_shows_the_analysis_phase_and_source_item_counts()
    {
        _cli.Respond(r => FakeHttpHandler.Json(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/sources", StringComparison.Ordinal)
            ? Sources
            : Samples.StatusBody(r, AnalysisState, Samples.Project)));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Phase:\s+analysis", run.Out);
        Assert.Matches(@"Stack:\s+being inferred: see 'tevrin analysis show'", run.Out); // FR-200
        Assert.Matches(@"Source items:\s+2 items from 1 import \(snapshot v1\), 1 failed", run.Out);
    }

    [Fact]
    public async Task FR194_status_without_imports_says_none_and_json_adds_the_sources()
    {
        _cli.Respond(r => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.StatusBody(r, Samples.State, Samples.Project)));

        var human = await _cli.RunAsync("status", Samples.ProjectId);
        var json = await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Matches(@"Source items:\s+none", human.Out);
        AssertJson(Samples.EmptySources, JsonNode.Parse(json.Out)!["sources"]!.ToJsonString());
    }

    [Fact]
    public async Task FR194_help_lists_the_new_commands_and_exit_code_4_errors()
    {
        var root = await _cli.RunAsync("--help");
        var create = await _cli.RunAsync("project", "create", "--help");
        var start = await _cli.RunAsync("version", "start", "--help");
        var deployments = await _cli.RunAsync("deployments", "--help");

        foreach (var command in new[] { "analysis", "sources" })
        {
            Assert.Contains(command, root.Out, StringComparison.Ordinal);
        }

        foreach (var code in new[] { "STACK_PROFILE_INCOMPLETE", "STACK_INFER_NO_CODE", "SOURCE_ITEMS_TOO_LARGE" })
        {
            Assert.Contains(code, root.Out, StringComparison.Ordinal);
        }

        Assert.Contains("--infer-stack", create.Out, StringComparison.Ordinal);
        Assert.Contains("--infer-stack", start.Out, StringComparison.Ordinal);
        Assert.Contains("groups", deployments.Out, StringComparison.Ordinal);
    }
}
