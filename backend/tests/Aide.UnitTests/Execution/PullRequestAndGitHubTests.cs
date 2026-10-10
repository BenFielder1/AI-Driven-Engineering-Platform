using System.Net;
using System.Text;
using Aide.Domain.Artifacts;
using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Infrastructure.Git;

namespace Aide.UnitTests.Execution;

/// <summary>FR-34–FR-36: the project PR description, GitHub remotes (token handling) and failure classification.</summary>
public class PullRequestAndGitHubTests
{
    private const string Token = "ghp_unitTestToken0123456789abcdef";
    private static readonly Guid ProjectId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static PullRequestInput Input(string? spec = "# Todo API\n\n## Summary\nManage todos over HTTP.\n\n## Details\nMore.", params PullRequestStep[] steps) =>
        new(
            ProjectId,
            "Todo API",
            5,
            spec,
            2,
            ArtifactType.ImplementationPlan,
            3,
            steps.Length > 0
                ? steps
                :
                [
                    new PullRequestStep(2, "Routes", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ["src/routes.js"]),
                    new PullRequestStep(1, "Server", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ["src/server.js", "package.json"]),
                ]);

    // ---------- FR-36: description ----------

    [Fact]
    public void FR36_Title_follows_the_old_stack_project_PR_title()
    {
        Assert.Equal("[AIDE] Todo API — Implementation (2 steps)", PullRequestDescription.Title("Todo API", 2));
        Assert.Equal("[AIDE] Todo API — Implementation (1 step)", PullRequestDescription.Title("Todo API", 1));
    }

    [Fact]
    public void FR36_Title_is_one_line_and_within_GitHubs_limit()
    {
        var title = PullRequestDescription.Title(new string('x', 400) + "\nInjected", 12);

        Assert.True(title.Length <= PullRequestDescription.MaxTitleLength);
        Assert.DoesNotContain('\n', title);
        Assert.EndsWith("— Implementation (12 steps)", title, StringComparison.Ordinal);
    }

