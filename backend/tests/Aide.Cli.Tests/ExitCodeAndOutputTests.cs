using System.Net;
using System.Text.Json.Nodes;

namespace Aide.Cli.Tests;

/// <summary>FR-52: usage errors, exit-code mapping, JSON output and error reporting.</summary>
public sealed class ExitCodeAndOutputTests : IDisposable
{
    private readonly CliTestHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    public static readonly TheoryData<string[]> UsageErrors = new()
    {
        Array.Empty<string>(),
        new[] { "config" },
        new[] { "artifact" },
        new[] { "frobnicate" },
        new[] { "status" },
        new[] { "status", "not-a-guid" },
        new[] { "status", Samples.ProjectId, "--bogus" },
        new[] { "rollback", Samples.ProjectId, "3" },
        new[] { "rollback", Samples.ProjectId, "three", "--reason", "x" },
        new[] { "rollback", Samples.ProjectId, "0", "--reason", "x" },
        new[] { "execute", Samples.ProjectId, "--pause", "--resume" },
        new[] { "projects", "list", "--limit", "500" },
        new[] { "projects", "list", "--phase", "nowhere" },
        new[] { "artifact", "show", Samples.ProjectId, "novel" },
        new[] { "config", "set", "colour", "blue" },
        new[] { "logs", Samples.ProjectId, "--follow", "--before", "abc" },
        new[] { "logs", Samples.ProjectId, "--interval", "0" },
        new[] { "projects", "list", "--page", "0" },
        new[] { "artifact", "show", Samples.ProjectId, "spec", "--version", "x" },
        new[] { "project", "init", "--compliance-threshold", "high" },
    };

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task FR52_usage_errors_exit_2_without_calling_the_api(string[] args)
    {
        _cli.LoggedIn();

        var run = await _cli.RunAsync(args);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.StartsWith("error:", run.Error, StringComparison.Ordinal);
        Assert.Contains("--help", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_help_exits_0_and_lists_the_commands()
    {
        var run = await _cli.RunAsync("--help");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        foreach (var command in new[] { "config", "login", "projects", "project", "status", "artifact", "execute", "review", "release", "rollback", "logs" })
        {
            Assert.Contains(command, run.Out, StringComparison.Ordinal);
        }

        Assert.Contains("3 auth (401/403), 4 conflict (409)", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_status_json_prints_the_project_state_and_exits_0()
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Json(HttpStatusCode.OK, Samples.State));

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Samples.State), JsonNode.Parse(run.Out)), run.Out);
        Assert.Empty(run.Error);
        var request = Assert.Single(_cli.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"http://localhost:8080/api/projects/{Samples.ProjectId}/state", request.Uri.ToString());
        Assert.Equal($"Bearer {CliTestHarness.Token}", request.Authorization);
    }

    [Fact]
    public async Task FR52_status_403_exits_3()
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Problem(HttpStatusCode.Forbidden, "FORBIDDEN", "Forbidden", "You are not allowed to do this."));

        var run = await _cli.RunAsync("status", Samples.ProjectId, "--json");

        Assert.Equal(ExitCodes.Auth, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Contains("403 Forbidden [FORBIDDEN]", run.Error, StringComparison.Ordinal);
        Assert.Contains("You are not allowed to do this.", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_status_human_output_shows_phase_status_and_name()
    {
        _cli.LoggedIn().Respond(r => FakeHttpHandler.Json(
            HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/state", StringComparison.Ordinal) ? Samples.State : Samples.Project));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Todo app", run.Out, StringComparison.Ordinal);
        Assert.Contains("execution", run.Out, StringComparison.Ordinal);
        Assert.Contains("building", run.Out, StringComparison.Ordinal);
        Assert.Contains("2/5 completed", run.Out, StringComparison.Ordinal);
        Assert.Contains("technical_specification", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Conflict, "REVIEW_IN_PROGRESS", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION", ExitCodes.Error)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", ExitCodes.Error)]
    [InlineData(HttpStatusCode.TooManyRequests, "RATE_LIMITED", ExitCodes.Error)]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL", ExitCodes.Error)]
    public async Task FR52_api_errors_map_to_exit_codes_and_print_the_error_code(HttpStatusCode status, string errorCode, int expectedExit)
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Problem(status, errorCode, "Something failed", "The detail."));

        var run = await _cli.RunAsync("review", Samples.ProjectId);

        Assert.Equal(expectedExit, run.ExitCode);
        Assert.Empty(run.Out);
        Assert.Contains($"[{errorCode}]", run.Error, StringComparison.Ordinal);
        Assert.Contains("Something failed", run.Error, StringComparison.Ordinal);
        Assert.Contains("The detail.", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_error_without_problem_body_still_maps_the_status()
    {
        _cli.LoggedIn().Respond(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var run = await _cli.RunAsync("review", Samples.ProjectId);

        Assert.Equal(ExitCodes.Conflict, run.ExitCode);
        Assert.Contains("409", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_rate_limited_rollback_reports_retry_after()
    {
        _cli.LoggedIn().Respond(_ =>
        {
            var response = FakeHttpHandler.Problem(HttpStatusCode.TooManyRequests, "RATE_LIMITED", "Too many rollbacks");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var run = await _cli.RunAsync("rollback", Samples.ProjectId, "2", "--reason", "bad plan");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("[RATE_LIMITED]", run.Error, StringComparison.Ordinal);
        Assert.Contains("Retry after 42s", run.Error, StringComparison.Ordinal);
        var request = Assert.Single(_cli.Requests);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"targetCheckpoint":2,"reason":"bad plan"}"""), JsonNode.Parse(request.Body!)), request.Body);
    }

    [Fact]
    public async Task FR52_validation_errors_list_each_field()
    {
        _cli.LoggedIn().Respond(_ => FakeHttpHandler.Json(
            HttpStatusCode.BadRequest,
            """{"title":"Validation failed","status":400,"errorCode":"VALIDATION","errors":{"name":["Name is required."]}}"""));

        var run = await _cli.RunAsync("project", "init", "--name", "x", "--idea", "y");

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("[VALIDATION]", run.Error, StringComparison.Ordinal);
        Assert.Contains("name: Name is required.", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_not_logged_in_exits_3_without_calling_the_api()
    {
        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Auth, run.ExitCode);
        Assert.Contains("aide login", run.Error, StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task FR52_unreachable_api_exits_1()
    {
        _cli.LoggedIn().Respond(_ => throw new HttpRequestException("Connection refused"));

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("Could not reach the AIDE API at http://localhost:8080/", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR52_non_json_success_body_exits_1()
    {
        _cli.LoggedIn().Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });

        var run = await _cli.RunAsync("status", Samples.ProjectId);

        Assert.Equal(ExitCodes.Error, run.ExitCode);
        Assert.Contains("could not read", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Forbidden, ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Conflict, ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, ExitCodes.Error)]
    [InlineData(HttpStatusCode.NotFound, ExitCodes.Error)]
    [InlineData(HttpStatusCode.TooManyRequests, ExitCodes.Error)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ExitCodes.Error)]
    public void FR52_status_code_to_exit_code(HttpStatusCode status, int expected) =>
        Assert.Equal(expected, ExitCodes.ForStatus(status));
}
