using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aide.Cli.Tests;

/// <summary>FR-52 / NFR-SEC: where the token may be sent, how it is entered, the editor temp file, and --follow paging.</summary>
public sealed class SecurityFixTests : IDisposable
{
    private readonly CliTestHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private CliTestHarness RespondState() => _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));

    private Task<string> ReadConfigAsync() => File.ReadAllTextAsync(_cli.DefaultConfigPath, TestContext.Current.CancellationToken);

    // --- The saved token is bound to the API URL it was saved for ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FR52_saved_token_is_not_sent_to_another_api_url(bool viaFlag)
    {
        _cli.LoggedIn();
        RespondState();
        string[] args = viaFlag ? ["status", Samples.ProjectId, "--api-url", "https://elsewhere.test"] : ["status", Samples.ProjectId];
        if (!viaFlag)
        {
            _cli.Environment["AIDE_API_URL"] = "https://elsewhere.test";
        }

        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Auth, run.ExitCode);
        Assert.Empty(_cli.Requests);
        Assert.Contains("saved token belongs to http://localhost:8080", run.Error, StringComparison.Ordinal);
        Assert.Contains("AIDE_TOKEN", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://aide.test", "HTTPS://AIDE.test:443/api/")]
    [InlineData("https://aide.test:443/", "https://aide.test")]
    [InlineData("http://localhost:8080", "http://LOCALHOST:8080/")]
    public async Task FR52_saved_token_is_sent_when_the_api_url_matches_after_normalisation(string savedFor, string used)
    {
        _cli.LoggedIn(apiUrl: savedFor);
        RespondState();

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json", "--api-url", used);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"Bearer {CliTestHarness.Token}", Assert.Single(_cli.Requests).Authorization);
    }

    [Theory]
    [InlineData("https://aide.test", "https://aide.test:8443")]
    [InlineData("https://aide.test", "https://aide.test/other")]
    [InlineData("https://aide.test", "http://aide.test")]
    [InlineData("http://localhost:8080", "http://localhost:9090")]
    public async Task FR52_saved_token_is_withheld_when_scheme_port_or_path_differ(string savedFor, string used)
    {
        _cli.LoggedIn(apiUrl: savedFor);
        RespondState();

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--api-url", used);

        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_AIDE_TOKEN_is_explicit_and_sent_to_an_overridden_api_url()
    {
        _cli.LoggedIn();
        RespondState();
        _cli.Environment["AIDE_TOKEN"] = "aide_pat_from_env_111111111111";

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json", "--api-url", "https://elsewhere.test");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal("elsewhere.test", request.Uri.Host);
        Assert.Equal("Bearer aide_pat_from_env_111111111111", request.Authorization);
    }

    [Fact]
    public async Task FR52_legacy_token_without_tokenApiUrl_is_bound_to_the_file_api_url()
    {
        _cli.WriteConfig(CliTestHarness.Token, "https://aide.test", tokenApiUrl: null);
        RespondState();

        var same = await _cli.RunAsync("status", Samples.ProjectId, "--json");
        var other = await _cli.RunAsync("status", Samples.ProjectId, "--api-url", "https://elsewhere.test");

        Assert.Equal(ExitCodes.Success, same.ExitCode);
        Assert.Equal(ExitCodes.Auth, other.ExitCode);
        Assert.Equal("aide.test", Assert.Single(_cli.Requests).Uri.Host);
    }

    [Fact]
    public async Task FR52_config_set_api_url_never_redirects_the_saved_token()
    {
        _cli.WriteConfig(CliTestHarness.Token, apiUrl: null, tokenApiUrl: null);
        RespondState();

        var set = await _cli.RunAsync("config", "set", "api-url", "https://elsewhere.test");
        var status = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, set.ExitCode);
        Assert.Contains("will not be sent to https://elsewhere.test", set.Error, StringComparison.Ordinal);
        Assert.Equal("http://localhost:8080/", JsonNode.Parse(await ReadConfigAsync())!["tokenApiUrl"]!.GetValue<string>());
        Assert.Equal(ExitCodes.Auth, status.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_config_get_shows_a_withheld_saved_token()
    {
        _cli.LoggedIn();

        var run = await _cli.RunAsync("config", "get", "--json", "--api-url", "https://elsewhere.test");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        Assert.Equal("aide_pat_********stuv", node["token"]!.GetValue<string>());
        Assert.Contains("not sent to https://elsewhere.test", node["tokenSource"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_login_binds_the_token_to_the_api_url_and_logout_clears_it()
    {
        _cli.Respond(r => FakeHttpHandler.Json(
            HttpStatusCode.OK,
            r.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal) ? Samples.User : Samples.State));
        _cli.Environment["AIDE_API_URL"] = "https://aide.test";

        var login = await _cli.RunAsync("login", "--token", CliTestHarness.Token);
        var status = await _cli.RunAsync("status", Samples.ProjectId, "--json");
        var saved = JsonNode.Parse(await ReadConfigAsync())!;
        await _cli.RunAsync("logout");
        var cleared = JsonNode.Parse(await ReadConfigAsync())!;

        Assert.Equal(ExitCodes.Success, login.ExitCode);
        Assert.Equal(ExitCodes.Success, status.ExitCode);
        Assert.Equal("https://aide.test:443/", saved["tokenApiUrl"]!.GetValue<string>());
        Assert.Null(cleared["token"]);
        Assert.Null(cleared["tokenApiUrl"]);
    }

    // --- Plain http only to loopback ---

    [Theory]
    [InlineData("env")]
    [InlineData("file")]
    [InlineData("login")]
    public async Task FR52_plain_http_to_a_non_loopback_host_is_refused_for_any_token(string tokenFrom)
    {
        RespondState();
        string[] args = ["status", Samples.ProjectId, "--api-url", "http://aide.test:8080"];
        switch (tokenFrom)
        {
            case "env":
                _cli.Environment["AIDE_TOKEN"] = CliTestHarness.Token;
                break;
            case "file":
                _cli.LoggedIn(apiUrl: "http://aide.test:8080");
                break;
            default:
                args = ["login", "--token", CliTestHarness.Token, "--api-url", "http://aide.test:8080"];
                break;
        }

        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
        Assert.Contains("plain http", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(CliTestHarness.Token, run.Out + run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://127.0.0.2:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://host.docker.internal:8080", false)]
    [InlineData("http://10.0.0.5:8080", false)]
    [InlineData("http://localhost.evil.test", false)]
    public async Task FR52_plain_http_is_allowed_only_for_loopback(string apiUrl, bool allowed)
    {
        RespondState();
        _cli.Environment["AIDE_TOKEN"] = CliTestHarness.Token;

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json", "--api-url", apiUrl);

        Assert.Equal(allowed ? ExitCodes.Success : ExitCodes.Usage, run.ExitCode);
        Assert.Equal(allowed ? 1 : 0, _cli.Requests.Count);
    }

    // --- `config set token` never takes the token as an argument ---

    [Fact]
    public async Task FR52_config_set_token_refuses_a_positional_value()
    {
        var run = await _cli.RunAsync("config", "set", "token", CliTestHarness.Token);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("aide login", run.Error, StringComparison.Ordinal);
        Assert.Contains("shell history", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(CliTestHarness.Token, run.Out + run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_cli.DefaultConfigPath));
    }

    [Fact]
    public async Task FR52_config_set_token_prompts_hidden_on_a_terminal_and_binds_it_to_the_api_url()
    {
        _cli.Terminal.IsInteractive = true;
        _cli.Terminal.Answers.Enqueue(CliTestHarness.Token);

        var run = await _cli.RunAsync("config", "set", "token");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("API token: ", Assert.Single(_cli.Terminal.Prompts));
        Assert.DoesNotContain(CliTestHarness.Token, run.Out, StringComparison.Ordinal);
        var saved = JsonNode.Parse(await ReadConfigAsync())!;
        Assert.Equal(CliTestHarness.Token, saved["token"]!.GetValue<string>());
        Assert.Equal("http://localhost:8080/", saved["tokenApiUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task FR52_config_set_token_with_no_input_is_a_usage_error()
    {
        var run = await _cli.RunAsync("config", "set", "token");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.False(File.Exists(_cli.DefaultConfigPath));
    }

    [Fact]
    public async Task FR52_config_set_token_empty_string_clears_it()
    {
        _cli.LoggedIn();

        var run = await _cli.RunAsync("config", "set", "token", "");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Null(JsonNode.Parse(await ReadConfigAsync())!["token"]);
    }

    // --- Editor temp file ---

    private CliTestHarness RespondArtifact(Func<HttpResponseMessage>? onPost = null) =>
        _cli.LoggedIn().Respond(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Samples.Artifact(2, "locked"))
            : onPost?.Invoke() ?? FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Artifact(3)));

    [Fact]
    public async Task FR52_artifact_edit_temp_file_is_owner_only_unpredictable_and_removed_on_success()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FakeEditor(content => content + "edited\n");
        _cli.Editor = editor;
        RespondArtifact();

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(editor.EditedPath)!));
        Assert.Matches(new Regex("^aide-technical_specification-[0-9a-f]{32}\\.md$"), Path.GetFileName(editor.EditedPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, editor.ModeDuringEdit);
        }

        Assert.False(File.Exists(editor.EditedPath));
    }

    [Fact]
    public async Task FR52_artifact_edit_without_changes_removes_the_temp_file()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FakeEditor(content => content);
        _cli.Editor = editor;
        RespondArtifact();

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.False(File.Exists(editor.EditedPath));
    }

    [Fact]
    public async Task FR52_artifact_edit_failed_editor_keeps_the_temp_file_and_says_where()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FailingEditor();
        _cli.Editor = editor;
        RespondArtifact();

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains($"kept at {editor.EditedPath}", run.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(editor.EditedPath));
        Assert.Single(_cli.Requests);
        File.Delete(editor.EditedPath!);
    }

    [Fact]
    public async Task FR52_artifact_edit_failed_save_keeps_the_temp_file_and_says_where()
    {
        _cli.Terminal.IsInteractive = true;
        var editor = new FakeEditor(_ => "my edit");
        _cli.Editor = editor;
        RespondArtifact(() => FakeHttpHandler.Problem(HttpStatusCode.InternalServerError, "INTERNAL", "Server error"));

        var run = await _cli.RunAsync("artifact", "edit", Samples.ProjectId, "spec");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains($"kept at {editor.EditedPath}", run.Error, StringComparison.Ordinal);
        Assert.Equal("my edit", await File.ReadAllTextAsync(editor.EditedPath!, TestContext.Current.CancellationToken));
        File.Delete(editor.EditedPath!);
    }

    // --- logs --follow pages back so a burst larger than --limit is not dropped ---

    private static string Event(int n) =>
        $$$"""{"id":"0199f2a0-4444-7000-8000-{{{n:D12}}}","type":"STEP_COMPLETED","category":"step","occurredAt":"2026-10-10T10:{{{n:D2}}}:00Z","actor":null,"summary":"event {{{n}}}","payload":{}}""";

    private static string Page(string? nextBefore, params int[] newestFirst) =>
        $$"""{"items":[{{string.Join(',', newestFirst.Select(Event))}}],"nextBefore":{{(nextBefore is null ? "null" : $"\"{nextBefore}\"")}}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR52_logs_follow_pages_back_when_more_than_limit_events_arrive_in_one_interval(bool json)
    {
        _cli.LoggedIn();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pages = new Queue<string>([
            Page("c1", 2, 1),       // initial page
            Page("c5", 6, 5),       // poll 1: newest page, nothing printed yet
            Page("c3", 4, 3),       // poll 1: before=c5
            Page("c1", 2, 1),       // poll 1: before=c3 reaches printed events, stop
            Page("c6", 7, 6),       // poll 2: one new event, then a printed one
        ]);
        _cli.Respond(_ =>
        {
            if (pages.Count == 0)
            {
                stop.Cancel(); // poll 3: stop following
                return FakeHttpHandler.Json(HttpStatusCode.OK, Page(null));
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, pages.Dequeue());
        });

        string[] args = json
            ? ["logs", Samples.ProjectId, "--follow", "--limit", "2", "--json"]
            : ["logs", Samples.ProjectId, "--follow", "--limit", "2"];
        var run = await _cli.RunAsync(stop.Token, args);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Enumerable.Range(1, 7).Select(n => $"event {n}"), lines.Select(l => Regex.Match(l, "event \\d+").Value));
        var queries = _cli.Requests.Select(r => r.Uri.Query).ToList();
        Assert.Equal(6, queries.Count);
        Assert.Equal("?limit=2", queries[1]);
        Assert.Equal("?before=c5&limit=2", queries[2]);
        Assert.Equal("?before=c3&limit=2", queries[3]);
        Assert.Equal("?limit=2", queries[4]);
    }
}