    [Fact]
    public void FR36_Body_is_generated_from_the_spec_the_plan_and_the_step_list()
    {
        var body = PullRequestDescription.Body(Input());

        Assert.Contains("## AIDE — Todo API (implementation complete)", body, StringComparison.Ordinal);
        Assert.Contains("Technical specification v2:", body, StringComparison.Ordinal);
        Assert.Contains("> Manage todos over HTTP.", body, StringComparison.Ordinal);
        Assert.Contains("### Plan: implementation_plan v3 (2 steps)", body, StringComparison.Ordinal);
        var step1 = body.IndexOf("| 1 | Server | `aaaaaaaa` | 2 |", StringComparison.Ordinal);
        var step2 = body.IndexOf("| 2 | Routes | `bbbbbbbb` | 1 |", StringComparison.Ordinal);
        Assert.True(step1 > 0 && step2 > step1, body);
        Assert.Contains("### Files changed (3)\n\n```text\npackage.json\nsrc/routes.js\nsrc/server.js\n```", body, StringComparison.Ordinal);
        Assert.Contains($"| Project ID | `{ProjectId:D}` |", body, StringComparison.Ordinal);
        Assert.Contains("| Project version | 5 |", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR36_Body_is_deterministic() =>
        Assert.Equal(PullRequestDescription.Body(Input()), PullRequestDescription.Body(Input()));

    [Fact]
    public void FR36_Agent_text_cannot_mention_users_inject_HTML_or_break_the_table()
    {
        var body = PullRequestDescription.Body(Input(
            "## Summary\nPing @octocat <script>alert(1)</script> | `x`",
            new PullRequestStep(1, "Hi @everyone | <img src=x>\n## Injected", "abc", ["a.js"])));

        Assert.DoesNotContain("@octocat", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@everyone", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n## Injected", body, StringComparison.Ordinal);
        Assert.Contains("| 1 | Hi &#64;everyone \\| &lt;img src=x&gt; ## Injected | `abc` | 1 |", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# Spec\n\n## Overview\nThe overview.\n\nSecond paragraph.", "The overview.")]
    [InlineData("# Spec\n\nIntro line one\nline two.\n\n## Summary\n", "Intro line one line two.")]
    [InlineData("# Spec\n\n- a list\n\n```\ncode\n```\n\nFirst prose.", "First prose.")]
    [InlineData("# Only a heading", null)]
    [InlineData("", null)]
    public void FR36_Spec_summary_prefers_the_summary_section_then_the_first_paragraph(string spec, string? expected) =>
        Assert.Equal(expected, PullRequestDescription.Summary(spec));

    [Fact]
    public void FR36_Body_without_a_spec_says_so_and_lists_at_most_100_files()
    {
        var files = Enumerable.Range(1, 150).Select(i => $"src/f{i:D3}.js").ToList();
        var body = PullRequestDescription.Body(Input(null, new PullRequestStep(1, "Many", null, files)));

        Assert.Contains("_No technical specification summary available._", body, StringComparison.Ordinal);
        Assert.Contains("| 1 | Many | — | 150 |", body, StringComparison.Ordinal);
        Assert.Contains("### Files changed (150)", body, StringComparison.Ordinal);
        Assert.Contains("src/f100.js\n... and 50 more", body, StringComparison.Ordinal);
        Assert.DoesNotContain("src/f101.js", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR36_Body_stays_within_GitHubs_body_limit()
    {
        var steps = Enumerable.Range(1, 400).Select(i => new PullRequestStep(i, new string('t', 500), null, [$"src/{i}.js"])).ToArray();

        var body = PullRequestDescription.Body(Input("## Summary\n" + new string('s', 5000), steps));

        Assert.True(body.Length <= PullRequestDescription.MaxBodyLength);
        Assert.Contains("| … | 200 more steps | | |", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fixes #12", "Fixes #‍12")]
    [InlineData("closes a/b#3 and GH-7", "closes a/b#‍3 and GH-‍7")]
    [InlineData("gh-9, #tag and # 4 stay", "gh-‍9, #tag and # 4 stay")]
    public void FR36_Issue_references_in_agent_text_are_neutralised(string text, string expected) =>
        Assert.Equal(expected, PullRequestDescription.Inline(text));

    [Fact]
    public void FR36_Agent_text_cannot_link_or_close_issues_in_the_body()
    {
        var body = PullRequestDescription.Body(Input(
            "## Summary\nFixes #12 and resolves acme/widgets#3.",
            new PullRequestStep(1, "Closes #4 (GH-5)", "abc", ["a.js"])));

        foreach (var reference in new[] { "#12", "widgets#3", "#4", "GH-5" })
        {
            Assert.DoesNotContain(reference, body, StringComparison.Ordinal);
        }

        Assert.Contains("> Fixes #‍12 and resolves acme/widgets#‍3.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR36_The_file_fence_is_longer_than_any_backtick_run_in_a_path()
    {
        Assert.Equal("```", PullRequestDescription.Fence(["src/a.js", "@org/x"]));
        Assert.Equal("````", PullRequestDescription.Fence(["```"]));
        Assert.Equal("``````", PullRequestDescription.Fence(["a`b", "x`````y"]));
    }

    [Fact]
    public void FR36_Listed_paths_cannot_close_the_fence_or_mention_anyone()
    {
        var body = PullRequestDescription.Body(Input(steps: new PullRequestStep(1, "Paths", "abc", ["@org/x", "```", "docs/a#1.md"])));

        // Paths with backticks are unsafe and never listed; the rest sit inside one fenced block, where GitHub links nothing.
        Assert.DoesNotContain("\n```\n```", body, StringComparison.Ordinal);
        var fenced = body[body.IndexOf("```text\n", StringComparison.Ordinal)..];
        Assert.StartsWith("```text\n@org/x\ndocs/a#1.md\n```\n", fenced, StringComparison.Ordinal);
    }

    // ---------- FR-36: when the PR may be requested again ----------

    [Theory]
    [InlineData(Phase.Execution, true, true, false, false, null)]
    [InlineData(Phase.Review, true, true, false, false, null)]
    [InlineData(Phase.Planning, true, true, false, false, "PHASE_PRECONDITION_FAILED")]
    [InlineData(Phase.Release, true, true, false, false, "PHASE_PRECONDITION_FAILED")]
    [InlineData(Phase.Execution, false, false, false, false, "PHASE_PRECONDITION_FAILED")]
    [InlineData(Phase.Execution, true, false, false, false, "PHASE_PRECONDITION_FAILED")]
    [InlineData(Phase.Review, true, true, true, false, "PHASE_PRECONDITION_FAILED")]
    [InlineData(Phase.Review, true, true, false, true, "CONFLICT")]
    [InlineData(Phase.Execution, true, true, false, true, "CONFLICT")]
    public void FR36_The_PR_can_be_requested_with_all_steps_completed_in_execution_or_review_without_a_recorded_PR_or_pending_job(
        Phase phase, bool hasPlan, bool allCompleted, bool recorded, bool jobPending, string? errorCode)
    {
        var result = PullRequestRules.CanRequest(new PullRequestFacts(phase, hasPlan, allCompleted, recorded, jobPending));

        Assert.Equal(errorCode, result.Error?.Code);
    }

    // ---------- FR-35: GitHub remotes and the token ----------

    [Fact]
    public void FR35_The_clone_URL_comes_from_system_config_and_never_holds_the_token()
    {
        var remote = GitHubRemotes.Create(new GitHubGitOptions(), new GitHubRepo("acme", "widgets"), Token);

        Assert.Equal("https://github.com/acme/widgets.git", remote.Url);
        Assert.Equal("https://github.com/acme/widgets.git", remote.ToString());
        Assert.DoesNotContain(Token, remote.Url, StringComparison.Ordinal);

        var enterprise = GitHubRemotes.Create(new GitHubGitOptions { GitBaseUrl = "https://ghe.example.com/git" }, new GitHubRepo("acme", "widgets"), Token);
        Assert.Equal("https://ghe.example.com/git/acme/widgets.git", enterprise.Url);
    }

    [Fact]
    public void FR35_The_token_travels_only_as_a_host_scoped_auth_header_in_the_git_config_environment()
    {
        var remote = GitHubRemotes.Create(new GitHubGitOptions(), new GitHubRepo("acme", "widgets"), Token);
        var env = remote.ConfigEnvironment;
        var count = int.Parse(env["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture);
        var settings = Enumerable.Range(0, count).ToDictionary(i => env[$"GIT_CONFIG_KEY_{i}"], i => env[$"GIT_CONFIG_VALUE_{i}"]);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));
        Assert.Equal($"Authorization: Basic {basic}", settings["http.https://github.com/.extraHeader"]);
        Assert.Equal("never", settings["protocol.allow"]);
        Assert.Equal("always", settings["protocol.https.allow"]);
        Assert.False(settings.ContainsKey("protocol.file.allow"));
        Assert.All(env.Keys, k => Assert.StartsWith("GIT_CONFIG_", k, StringComparison.Ordinal));
    }

    [Fact]
    public void FR35_Local_bare_repositories_are_reachable_only_with_the_test_switch()
    {
        var remote = GitHubRemotes.Create(new GitHubGitOptions { GitBaseUrl = "file:///tmp/remotes/", AllowInsecureGitBaseUrl = true }, new GitHubRepo("acme", "widgets"), Token);

        Assert.Equal("file:///tmp/remotes/acme/widgets.git", remote.Url);
        Assert.Contains("always", remote.ConfigEnvironment.Where(e => e.Key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).Select(e => e.Value));
        Assert.Contains("protocol.file.allow", remote.ConfigEnvironment.Values);
    }

    [Fact]
    public void FR35_Redact_removes_the_token_in_every_form()
    {
        var remote = GitHubRemotes.Create(new GitHubGitOptions(), new GitHubRepo("acme", "widgets"), Token);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));

        var redacted = remote.Redact($"fatal: {Token} / Authorization: Basic {basic} / {basic}");

        Assert.DoesNotContain(Token, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(basic, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    // ---------- FR-34 / FR-36: transient vs permanent ----------

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/acme/widgets.git/': Could not resolve host: github.com", true)]
    [InlineData("fatal: unable to access '...': The requested URL returned error: 502", true)]
    [InlineData("git push timed out after 120 s.", true)]
    [InlineData("remote: Invalid username or token. fatal: Authentication failed for 'https://github.com/acme/widgets.git/'", false)]
    [InlineData("remote: Permission to acme/widgets.git denied to bot.", false)]
    [InlineData("remote: Repository not found.", false)]
    [InlineData("! [rejected] aide/x -> aide/x (fetch first)", false)]
    [InlineData("! [remote rejected] aide/x -> aide/x (protected branch hook declined)", false)]
    public void FR34_Git_failures_are_transient_unless_a_retry_cannot_help(string error, bool transient) =>
        Assert.Equal(transient, GitHubFailures.IsTransientGitError(error));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false, true)]
    [InlineData(HttpStatusCode.BadGateway, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, true)]
    [InlineData(HttpStatusCode.Forbidden, true, true)]
    [InlineData(HttpStatusCode.Forbidden, false, false)]
    [InlineData(HttpStatusCode.Unauthorized, false, false)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false, false)]
    public void FR36_GitHub_API_errors_are_transient_only_for_5xx_and_rate_limits(HttpStatusCode status, bool rateLimited, bool transient) =>
        Assert.Equal(transient, GitHubFailures.IsTransientStatus(status, rateLimited));
}
