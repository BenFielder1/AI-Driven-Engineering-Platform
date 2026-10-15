using System.Net;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-52: each command calls the right route with the right body and prints a useful result.</summary>
public sealed class CommandTests : IDisposable
{
    private readonly CliTestHarness _cli = new();

    public CommandTests() => _cli.LoggedIn();

    public void Dispose() => _cli.Dispose();

    private static void AssertJson(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    [Fact]
    public async Task FR52_projects_list_passes_paging_search_and_filters()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));

        var run = await _cli.RunAsync("projects", "list", "--page", "2", "--limit", "10", "--search", "todo app", "--phase", "review", "--sort-by", "name", "--sort-order", "asc");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(
            "/api/projects?page=2&limit=10&search=todo%20app&phase=review&sortBy=name&sortOrder=asc",
            Assert.Single(_cli.Requests).Uri.PathAndQuery);
        Assert.Contains("Todo app", run.Out, StringComparison.Ordinal);
        Assert.Contains("reviewing", run.Out, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2 (11 projects)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_projects_list_json_prints_the_api_page()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ProjectsPage));

        var run = await _cli.RunAsync("projects", "list", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(Samples.ProjectsPage, run.Out);
    }

    [Fact]
    public async Task FR52_project_init_posts_the_create_dto_with_snake_case_enums()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync(
            "project", "init", "--name", "Todo app", "--idea", "A todo app", "--approval-mode", "advisory",
            "--compliance-threshold", "0.9", "--max-review-cycles", "4", "--token-budget", "50000", "--default-branch", "trunk");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects", request.Uri.AbsolutePath);
        AssertJson(
            """
            {"name":"Todo app","idea":"A todo app","approvalMode":"advisory","complianceThreshold":0.9,"maxReviewCycles":4,"tokenBudget":50000,
             "git":{"provider":"local","defaultBranch":"trunk"}}
            """,
            request.Body);
        Assert.Contains($"Created project Todo app ({Samples.ProjectId})", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_project_init_github_reads_the_git_token_from_the_environment()
    {
        _cli.Environment["AIDE_GIT_TOKEN"] = "ghp_secret";
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync("project", "init", "--name", "N", "--idea", "I", "--git-provider", "github", "--repo-url", "https://github.com/o/r");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """{"name":"N","idea":"I","git":{"provider":"github","repoUrl":"https://github.com/o/r","token":"ghp_secret"}}""",
            Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR52_project_init_without_a_terminal_requires_the_flags()
    {
        _cli.Terminal.IsInteractive = false;

        var run = await _cli.RunAsync("project", "init", "--idea", "An idea");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("--name is required", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Terminal.Prompts);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_project_init_prompts_for_missing_values_on_a_terminal()
    {
        _cli.Terminal.IsInteractive = true;
        foreach (var answer in new[] { "Prompted name", "Prompted idea", "", "" })
        {
            _cli.Terminal.Answers.Enqueue(answer);
        }

        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync("project", "init");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(4, _cli.Terminal.Prompts.Count);
        AssertJson("""{"name":"Prompted name","idea":"Prompted idea","git":{"provider":"local"}}""", Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR52_project_init_no_input_never_prompts()
    {
        _cli.Terminal.IsInteractive = true;

        var run = await _cli.RunAsync("project", "init", "--no-input", "--name", "N");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Terminal.Prompts);
    }

    [Fact]
    public async Task FR52_artifact_show_prints_metadata_and_content()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(3, "locked")));

        var run = await _cli.RunAsync("artifact", "show", Samples.ProjectId, "spec", "--version", "3");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/technical_specification/versions/3", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Contains("technical_specification v3", run.Out, StringComparison.Ordinal);
        Assert.Contains("v1 content", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_artifact_approve_by_type_resolves_the_latest_version()
    {
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2))
            : FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked")));

        var run = await _cli.RunAsync("artifact", "approve", Samples.ProjectId, "technical_specification", "--comment", "LGTM");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(2, _cli.Requests.Count);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/technical_specification/latest", _cli.Requests[0].Uri.AbsolutePath);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/{Samples.ArtifactId}/approve", _cli.Requests[1].Uri.AbsolutePath);
        AssertJson("""{"comment":"LGTM"}""", _cli.Requests[1].Body);
        Assert.Contains("technical_specification v2 is now locked", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_artifact_reject_by_id_when_not_a_draft_exits_4()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.Conflict, "ARTIFACT_LOCKED", "Artifact is locked"));

        var run = await _cli.RunAsync("artifact", "reject", Samples.ProjectId, Samples.ArtifactId);

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/{Samples.ArtifactId}/reject", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Contains("[ARTIFACT_LOCKED]", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_artifact_edit_with_file_posts_a_draft_with_the_base_version()
    {
        var file = Path.Combine(_cli.ConfigRoot, "spec.md");
        await File.WriteAllTextAsync(file, "# Spec\n\nnew content\n", TestContext.Current.CancellationToken);
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked"))
            : FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(3)));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec", "--file", file);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var post = _cli.Requests[1];
        Assert.Equal($"/api/projects/{Samples.ProjectId}/artifacts/technical_specification", post.Uri.AbsolutePath);
        AssertJson("""{"content":"# Spec\n\nnew content\n","baseVersion":2}""", post.Body);
        Assert.Contains("Saved technical_specification v3 as draft", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_artifact_edit_force_omits_the_base_version()
    {
        _cli.Terminal.StandardInput = "piped content";
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked"))
            : FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(3)));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec", "--file", "-", "--force");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson("""{"content":"piped content"}""", _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR52_artifact_edit_opens_the_editor_on_the_latest_content()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FakeEditor(content => content + "edited\n");
        _cli.Editor = editor;
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked"))
            : FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(3)));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("# Spec\n\nv1 content\n", editor.OriginalContent);
        AssertJson("""{"content":"# Spec\n\nv1 content\nedited\n","baseVersion":2}""", _cli.Requests[1].Body);
        Assert.False(File.Exists(editor.EditedPath));
    }

    [Fact]
    public async Task FR52_artifact_edit_without_changes_creates_no_draft()
    {
        _cli.Terminal.IsInteractive = true;
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked")));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Single(_cli.Requests);
        Assert.Contains("No changes", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_artifact_edit_stale_base_version_exits_4_and_keeps_the_edit()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FakeEditor(_ => "my edit");
        _cli.Editor = editor;
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked"))
            : FakeHttpHandler.Problem(HttpStatusCode.Conflict, "CONFLICT", "Conflict", "latestVersion: 3"));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("[CONFLICT]", run.Error, StringComparison.Ordinal);
        Assert.Contains(editor.EditedPath!, run.Error, StringComparison.Ordinal);
        Assert.Equal("my edit", await File.ReadAllTextAsync(editor.EditedPath!, TestContext.Current.CancellationToken));
        File.Delete(editor.EditedPath!);
    }

    [Fact]
    public async Task FR52_artifact_edit_of_a_new_type_omits_the_base_version()
    {
        _cli.Terminal.StandardInput = "first version";
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Problem(HttpStatusCode.NotFound, "NOT_FOUND", "Not found")
            : FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(1)));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec", "--file", "-");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson("""{"content":"first version"}""", _cli.Requests[1].Body);
    }

    [Fact]
    public async Task FR52_artifact_edit_without_a_terminal_needs_file()
    {
        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Theory]
    [InlineData("", "start")]
    [InlineData("--pause", "pause")]
    [InlineData("--resume", "resume")]
    public async Task FR52_execute_starts_pauses_or_resumes(string flag, string action)
    {
        _cli.Respond(_ => FakeHttpHandler.Json(
            HttpStatusCode.Accepted,
            """{"status":"running","paused":false,"currentStepNumber":1,"counts":{"total":3,"pending":2,"inProgress":1,"completed":0,"rejected":0,"blocked":0}}"""));

        var run = await _cli.RunAsync(flag.Length == 0 ? ["execute", Samples.ProjectId] : ["execute", Samples.ProjectId, flag]);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/execution/{action}", request.Uri.AbsolutePath);
        Assert.Contains("0/3 steps completed", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_review_queues_a_review_cycle()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Accepted, """{"jobId":"0199f2a0-6666-7000-8000-000000000001","cycle":2}"""));

        var run = await _cli.RunAsync("review", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/review", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Contains("Review cycle 2 queued", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_release_approves_the_pending_pre_release_gate()
    {
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.PendingPreReleaseGate)
            : FakeHttpHandler.Json(HttpStatusCode.OK, Samples.ApprovedGate));

        var run = await _cli.RunAsync("release", Samples.ProjectId, "--comment", "ship it");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("/api/projects/" + Samples.ProjectId + "/gates?status=pending", _cli.Requests[0].Uri.PathAndQuery);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/gates/{Samples.GateId}/approve", _cli.Requests[1].Uri.AbsolutePath);
        AssertJson("""{"comment":"ship it"}""", _cli.Requests[1].Body);
        Assert.Contains("release is queued", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_release_without_a_gate_retries_a_failed_release()
    {
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, "[]")
            : FakeHttpHandler.Json(HttpStatusCode.Accepted, Samples.Release));

        var run = await _cli.RunAsync("release", Samples.ProjectId, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/release", _cli.Requests[1].Uri.AbsolutePath);
        AssertJson(Samples.Release, run.Out);
    }

    [Fact]
    public async Task FR52_release_when_not_releasing_exits_4()
    {
        _cli.Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, "[]")
            : FakeHttpHandler.Problem(HttpStatusCode.Conflict, "NOT_RELEASING", "Project is not releasing"));

        var run = await _cli.RunAsync("release", Samples.ProjectId);

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("[NOT_RELEASING]", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_amended_by_FR63_rollback_posts_target_checkpoint_and_reason()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(
            HttpStatusCode.OK,
            """
            {"id":"0199f2a0-7777-7000-8000-000000000001","fromCheckpoint":5,"toCheckpoint":2,"restoredPhase":"planning","reason":"wrong plan",
             "invalidatedArtifactIds":["0199f2a0-7777-7000-8000-000000000002"],"resetStepIds":[],"requeuedStepIds":[],"cancelledGateIds":[],"cancelledJobIds":[],
             "createdAt":"2026-10-10T10:00:00Z","createdBy":{"id":null,"name":"Ada","isSystem":false}}
            """));

        var run = await _cli.RunAsync("rollback", Samples.ProjectId, "2", "--reason", "wrong plan");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/rollback", Assert.Single(_cli.Requests).Uri.AbsolutePath);
        Assert.Contains("Rolled back from checkpoint 5 to checkpoint 2; phase is now planning", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_logs_print_the_feed_oldest_first_with_filters()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Activity));

        var run = await _cli.RunAsync("logs", Samples.ProjectId, "--type", "gate", "--limit", "20");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"/api/projects/{Samples.ProjectId}/activity?type=gate&limit=20", Assert.Single(_cli.Requests).Uri.PathAndQuery);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first event", lines[0], StringComparison.Ordinal);
        Assert.Contains("second event", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_logs_json_prints_the_api_page()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Activity));

        var run = await _cli.RunAsync("logs", Samples.ProjectId, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(Samples.Activity, run.Out);
    }
}
