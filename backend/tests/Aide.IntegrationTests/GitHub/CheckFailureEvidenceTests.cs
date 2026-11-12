using System.Net;
using System.Text.Json;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Releases;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.GitHub;

/// <summary>
/// FR-236 end to end with the Fake LLM and <see cref="FakeGitHubApi"/>: a failed required GitHub Actions check run gets its
/// output, annotations and job log tail as the finding's evidence (redacted) and in <c>external_checks.failures</c>; a log
/// GitHub refuses (403, no <c>actions: read</c>) leaves the rest and the review goes on.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CheckFailureEvidenceTests(AideApiFactory factory)
{
    private const string Repo = "/repos/acme/widgets";
    private const string DetailsUrl = "https://github.com/acme/widgets/actions/runs/7/job/9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Reviewing() =>
        AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    private static MergeFlowTests.GitHubState FailingBuild(BareRemote remote, HttpStatusCode logStatus)
    {
        var github = new MergeFlowTests.GitHubState(remote) { RequiredChecks = ["build"] };
        github.CheckRuns =
        [
            new { id = 9, name = "build", status = "completed", conclusion = "failure", details_url = DetailsUrl, app = new { slug = "github-actions" } },
        ];
        github.Api
            .On(HttpMethod.Get, Repo + "/check-runs/9", HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = 9,
                name = "build",
                output = new { title = "Build failed", summary = "1 error in frontend/src/app/page.tsx" },
            }))
            .On(HttpMethod.Get, Repo + "/check-runs/9/annotations", HttpStatusCode.OK, JsonSerializer.Serialize(new[]
            {
                new { path = "frontend/src/app/page.tsx", start_line = 3, annotation_level = "failure", message = "Module not found: Can't resolve '@/lib/db'", title = (string?)null },
            }))
            .On(HttpMethod.Get, Repo + "/actions/jobs/9/logs", _ => logStatus == HttpStatusCode.OK
                ? (HttpStatusCode.OK, string.Join('\n', Enumerable.Range(1, 150).Select(i => i switch
                {
                    140 => "2026-11-12T10:00:00Z ##[error]Process completed with exit code 1. GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789",
                    _ => $"2026-11-12T10:00:00Z build step {i}",
                })))
                : (logStatus, """{"message":"Resource not accessible by integration"}"""));
        return github;
    }

    private static async Task<(Domain.Entities.ComplianceReport Report, ReviewFinding Finding)> ReviewAsync(WebApplicationFactory<Program> app, MergeFlowTests.GitHubState github)
    {
        using var fixture = await MergeFlowTests.GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = MergeFlowTests.BranchOf(fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        return (report, Assert.Single(report.Violations, v => v.Message == "required check build failed"));
    }

    [Fact]
    public async Task FR236_A_failed_required_GitHub_Actions_check_gets_its_output_annotations_and_a_redacted_job_log_excerpt()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = FailingBuild(remote, HttpStatusCode.OK);
        await using var app = remote.App(Reviewing(), github.Api, out _);

        var (report, finding) = await ReviewAsync(app, github);

        Assert.Equal("failure:check:build", finding.FailureId);
        var evidence = finding.Evidence!;
        Assert.StartsWith($"build: {DetailsUrl}\nBuild failed\n1 error in frontend/src/app/page.tsx\nfrontend/src/app/page.tsx:3 failure Module not found: Can't resolve '@/lib/db'\n", evidence, StringComparison.Ordinal);
        Assert.Contains("##[error]Process completed with exit code 1.", evidence, StringComparison.Ordinal);
        Assert.EndsWith("build step 150", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", evidence, StringComparison.Ordinal);
        Assert.Equal(1, github.Api.Count(HttpMethod.Get, Repo + "/actions/jobs/9/logs"));

        var failure = Assert.Single(JsonDocument.Parse(report.ExternalChecks!).RootElement.GetProperty("failures").EnumerateArray());
        Assert.Equal(("failure:check:build", "check"), (failure.GetProperty("id").GetString(), failure.GetProperty("source").GetString()));
        Assert.Equal(evidence, failure.GetProperty("excerpt").GetString());
    }

    [Fact]
    public void FR240_Stored_failures_round_trip_and_an_old_format_row_reads_as_none()
    {
        var finding = new ReviewFinding(ReviewCategory.Testing, Severity.Major, "required check build failed", Evidence: "build\nError: x")
        {
            FailureId = "failure:check:build",
        };
        var records = FailureHistory.Records([finding], null);
        var checks = new Aide.Api.Features.Review.CollectedChecks([], null, false);

        var json = Aide.Api.Features.Review.CollectedChecks.Compose(checks, null, [finding], records);
        const string OldRow = """{"checks":[{"name":"build","conclusion":"failure","required":true,"detailsUrl":null}],"checksWaitedSeconds":null,"checksTimedOut":false,"preview":null}""";

        Assert.Equal(records, Aide.Api.Features.Review.CollectedChecks.StoredFailures(json));
        Assert.Equal("build\nError: x", JsonDocument.Parse(json!).RootElement.GetProperty("failures")[0].GetProperty("excerpt").GetString());
        Assert.Empty(Aide.Api.Features.Review.CollectedChecks.StoredFailures(OldRow));
        Assert.Empty(Aide.Api.Features.Review.CollectedChecks.StoredFailures("not json"));
        Assert.Empty(Aide.Api.Features.Review.CollectedChecks.StoredFailures(null));
        Assert.False(JsonDocument.Parse(Aide.Api.Features.Review.CollectedChecks.Compose(checks, null)!).RootElement.TryGetProperty("failures", out var none) && none.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task FR236_A_job_log_GitHub_refuses_is_skipped_and_the_review_keeps_the_output_and_annotations()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = FailingBuild(remote, HttpStatusCode.Forbidden);
        await using var app = remote.App(Reviewing(), github.Api, out _);

        var (report, finding) = await ReviewAsync(app, github);

        // The review was recorded; the finding keeps the check output and annotations without the log.
        Assert.NotNull(report.ExternalChecks);
        Assert.Contains("Module not found: Can't resolve '@/lib/db'", finding.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("build step", finding.Evidence, StringComparison.Ordinal);
    }
}
