using System.Net;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-93: the v2 CLI commands (version start, versions, deployments, artifact restore), `status` and the exit codes.</summary>
public sealed class Fr93CommandTests : IDisposable
{
    private const string VersionId = "0199f2a0-6666-7000-8000-000000000001";
    private const string DeploymentId = "0199f2a0-7777-7000-8000-000000000001";

    private readonly CliTestHarness _cli = new();

    public Fr93CommandTests() => _cli.LoggedIn();

    public void Dispose() => _cli.Dispose();

    private static void AssertJson(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    private static string ReleaseVersion(string version, string status, string? tag = null, string? presetId = null) => $$"""
        {"id":"{{VersionId}}","version":"{{version}}","status":"{{status}}","tag":{{(tag is null ? "null" : $"\"{tag}\"")}},"tagPrefix":"v","baseTag":"v1.2.0",
         "baseCommitSha":"{{new string('a', 40)}}","mergeCommitSha":null,"branchName":"aide/todo-app-{{version}}","pullRequestUrl":null,
         "checkpoints":{"current":1,"max":1},"presetId":{{(presetId is null ? "null" : $"\"{presetId}\"")}},"presetVersion":{{(presetId is null ? "null" : "\"1.0.0\"")}},
         "presetName":{{(presetId is null ? "null" : "\"Next.js + Supabase\"")}},"request":"Add tags","startedBy":{"id":null,"name":"Ada","isSystem":false},
         "createdAt":"2026-10-17T10:00:00Z","releasedAt":null}
        """;

    private const string StartedState = """
        {"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"refinement","status":"active","checkpoint":1,"releaseVersionId":"0199f2a0-6666-7000-8000-000000000001",
         "releasedVersion":"1.2.0","developmentVersion":"1.3.0","suggestedNextVersion":"1.4.0","executionPaused":false,"artifacts":[],
         "execution":{"total":0,"pending":0,"inProgress":0,"completed":0,"rejected":0,"blocked":0},
         "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
         "pendingGates":[],"tokensUsed":0,"tokenBudget":null,"executionStatus":"idle","pullRequest":null,"latestRelease":null,"latestDeployment":null}
        """;

    private static string Started(string? presetId = null) =>
        $$"""{"releaseVersion":{{ReleaseVersion("1.3.0", "in_development", presetId: presetId)}},"state":{{StartedState}}}""";

    private const string Deployment = """
        {"id":"0199f2a0-7777-7000-8000-000000000001","target":"production","provider":"vercel","status":"ready","url":"https://todo.vercel.app",
         "inspectorUrl":null,"gitRef":"main","gitSha":"0123456789abcdef0123456789abcdef01234567","releaseVersionId":"0199f2a0-6666-7000-8000-000000000001",
         "releaseVersion":"1.2.0","releaseId":null,"errorCode":null,"errorMessage":null,"buildLogTail":null,"trigger":"manual",
         "triggeredBy":{"id":"0199f2a0-0000-7000-8000-000000000000","name":"Ada"},"promotedFromDeploymentId":null,"createdAt":"2026-10-17T10:00:00Z","readyAt":"2026-10-17T10:02:00Z"}
        """;

    private const string FailedPreview = """
        {"id":"0199f2a0-7777-7000-8000-000000000002","target":"preview","provider":"vercel","status":"error","url":null,
         "inspectorUrl":null,"gitRef":"aide/todo-app-1.3.0","gitSha":"fedcba9876543210fedcba9876543210fedcba98","releaseVersionId":"0199f2a0-6666-7000-8000-000000000001",
         "releaseVersion":"1.3.0","releaseId":null,"errorCode":"VERCEL_ERROR","errorMessage":"Build failed","buildLogTail":"error TS2322","trigger":"auto",
         "triggeredBy":null,"promotedFromDeploymentId":null,"createdAt":"2026-10-17T11:00:00Z","readyAt":null}
        """;

    [Fact]
    public async Task FR93_version_start_with_request_from_a_file_exits_0_and_prints_the_new_version()
    {
        var file = Path.Combine(_cli.ConfigRoot, "req.md");
        await File.WriteAllTextAsync(file, "# 1.3.0\n\nAdd tags to todos.\n", TestContext.Current.CancellationToken);
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Started()));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "@" + file);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/projects/{Samples.ProjectId}/versions"), (post.Method, post.Uri.AbsolutePath));
        AssertJson("""{"version":"1.3.0","request":"# 1.3.0\n\nAdd tags to todos.\n"}""", post.Body);
        Assert.Contains("Started version 1.3.0 (from v1.2.0): phase refinement, checkpoint 1.", run.Out, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task FR93_version_start_takes_inline_text()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Started()));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "v1.3.0", "--request", "Add tags");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson("""{"version":"v1.3.0","request":"Add tags"}""", Assert.Single(_cli.Requests).Body);
    }

    [Theory]
    [InlineData("nextjs-supabase")]
    [InlineData("none")]
    public async Task FR156_FR129_version_start_with_preset_exits_2_saying_presets_were_removed_and_sends_nothing(string preset)
    {
        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "-r", "Add tags", "--preset", preset);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("presets were removed in v4; use --frontend", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR93_version_start_reads_the_request_from_stdin_and_prints_json()
    {
        _cli.Terminal.StandardInput = "Piped request";
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Started()));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "@-", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson("""{"version":"1.3.0","request":"Piped request"}""", Assert.Single(_cli.Requests).Body);
        AssertJson(Started(), run.Out);
    }

    [Theory]
    [InlineData("version", "start", Samples.ProjectId, "1.3.0")]
    [InlineData("version", "start", Samples.ProjectId, "--request", "x")]
    [InlineData("version", "start", "not-a-guid", "1.3.0", "--request", "x")]
    [InlineData("deployments", Samples.ProjectId, "--target", "staging")]
    [InlineData("artifact", "restore", Samples.ProjectId, "spec", "0")]
    [InlineData("artifact", "restore", Samples.ProjectId, "nope", "1")]
    public async Task FR93_bad_arguments_exit_2_without_calling_the_api(params string[] args)
    {
        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR93_an_empty_request_file_is_a_usage_error()
    {
        var file = Path.Combine(_cli.ConfigRoot, "empty.md");
        await File.WriteAllTextAsync(file, "  \n", TestContext.Current.CancellationToken);

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "@" + file);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Theory]
    [InlineData("@")]
    [InlineData("@ ")]
    public async Task FR93_a_bare_at_sign_is_a_usage_error_with_one_line(string request)
    {
        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", request);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal("error: --request @ needs a file path (@path, or @- for stdin).", run.Error.Trim());
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR93_an_unreadable_request_path_exits_1_without_a_stack_trace()
    {
        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "@bad\0path");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.StartsWith("error: Could not read", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "VERSION_INVALID")]
    [InlineData(HttpStatusCode.Conflict, "VERSION_NOT_GREATER")]
    [InlineData(HttpStatusCode.Conflict, "VERSION_IN_PROGRESS")]
    [InlineData(HttpStatusCode.Conflict, "TAG_EXISTS")]
    [InlineData(HttpStatusCode.Conflict, "RELEASE_NOT_MERGED")]
    [InlineData(HttpStatusCode.Conflict, "NOT_RELEASED")]
    public async Task FR93_release_version_errors_exit_4_and_print_the_error_code(HttpStatusCode status, string errorCode)
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Can't start the version", "The detail."));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "Add tags");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Contains($"[{errorCode}]", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION", ExitCodes.Error)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.BadGateway, "UPSTREAM_ERROR", ExitCodes.Error)]
    public async Task FR93_other_errors_keep_the_v1_mapping(HttpStatusCode status, string errorCode, int expected)
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Failed"));

        var run = await _cli.RunAsync("version", "start", Samples.ProjectId, "1.3.0", "--request", "Add tags");

        Assert.Equal(expected, run.ExitCode);
    }

    [Fact]
    public async Task FR93_versions_lists_each_version_with_status_tag_checkpoint_and_preset()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            $"[{ReleaseVersion("1.3.0", "in_development", presetId: "nextjs-supabase")},{ReleaseVersion("1.2.0", "released", tag: "v1.2.0")}]"));

        var run = await _cli.RunAsync("versions", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/versions", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("VERSION", lines[0], StringComparison.Ordinal);
        Assert.Contains("1.3.0", lines[1], StringComparison.Ordinal);
        Assert.Contains("in_development", lines[1], StringComparison.Ordinal);
        Assert.Contains("Next.js + Supabase@1.0.0 (retired, migrated)", lines[1], StringComparison.Ordinal);
        Assert.Contains("v1.2.0", lines[2], StringComparison.Ordinal);
        Assert.Contains("released", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR93_deployments_lists_target_version_sha_state_url_and_trigger_with_a_target_filter()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, $"[{FailedPreview},{Deployment}]"));

        var run = await _cli.RunAsync("deployments", Samples.ProjectId, "--target", "production");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/deployments?target=production", Assert.Single(_cli.Requests).Uri.PathAndQuery);
        Assert.Contains("error (VERCEL_ERROR)", run.Out, StringComparison.Ordinal);
        Assert.Contains("fedcba9", run.Out, StringComparison.Ordinal);
        Assert.Contains("https://todo.vercel.app", run.Out, StringComparison.Ordinal);
        Assert.Contains("Ada", run.Out, StringComparison.Ordinal);
        Assert.Contains("auto", run.Out, StringComparison.Ordinal);
        Assert.Contains(DeploymentId, run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR93_deployments_with_none_says_so()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "[]"));

        var run = await _cli.RunAsync("deployments", Samples.ProjectId);

        Assert.Equal((ExitCodes.Success, "No deployments."), (run.ExitCode, run.Out.Trim()));
    }

    [Fact]
    public async Task FR93_artifact_restore_posts_to_the_restore_route_and_prints_the_new_draft()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(3)));

        var run = await _cli.RunAsync("artifact", "restore", Samples.ProjectId, "spec", "1");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = Assert.Single(_cli.Requests);
        Assert.Equal((HttpMethod.Post, $"/api/projects/{Samples.ProjectId}/artifacts/technical_specification/versions/1/restore"), (post.Method, post.Uri.AbsolutePath));
        Assert.Null(post.Body);
        Assert.Contains("Restored technical_specification v1 as draft v3", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR93_artifact_restore_not_allowed_exits_4()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.Conflict, "RESTORE_NOT_ALLOWED", "Can't restore", "reason: stage_completed"));

        var run = await _cli.RunAsync("artifact", "restore", Samples.ProjectId, "plan", "2");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("[RESTORE_NOT_ALLOWED]", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR93_status_shows_released_and_in_development_versions_checkpoint_preset_and_latest_deployment()
    {
        var state = JsonNode.Parse(StartedState)!.AsObject();
        state["latestDeployment"] = JsonNode.Parse(Deployment);
        var project = JsonNode.Parse(Samples.Project)!.AsObject();
        project["presetId"] = "nextjs-supabase";
        project["presetName"] = "Next.js + Supabase";
        project["presetVersion"] = "1.0.0";
        _cli.Respond(r => FakeHttpHandler.Json(HttpStatusCode.OK,
            r.RequestUri!.AbsolutePath.EndsWith("/state", StringComparison.Ordinal) ? state.ToJsonString() : project.ToJsonString()));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Released:\s+1\.2\.0", run.Out);
        Assert.Matches(@"In development:\s+1\.3\.0 \(checkpoint 1\)", run.Out);
        Assert.Matches(@"Checkpoint:\s+1", run.Out);
        Assert.Matches(@"Preset:\s+Next\.js \+ Supabase@1\.0\.0 \(retired, migrated\)", run.Out);
        Assert.Matches(@"Latest deployment:\s+production 1\.2\.0 ready https://todo\.vercel\.app", run.Out);
    }

    [Fact]
    public async Task FR93_status_without_preset_or_deployment_shows_none_and_a_dash()
    {
        _cli.Respond(r => FakeHttpHandler.Json(HttpStatusCode.OK,
            r.RequestUri!.AbsolutePath.EndsWith("/state", StringComparison.Ordinal) ? Samples.State : Samples.Project));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Matches(@"Preset:\s+none", run.Out);
        Assert.Matches(@"Latest deployment:\s+-", run.Out);
    }

    [Fact]
    public async Task FR93_help_lists_the_new_commands_and_the_exit_code_4_errors()
    {
        var root = await _cli.RunAsync("--help");
        var start = await _cli.RunAsync("version", "start", "--help");

        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (root.ExitCode, start.ExitCode));
        foreach (var command in new[] { "version", "versions", "deployments" })
        {
            Assert.Contains(command, root.Out, StringComparison.Ordinal);
        }

        Assert.Contains("TAG_EXISTS", root.Out, StringComparison.Ordinal);
        Assert.Contains("--request", start.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("--preset", start.Out, StringComparison.Ordinal); // FR-156: hidden, only kept to explain its removal
        Assert.Contains("@path", start.Out, StringComparison.Ordinal);
    }
}
