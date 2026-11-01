using System.Net;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-156 (LLM part), FR-159: `aide project create` with the LLM flags, and LLM_PROVIDER_UNAVAILABLE → exit 4.</summary>
public sealed class Fr156CommandTests : IDisposable
{
    private const string ConnectionId = "0199f2a0-8888-7000-8000-000000000001";

    private readonly CliTestHarness _cli = new();

    public Fr156CommandTests() => _cli.LoggedIn();

    public void Dispose() => _cli.Dispose();

    private static void AssertJson(string expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), actual);

    [Fact]
    public async Task FR156_project_create_posts_the_llm_choice_with_the_connection()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync(
            "project", "create", "--name", "Todo app", "--idea", "A todo app",
            "--llm-provider", "anthropic", "--llm-model", "claude-opus-5-5", "--llm-connection", ConnectionId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal("/api/projects", request.Uri.AbsolutePath);
        AssertJson(
            $$$"""
            {"name":"Todo app","idea":"A todo app","git":{"provider":"local"},
             "llm":{"provider":"anthropic","defaultModel":"claude-opus-5-5","credentialId":"{{{ConnectionId}}}"}}
            """,
            request.Body);
    }

    [Fact]
    public async Task FR156_project_init_accepts_the_llm_flags_without_a_connection()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync("project", "init", "--name", "N", "--idea", "I", "--llm-provider", "ollama", "--llm-model", "qwen2.5-coder");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson(
            """{"name":"N","idea":"I","git":{"provider":"local"},"llm":{"provider":"ollama","defaultModel":"qwen2.5-coder"}}""",
            Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR156_project_create_without_llm_flags_sends_no_llm()
    {
        _cli.Respond(_ => FakeHttpHandler.Json(HttpStatusCode.Created, Samples.Project));

        var run = await _cli.RunAsync("project", "create", "--name", "N", "--idea", "I", "--no-input");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertJson("""{"name":"N","idea":"I","git":{"provider":"local"}}""", Assert.Single(_cli.Requests).Body);
    }

    [Fact]
    public async Task FR156_llm_provider_without_a_model_is_a_usage_error()
    {
        var run = await _cli.RunAsync("project", "create", "--name", "N", "--idea", "I", "--llm-provider", "anthropic");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("--llm-model is required", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR156_llm_model_without_a_provider_is_a_usage_error()
    {
        var run = await _cli.RunAsync("project", "create", "--name", "N", "--idea", "I", "--llm-model", "claude-opus-5-5");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("--llm-provider", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR156_an_unknown_llm_provider_is_a_usage_error()
    {
        var run = await _cli.RunAsync("project", "create", "--name", "N", "--idea", "I", "--llm-provider", "gemini", "--llm-model", "x");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR156_LLM_PROVIDER_UNAVAILABLE_exits_4_and_prints_the_error_code()
    {
        _cli.Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.BadRequest, "LLM_PROVIDER_UNAVAILABLE", "The fake AI provider is turned off on this server.", "Choose another provider."));

        var run = await _cli.RunAsync("project", "create", "--name", "N", "--idea", "I", "--llm-provider", "fake", "--llm-model", "fake-1");

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("[LLM_PROVIDER_UNAVAILABLE]", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FR156_LLM_PROVIDER_UNAVAILABLE_maps_to_exit_4_whatever_the_status() =>
        Assert.Equal(ExitCodes.Conflict, ExitCodes.ForProblem(HttpStatusCode.BadRequest, "LLM_PROVIDER_UNAVAILABLE"));
}
