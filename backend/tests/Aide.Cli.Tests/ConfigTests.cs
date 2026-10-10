using System.Net;
using System.Text.Json.Nodes;
using Aide.Cli.Config;

namespace Aide.Cli.Tests;

/// <summary>FR-52: config location, precedence (flag &gt; env &gt; file &gt; default), token masking and login.</summary>
public sealed class ConfigTests : IDisposable
{
    private readonly CliTestHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task FR52_config_path_defaults_to_the_user_config_folder()
    {
        var run = await _cli.RunAsync("config", "path");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(_cli.DefaultConfigPath, run.Out.Trim());
    }

    [Fact]
    public async Task FR52_AIDE_CONFIG_overrides_the_config_path()
    {
        var custom = Path.Combine(_cli.ConfigRoot, "elsewhere", "my.json");
        _cli.Environment["AIDE_CONFIG"] = custom;

        await _cli.RunAsync("config", "set", "api-url", "http://aide.test:9000");
        var run = await _cli.RunAsync("config", "path");

        Assert.Equal(custom, run.Out.Trim());
        Assert.True(File.Exists(custom));
        Assert.False(File.Exists(_cli.DefaultConfigPath));
    }

    [Fact]
    public async Task FR52_default_api_url_is_localhost_8080()
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));

        await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal("localhost:8080", Assert.Single(_cli.Requests).Uri.Authority);
    }

    [Theory]
    [InlineData(true, true, true, "flag.test")]
    [InlineData(false, true, true, "env.test")]
    [InlineData(false, false, true, "file.test")]
    [InlineData(false, false, false, "localhost:8080")]
    public async Task FR52_api_url_precedence_is_flag_then_env_then_file_then_default(bool flag, bool env, bool file, string expectedAuthority)
    {
        _cli.LoggedIn(apiUrl: file ? "https://file.test" : null).Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));
        _cli.Environment["AIDE_TOKEN"] = CliTestHarness.Token;
        if (env)
        {
            _cli.Environment["AIDE_API_URL"] = "https://env.test";
        }

        string[] args = flag
            ? ["status", Samples.ProjectId, "--json", "--api-url", "https://flag.test"]
            : ["status", Samples.ProjectId, "--json"];
        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(expectedAuthority, Assert.Single(_cli.Requests).Uri.Authority);
    }

    [Fact]
    public async Task FR52_AIDE_TOKEN_overrides_the_saved_token()
    {
        _cli.LoggedIn("aide_pat_from_file_000000000000").Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));
        _cli.Environment["AIDE_TOKEN"] = "aide_pat_from_env_111111111111";

        await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal("Bearer aide_pat_from_env_111111111111", Assert.Single(_cli.Requests).Authorization);
    }

    [Fact]
    public async Task FR52_api_url_with_trailing_api_segment_is_accepted()
    {
        _cli.LoggedIn(apiUrl: "https://aide.test:8080/api/").Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));

        await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal($"https://aide.test:8080/api/projects/{Samples.ProjectId}/state", Assert.Single(_cli.Requests).Uri.ToString());
    }

    [Fact]
    public async Task FR52_invalid_api_url_flag_is_a_usage_error()
    {
        _cli.LoggedIn();

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--api-url", "ftp://nope");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_config_get_shows_the_token_masked_with_its_source()
    {
        _cli.LoggedIn();

        var human = await _cli.RunAsync("config", "get");
        var token = await _cli.RunAsync("config", "get", "token");
        var json = await _cli.RunAsync("config", "get", "--json");

        foreach (var run in new[] { human, token, json })
        {
            Assert.Equal(ExitCodes.Success, run.ExitCode);
            Assert.DoesNotContain(CliTestHarness.Token, run.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(CliTestHarness.Token[9..^4], run.Out, StringComparison.Ordinal);
        }

        Assert.Equal("aide_pat_********stuv", token.Out.Trim());
        Assert.Contains("config file", human.Out, StringComparison.Ordinal);
        var node = JsonNode.Parse(json.Out)!;
        Assert.Equal("aide_pat_********stuv", node["token"]!.GetValue<string>());
        Assert.Equal("config file", node["tokenSource"]!.GetValue<string>());
        Assert.Equal("http://localhost:8080", node["apiUrl"]!.GetValue<string>());
        Assert.Equal("default", node["apiUrlSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task FR52_config_set_token_never_echoes_it()
    {
        _cli.Terminal.StandardInput = CliTestHarness.Token + "\n";
        var run = await _cli.RunAsync("config", "set", "token");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.DoesNotContain(CliTestHarness.Token, run.Out, StringComparison.Ordinal);
        Assert.Contains(CliTestHarness.Token, await File.ReadAllTextAsync(_cli.DefaultConfigPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_config_set_rejects_an_invalid_api_url()
    {
        var run = await _cli.RunAsync("config", "set", "api-url", "not a url");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.False(File.Exists(_cli.DefaultConfigPath));
    }

    [Fact]
    public async Task FR52_config_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        _cli.Terminal.StandardInput = CliTestHarness.Token;
        await _cli.RunAsync("config", "set", "token");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_cli.DefaultConfigPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(_cli.DefaultConfigPath)!));
    }

    [Fact]
    public async Task FR52_corrupt_config_file_exits_1_with_its_path()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cli.DefaultConfigPath)!);
        await File.WriteAllTextAsync(_cli.DefaultConfigPath, "{not json", TestContext.Current.CancellationToken);

        var run = await _cli.RunAsync("config", "get");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains(_cli.DefaultConfigPath, run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_login_validates_the_token_with_auth_me_and_saves_it()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.User));

        var run = await _cli.RunAsync("login", "--token", CliTestHarness.Token, "--api-url", "https://aide.test:8080");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal("https://aide.test:8080/api/auth/me", request.Uri.ToString());
        Assert.Equal($"Bearer {CliTestHarness.Token}", request.Authorization);
        Assert.Contains("Ada <ada@example.com>", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(CliTestHarness.Token, run.Out + run.Error, StringComparison.Ordinal);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(_cli.DefaultConfigPath, TestContext.Current.CancellationToken))!;
        Assert.Equal(CliTestHarness.Token, saved["token"]!.GetValue<string>());
        Assert.Equal("https://aide.test:8080", saved["apiUrl"]!.GetValue<string>());
        Assert.Equal("https://aide.test:8080/", saved["tokenApiUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task FR52_login_reads_a_piped_token_from_stdin()
    {
        _cli.Terminal.StandardInput = CliTestHarness.Token + "\n";
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.User));

        var run = await _cli.RunAsync("login");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal($"Bearer {CliTestHarness.Token}", Assert.Single(_cli.Requests).Authorization);
    }

    [Fact]
    public async Task FR52_login_with_a_rejected_token_exits_3_and_saves_nothing()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", "Unauthorized"));

        var run = await _cli.RunAsync("login", "--token", "aide_pat_revoked_00000000000000");

        Assert.Equal(ExitCodes.Auth, run.ExitCode);
        Assert.Contains("[UNAUTHENTICATED]", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_cli.DefaultConfigPath));
    }

    [Fact]
    public async Task FR52_logout_removes_the_saved_token()
    {
        _cli.LoggedIn();

        var run = await _cli.RunAsync("logout");
        var after = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(ExitCodes.Auth, after.ExitCode);
    }

    [Theory]
    [InlineData(null, "(not set)")]
    [InlineData("", "(not set)")]
    [InlineData("short", "********")]
    [InlineData("aide_pat_abcdefghijklmnopqrstuvwxyz", "aide_pat_********wxyz")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz", "********wxyz")]
    public void FR52_token_mask(string? token, string expected) => Assert.Equal(expected, TokenMask.Mask(token));
}
