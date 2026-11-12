using System.Net;
using System.Text;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.GitLab;
using Aide.UnitTests.Deployments;

namespace Aide.UnitTests.GitHosts;

/// <summary>
/// FR-236, SPEC §8: fetching failed checks' logs against stub handlers (no real host): the GitLab job trace, the Bitbucket
/// failed step log, the last-5-MB cap, per-check and per-part timeouts.
/// </summary>
public sealed class CheckFailureLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string type = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static readonly CheckObservation Failed = new("build", CheckRunStatus.Completed, CheckRunConclusion.Failure, true, null) { RunId = "9", App = "github-actions" };

    [Fact]
    public async Task FR236_The_GitLab_job_trace_is_read_and_a_missing_job_is_null()
    {
        var handler = new StubHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/jobs/42/trace", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, "$ npm test\nFAIL src/a.test.ts")
            : Text(HttpStatusCode.NotFound, """{"message":"404 Not found"}""", "application/json"));
        var client = new GitLabClients(new StubFactory(handler), new StaticMonitor<GitLabOptions>(new GitLabOptions()), TimeProvider.System)
            .ForInstance(new Uri("https://gitlab.example.com/"));

        var trace = await client.GetJobTraceAsync("glpat-token", "77", 42, CheckFailureLogs.MaxBytes, Ct);
        var missing = await client.GetJobTraceAsync("glpat-token", "77", 43, CheckFailureLogs.MaxBytes, Ct);

        Assert.Equal("$ npm test\nFAIL src/a.test.ts", trace);
        Assert.Null(missing);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/77/jobs/42/trace", handler.Requests[0].Url);
        Assert.Equal("Bearer glpat-token", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task FR236_The_Bitbucket_log_is_the_first_failed_steps_log()
    {
        var handler = new StubHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/steps/", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, """{"values":[{"uuid":"{s-1}","state":{"result":{"name":"SUCCESSFUL"}}},{"uuid":"{s-2}","state":{"result":{"name":"FAILED"}}}]}""", "application/json")
            : r.RequestUri.AbsoluteUri.Contains("%7Bs-2%7D/log", StringComparison.Ordinal)
                ? Text(HttpStatusCode.OK, "+ npm run build\nError: build failed")
                : Text(HttpStatusCode.NotFound, "{}", "application/json"));
        var client = new BitbucketClients(new StubFactory(handler), new StaticMonitor<BitbucketOptions>(new BitbucketOptions()), new BitbucketRateLimiter(TimeProvider.System), TimeProvider.System)
            .Create(Guid.NewGuid());

        var log = await client.GetFailedStepLogAsync(BitbucketAuth.OAuth("t"), "acme/todo", 42, CheckFailureLogs.MaxBytes, Ct);

        Assert.Equal("+ npm run build\nError: build failed", log);
        Assert.StartsWith("https://api.bitbucket.org/2.0/repositories/acme/todo/pipelines/42/steps/", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR236_A_long_log_keeps_its_last_bytes_with_the_cut_noted()
    {
        var log = string.Join('\n', Enumerable.Range(1, 1000).Select(i => $"line {i:D4}"));

        var text = await CheckFailureLogs.ReadTailAsync(new MemoryStream(Encoding.UTF8.GetBytes(log)), 1000, Ct);

        Assert.StartsWith("… (log cut: the first ", text, StringComparison.Ordinal);
        Assert.EndsWith("line 1000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0001", text, StringComparison.Ordinal);
        Assert.InRange(text.Length, 900, 1060);
    }

    [Fact]
    public async Task FR236_A_log_over_5_MB_keeps_the_last_5_MB()
    {
        var line = new string('x', 99) + "\n";
        var log = string.Concat(Enumerable.Repeat(line, 60_000)) + "Error: the end";
        using var content = new StringContent(log, Encoding.UTF8);

        var text = await CheckFailureLogs.ReadTextAsync(content, CheckFailureLogs.MaxBytes, Ct);

        Assert.EndsWith("Error: the end", text, StringComparison.Ordinal);
        Assert.StartsWith("… (log cut: the first ", text, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), CheckFailureLogs.MaxBytes - 200, CheckFailureLogs.MaxBytes + 100);
    }

    [Fact]
    public async Task FR236_A_check_whose_fetch_times_out_is_a_warning_and_the_others_keep_their_excerpts()
    {
        var slow = Failed with { Name = "slow" };

        var outcomes = await CheckFailureLogs.FanOutAsync(
            [Failed, slow],
            async (check, ct) =>
            {
                if (check.Name == "slow")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return (new CheckFailureDetail(null, null, [], "Error: x"), []);
            },
            ExternalCheckRules.Excerpt,
            TimeSpan.FromMilliseconds(100),
            Ct);

        Assert.Equal("build\nError: x", outcomes[0].Excerpt!.Text);
        Assert.Null(outcomes[1].Excerpt);
        Assert.Contains("timed out", Assert.Single(outcomes[1].Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR236_A_slow_GitHub_job_log_times_out_alone_and_the_output_and_annotations_are_kept()
    {
        var handler = new SlowLogHandler();
        var api = new GitHubApi(new AnyHostStubFactory(handler), TimeProvider.System);

        var result = await api.GetCheckFailureAsync("t", "acme", "widgets", Failed, Ct, partTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal(("Build failed", "1 error", (string?)null), (result.Detail.Title, result.Detail.Summary, result.Detail.Log));
        Assert.Equal("src/a.ts", Assert.Single(result.Detail.Annotations).Path);
        Assert.Contains("job log", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    /// <summary>GitHub's check run and annotations at once; the job log never answers (until cancelled).</summary>
    private sealed class SlowLogHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/logs", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return path.EndsWith("/annotations", StringComparison.Ordinal)
                ? Text(HttpStatusCode.OK, """[{"path":"src/a.ts","start_line":2,"annotation_level":"failure","message":"Cannot find name 'x'"}]""", "application/json")
                : Text(HttpStatusCode.OK, """{"output":{"title":"Build failed","summary":"1 error"}}""", "application/json");
        }
    }
}
