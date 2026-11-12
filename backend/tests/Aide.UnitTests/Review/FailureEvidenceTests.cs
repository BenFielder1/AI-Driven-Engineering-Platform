using System.Globalization;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Bitbucket;
using Aide.UnitTests.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Review;

/// <summary>
/// FR-236–FR-240, §4.37, §4.38: failure excerpts and fingerprints, failure findings in the review prompt, the v9 prompt,
/// evidence blocks on fix steps, and repeated failures across cycles.
/// </summary>
public sealed class FailureEvidenceTests
{
    private const string ModuleNotFound = "Module not found: Can't resolve '@/lib/db'";

    private static readonly HashSet<string> EmptyBase = new(StringComparer.OrdinalIgnoreCase);

    private static string Lines(int count, Func<int, string> line) =>
        string.Join('\n', Enumerable.Range(1, count).Select(line));

    private static string[] Split(string text) => text.Split('\n');

    // ---------- §4.37 failure fingerprint ----------

    [Fact]
    public void FR240_The_two_cannot_find_module_zod_lines_have_the_same_fingerprint()
    {
        var first = FailureFingerprint.Of("failure:preview:vercel", "12:01:03 Error: Cannot find module 'zod' (/vercel/path0/a1b2c3d4e5/x.js)");
        var second = FailureFingerprint.Of("failure:preview:vercel", "12:09:44 Error: Cannot find module 'zod' (/vercel/path0/9f8e7d6c5b/x.js)");

        Assert.Equal(first, second);
    }

    [Fact]
    public void FR240_Cannot_find_module_and_a_type_error_have_different_fingerprints() =>
        Assert.NotEqual(
            FailureFingerprint.Of("failure:preview:vercel", "Cannot find module 'zod'"),
            FailureFingerprint.Of("failure:preview:vercel", "Type error: Property 'id' does not exist"));

    [Fact]
    public void FR240_Without_an_error_line_the_fingerprint_is_the_failure_id_alone() =>
        Assert.Equal("failure:check:build", FailureFingerprint.Of("failure:check:build", "Compiling...\nDone in 3s"));

    [Theory]
    [InlineData("2026-11-12T10:15:00Z Error: boom", "# Error: boom")]
    [InlineData("Error: open /tmp/build-123/out.js failed", "Error: open # failed")]
    [InlineData("Error at commit 0123abcdef9", "Error at commit #")]
    [InlineData("Error: exit code 137", "Error: exit code #")]
    public void FR240_Timestamps_temp_paths_hex_runs_and_digits_become_hashes(string line, string expected) =>
        Assert.Equal(expected, FailureFingerprint.Normalise(line));

    // ---------- §4.38 failure excerpt ----------

    [Fact]
    public void FR236_The_excerpt_is_10_lines_before_the_error_30_from_it_and_the_last_20_joined_by_lines_omitted()
    {
        var log = Lines(200, i => i == 101 ? $"line {i}: Error: boom" : $"line {i}");

        var excerpt = Split(FailureExcerpt.Build(log));

        Assert.Equal(61, excerpt.Length);
        Assert.Equal("line 91", excerpt[0]);
        Assert.Equal("line 101: Error: boom", excerpt[10]);
        Assert.Equal("line 130", excerpt[39]);
        Assert.Equal("… 50 lines omitted", excerpt[40]);
        Assert.Equal("line 181", excerpt[41]);
        Assert.Equal("line 200", excerpt[^1]);
    }

    [Fact]
    public void FR236_A_window_that_reaches_the_tail_is_joined_without_a_note()
    {
        var log = Lines(50, i => i == 5 ? "npm ERR! missing script: build" : $"line {i}");

        var excerpt = Split(FailureExcerpt.Build(log));

        Assert.Equal(50, excerpt.Length);
        Assert.DoesNotContain(excerpt, l => l.Contains("omitted", StringComparison.Ordinal));
    }

    [Fact]
    public void FR236_Without_an_error_line_the_excerpt_is_the_last_60_lines()
    {
        var excerpt = Split(FailureExcerpt.Build(Lines(100, i => $"step {i} ok")));

        Assert.Equal(60, excerpt.Length);
        Assert.Equal(("step 41 ok", "step 100 ok"), (excerpt[0], excerpt[^1]));
    }

    [Fact]
    public void FR236_ANSI_codes_are_stripped()
    {
        var excerpt = FailureExcerpt.Build("\u001b[31mError:\u001b[0m build \u001b[1mfailed\u001b[22m");

        Assert.Equal("Error: build failed", excerpt);
    }

    [Fact]
    public void FR236_Secrets_are_redacted_again_when_the_excerpt_is_built()
    {
        var excerpt = FailureExcerpt.Build("Error: auth failed for AKIAIOSFODNN7EXAMPLE");

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", excerpt, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:aws_access_key_id]", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void FR236_Up_to_10_annotations_are_listed_as_path_line_level_message()
    {
        var annotations = Enumerable.Range(1, 12).Select(i => new FailureAnnotation($"src/f{i}.ts", i, "failure", $"Problem {i}")).ToList();

        var excerpt = Split(FailureExcerpt.Build(null, annotations));

        Assert.Equal(10, excerpt.Length);
        Assert.Equal("src/f1.ts:1 failure Problem 1", excerpt[0]);
        Assert.Equal("src/f10.ts:10 failure Problem 10", excerpt[^1]);
    }

    [Fact]
    public void FR236_An_excerpt_is_capped_at_6000_characters()
    {
        var excerpt = FailureExcerpt.Build(Lines(60, i => $"Error {i}: " + new string('x', 300)));

        Assert.InRange(excerpt.Length, 1, FailureExcerpt.MaxPerFailure);
        Assert.StartsWith("Error 1: ", excerpt, StringComparison.Ordinal);
        Assert.Matches(@"… \d+ lines omitted$", excerpt);
    }

    [Fact]
    public void FR236_Over_18000_characters_failures_keep_their_excerpts_in_severity_order_and_the_rest_are_cut_to_their_first_error_line()
    {
        var big = "prefix\n" + Lines(20, i => i == 1 ? "Error: first error line" : new string('y', 280));
        ReviewFinding Failure(string id, Severity severity) =>
            new(ReviewCategory.Testing, severity, id, Evidence: big) { FailureId = id };
        var findings = new[]
        {
            Failure("failure:check:minor", Severity.Minor),
            Failure("failure:check:major", Severity.Major),
            new ReviewFinding(ReviewCategory.Security, Severity.Critical, "secret", Evidence: big),
            Failure("failure:check:crit-a", Severity.Critical),
            Failure("failure:check:crit-b", Severity.Critical),
            Failure("failure:check:crit-c", Severity.Critical),
        };

        var capped = FailureExcerpt.CapAll(findings);

        Assert.Equal(findings.Select(f => f.Message), capped.Select(f => f.Message));
        Assert.Equal(big, capped[3].Evidence);
        Assert.Equal(big, capped[4].Evidence);
        Assert.Equal(big, capped[5].Evidence);
        Assert.Equal("Error: first error line", capped[0].Evidence);
        Assert.Equal("Error: first error line", capped[1].Evidence);
        Assert.Equal(big, capped[2].Evidence); // not a failure: unchanged
        Assert.True(capped.Where(f => f.FailureId is not null).Sum(f => f.Evidence!.Length) <= FailureExcerpt.MaxTotal);
    }

    // ---------- FR-236: check details as evidence ----------

    private static readonly CheckObservation FailedBuild =
        new("build", CheckRunStatus.Completed, CheckRunConclusion.Failure, true, "https://github.com/acme/widgets/actions/runs/7/job/9") { RunId = "9", App = "github-actions" };

    [Fact]
    public void FR236_A_failed_required_GitHub_Actions_check_has_its_output_annotations_and_the_last_log_lines_redacted_as_evidence()
    {
        var log = Lines(120, i => i == 118 ? "Error: Process completed with exit code 1. token ghp_abcdefghijklmnopqrstuvwxyz0123456789" : $"build line {i}");
        var detail = new CheckFailureDetail("Build failed", "1 error", [new FailureAnnotation("frontend/src/app/page.tsx", 3, "failure", ModuleNotFound)], log);

        var finding = Assert.Single(ExternalCheckRules.Findings([FailedBuild], Excerpts(FailedBuild, detail)));

        Assert.Equal("failure:check:build", finding.FailureId);
        var evidence = finding.Evidence!;
        Assert.StartsWith("build: https://github.com/acme/widgets/actions/runs/7/job/9\nBuild failed\n1 error\nfrontend/src/app/page.tsx:3 failure " + ModuleNotFound, evidence, StringComparison.Ordinal);
        Assert.Contains("build line 120", evidence, StringComparison.Ordinal);
        Assert.Contains("Error: Process completed with exit code 1.", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void FR236_A_failed_check_that_is_not_required_is_still_dropped() =>
        Assert.Empty(ExternalCheckRules.Findings(
            [FailedBuild with { Required = false }],
            Excerpts(FailedBuild, new(null, null, [], "Error: x"))));

    [Fact]
    public void FR236_Without_details_after_a_403_404_or_timeout_the_finding_keeps_the_name_and_URL_evidence()
    {
        var finding = Assert.Single(ExternalCheckRules.Findings([FailedBuild], new Dictionary<string, FailureExcerptResult>()));

        Assert.Equal("build: https://github.com/acme/widgets/actions/runs/7/job/9", finding.Evidence);
    }

    [Fact]
    public void FR236_A_failed_GitLab_job_test_has_the_trace_tail_as_evidence()
    {
        var checks = CommitStatusFindings.FromGitLab([new GitLabCommitStatus("test", "failed", false, "https://gitlab.example.com/acme/todo/-/jobs/42", "42")], pipelinesMustSucceed: true);
        var trace = Lines(80, i => i == 79 ? "FAIL src/todo.test.ts: expected 2 got 3" : $"$ step {i}");

        var finding = Assert.Single(CommitStatusFindings.Findings(checks, CommitStatusHost.Gitlab, Excerpts(checks[0], new(null, null, [], trace))));

        Assert.Equal(("42", "failure:check:test"), (checks[0].RunId, finding.FailureId));
        Assert.StartsWith("test: https://gitlab.example.com/acme/todo/-/jobs/42\n", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("FAIL src/todo.test.ts: expected 2 got 3", finding.Evidence, StringComparison.Ordinal);
        Assert.EndsWith("$ step 80", finding.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bitbucket.org/acme/todo/addon/pipelines/home#!/results/42", 42)]
    [InlineData("https://bitbucket.org/acme/todo/pipelines/results/7", 7)]
    [InlineData("https://bitbucket.org/other/repo/addon/pipelines/home#!/results/42", null)]
    [InlineData("https://evil.example.com/acme/todo/pipelines/results/7", null)]
    [InlineData("http://bitbucket.org/acme/todo/pipelines/results/7", null)]
    [InlineData("https://ci.example.com/build/3", null)]
    [InlineData(null, null)]
    public void FR236_Only_a_bitbucket_org_Pipelines_status_of_this_repo_names_its_build_number(string? url, int? expected) =>
        Assert.Equal(expected, BitbucketClient.PipelineBuildNumber(url, "acme/todo"));

    private static Dictionary<string, FailureExcerptResult> Excerpts(CheckObservation check, CheckFailureDetail detail) =>
        new() { [check.Name] = ExternalCheckRules.Excerpt(check, detail)! };

    [Fact]
    public void FR240_The_fingerprint_ignores_the_check_title_and_two_different_log_errors_differ()
    {
        var first = Assert.Single(ExternalCheckRules.Findings([FailedBuild], Excerpts(FailedBuild, new("Build failed", null, [], "step 1\nError: Cannot find module 'zod'"))));
        var second = Assert.Single(ExternalCheckRules.Findings([FailedBuild], Excerpts(FailedBuild, new("Build failed", null, [], "step 1\nType error: Property 'id' does not exist"))));

        Assert.Equal("Error: Cannot find module 'zod'", first.FailureErrorLine);
        Assert.NotEqual(FailureFingerprint.Of(first), FailureFingerprint.Of(second));
    }

    [Fact]
    public void FR236_A_secret_split_by_an_ANSI_code_is_redacted_after_the_strip()
    {
        var excerpt = FailureExcerpt.Build("Error: key AKIAIOSFODNN7\u001b[0mEXAMPLE leaked");

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void FR237_Duplicate_failure_ids_are_disambiguated_in_order()
    {
        ReviewFinding F(string id) => new(ReviewCategory.Testing, Severity.Major, id) { FailureId = id };

        var result = FailureIds.Disambiguate([F("failure:check:ci-test"), F("failure:check:ci-test"), new(ReviewCategory.Security, Severity.Minor, "x"), F("failure:check:ci-test")]);

        Assert.Equal(["failure:check:ci-test", "failure:check:ci-test-2", null, "failure:check:ci-test-3"], result.Select(f => f.FailureId));
    }

    [Fact]
    public void FR237_Failure_findings_render_first_so_the_100_finding_cap_never_drops_one()
    {
        var others = Enumerable.Range(1, 120).Select(i => new ReviewFinding(ReviewCategory.Testing, Severity.Minor, $"other {i}"));
        var failure = new ReviewFinding(ReviewCategory.Testing, Severity.Major, "required check build failed", Evidence: "Error: x") { FailureId = "failure:check:build" };

        var text = ReviewCode.RenderFindings([.. others, failure], false, 0);

        Assert.Contains("failure id failure:check:build", text, StringComparison.Ordinal);
        Assert.DoesNotContain("other 100\n", text, StringComparison.Ordinal);
    }

    // ---------- FR-237: failure ids and the prompt ----------

    [Theory]
    [InlineData("check", "build", "failure:check:build")]
    [InlineData("check", "ci / test", "failure:check:ci-test")]
    [InlineData("preview", "Vercel", "failure:preview:vercel")]
    public void FR237_Failure_ids_are_stable(string source, string name, string expected) =>
        Assert.Equal(expected, FailureIds.Of(source, name));

    [Fact]
    public void FR237_A_failure_id_is_named_only_as_a_whole_token()
    {
        Assert.True(FailureIds.IsNamedIn("Fix FAILURE:CHECK:BUILD now.", "failure:check:build"));
        Assert.False(FailureIds.IsNamedIn("Fix failure:check:build-docker.", "failure:check:build"));
    }

    private static IReadOnlyList<ReviewFinding> VercelPreviewFailure(string log) =>
        RolloutFindings.For([new RolloutComponentOutcome(DeploymentComponent.Frontend, DeploymentProvider.Vercel, DeploymentStatus.Error, null, null, log, null)], null);

    private static readonly string VercelLog =
        "Running \"npm run build\"\n> next build\nCreating an optimized production build ...\nFailed to compile.\n\n./src/app/page.tsx\n" + ModuleNotFound + "\n\nhttps://nextjs.org/docs/messages/module-not-found\nError: Command \"npm run build\" exited with 1";

    [Fact]
    public void FR237_A_failed_Vercel_preview_lists_failure_preview_vercel_with_the_module_not_found_line_in_its_excerpt()
    {
        var findings = VercelPreviewFailure(VercelLog);

        var text = ReviewCode.RenderFindings(findings, allowlistChecked: false, endpointCount: 0);

        var finding = Assert.Single(findings);
        Assert.Equal("failure:preview:vercel", finding.FailureId);
        Assert.Contains("- [architecture/critical] failure id failure:preview:vercel (preview deployment): Vercel preview build failed\n", text, StringComparison.Ordinal);
        var excerpt = text[text.IndexOf("Evidence excerpt:", StringComparison.Ordinal)..];
        Assert.Contains(ModuleNotFound, excerpt, StringComparison.Ordinal);
        Assert.Contains("```\n", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void FR237_Two_failed_components_on_one_platform_get_ids_with_the_component()
    {
        var findings = RolloutFindings.For(
        [
            new RolloutComponentOutcome(DeploymentComponent.Frontend, DeploymentProvider.Railway, DeploymentStatus.Error, null, null, "Error: a", null),
            new RolloutComponentOutcome(DeploymentComponent.Backend, DeploymentProvider.Railway, DeploymentStatus.Error, null, null, "Error: b", null),
        ], null);

        Assert.Equal(["failure:preview:railway-frontend", "failure:preview:railway-backend"], findings.Select(f => f.FailureId));
    }

    [Fact]
    public void FR237_A_preview_id_does_not_depend_on_whether_the_other_component_on_the_platform_failed()
    {
        var findings = RolloutFindings.For(
        [
            new RolloutComponentOutcome(DeploymentComponent.Frontend, DeploymentProvider.Railway, DeploymentStatus.Ready, null, null, null, null),
            new RolloutComponentOutcome(DeploymentComponent.Backend, DeploymentProvider.Railway, DeploymentStatus.Error, null, null, "Error: b", null),
        ], null);

        Assert.Equal("failure:preview:railway-backend", Assert.Single(findings).FailureId);
    }

    [Fact]
    public void FR237_A_secrets_finding_renders_exactly_as_in_0_6_0()
    {
        var finding = new ReviewFinding(ReviewCategory.Security, Severity.Critical, "Possible secret (github_token) committed", "src/key.ts", Evidence: "line 3");

        var text = ReviewCode.RenderFindings([finding], allowlistChecked: true, endpointCount: 2);

        Assert.Equal(
            "Checks run: secret scan; dependency allowlist; 2 specification endpoint path(s).\n- [security/critical] Possible secret (github_token) committed (src/key.ts)",
            text);
    }

    [Fact]
    public void FR237_A_finding_without_a_failure_id_is_stored_without_one()
    {
        var json = JsonSerializer.Serialize(new ReviewFinding(ReviewCategory.Security, Severity.Minor, "m"));

        Assert.DoesNotContain("FailureId", json, StringComparison.Ordinal);
    }

    // ---------- FR-238: the v9 prompt ----------

    [Fact]
    public void FR238_The_code_review_prompt_v9_has_the_failure_step_rules()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.CodeReview);

        Assert.Equal("9", template.Version);
        Assert.Contains("failure id", template.System, StringComparison.Ordinal);
        Assert.Contains("quotes the key error line", template.System, StringComparison.Ordinal);
        Assert.Contains("says how to verify", template.System, StringComparison.Ordinal);
        Assert.Contains("\"reproduce and fix the failure\" is not acceptable", template.System, StringComparison.Ordinal);
        Assert.Contains("`repeated (n cycles)`", template.System, StringComparison.Ordinal);
        Assert.Contains("a different approach", template.System, StringComparison.Ordinal);
    }

    private static readonly string[] Categories = ["feature_coverage", "api_contracts", "data_model", "architecture", "security", "testing"];

    private static string ReviewReply(params object[] steps) => JsonSerializer.Serialize(new
    {
        categoryScores = Categories
            .Select(category => new { category, score = 0.8m, evidence = Array.Empty<object>() }),
        violations = Array.Empty<object>(),
        optimizationPlan = new { steps },
        summary = "s",
    });

    private static object ReplyStep(int number, string title, string task, string path, string? agent = null, params int[] dependencies) => new
    {
        number,
        title,
        task,
        files = new[] { new { path, action = "modify" } },
        dependencies,
        agent,
    };

    [Fact]
    public void FR238_A_reply_step_naming_failure_preview_vercel_is_stored_with_its_evidence_block()
    {
        var failures = FailureEvidenceSteps.From(VercelPreviewFailure(VercelLog));
        var reply = ReviewReply(ReplyStep(1, "Fix failure:preview:vercel", "Create src/lib/db.ts, which page.tsx imports as @/lib/db; verify with npm run build.", "src/app/page.tsx"));

        var result = CodeReviewOutput.Parse(reply, 5, failures: new FailureEvidenceCheck(failures, null));

        var step = Assert.Single(result.Value.OptimizationPlan);
        Assert.Contains("**Failure evidence** (added by Tevrin)\nFailure id: failure:preview:vercel\nSource: preview deployment\nExcerpt:\n", step.Task, StringComparison.Ordinal);
        Assert.Contains("    " + ModuleNotFound, step.Task, StringComparison.Ordinal);

        // The block survives rendering to the stored plan document and reading it back (what execution steps are made from).
        var stored = Assert.Single(PlanDocument.ParseMarkdown(result.Value.OptimizationPlanMarkdown!, 5).Value);
        Assert.Equal(step.Task, stored.Task);
    }

    // ---------- FR-239: evidence blocks ----------

    private static StackResolution NextVercel() => StackResolverTests.Resolve(Sel("fe-nextjs", "none", "none", "vcs-github", frontendHost: "deploy-vercel"));

    private static Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps, PlanAgents agents) =>
        PlanValidator.Validate(steps, maxFilesPerStep: 5, allowProtectedPaths: false, agents);

    private static PlanStep Step(int number, string task, string path, string? agent = null) =>
        new(number, $"Step {number}", task, [new DeclaredFile(path, FileAction.Modify)], [], Agent: agent);

    [Fact]
    public void FR239_With_two_failures_and_a_plan_naming_only_the_first_the_first_step_gets_its_block_and_a_fix_step_is_added_for_the_second()
    {
        var stack = NextVercel();
        var agents = PlanAgents.ForStackOptimization(stack, EmptyBase);
        var repo = new HashSet<string>(["frontend/package.json", "frontend/src/app/page.tsx", "frontend/src/lib/db.ts"], StringComparer.OrdinalIgnoreCase);
        var vercel = new FailureEvidence("failure:preview:vercel", "Vercel preview build failed", "./src/app/page.tsx\n" + ModuleNotFound);
        var build = new FailureEvidence("failure:check:build", "required check build failed", "build: https://ci\nfrontend/src/lib/db.ts:3 failure Type error: Property 'id' does not exist");
        var plan = Validate([Step(1, "Fix failure:preview:vercel by adding the missing module.", "frontend/src/app/page.tsx", StackAgents.FrontendEngineer)], agents).Value;

        var result = FailureEvidenceSteps.Attach(plan, new FailureEvidenceCheck([vercel, build], repo, 5), agents, s => Validate(s, agents));

        Assert.Empty(result.Dropped);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("Failure id: failure:preview:vercel", result.Steps[0].Task, StringComparison.Ordinal);
        Assert.DoesNotContain("failure:check:build", result.Steps[0].Task, StringComparison.Ordinal);
        var added = result.Steps[1];
        Assert.Equal("Fix: required check build failed", added.Title);
        Assert.Equal(("tevrin", "No step addressed failure:check:build"), (added.AddedBy, added.AddedReason));
        Assert.Contains("Failure id: failure:check:build", added.Task, StringComparison.Ordinal);
        Assert.Equal(["frontend/src/lib/db.ts"], added.Files.Select(f => f.Path));
        Assert.Equal(stack.Specialists.First(s => s.Owns.IsMatch("frontend/src/lib/db.ts")).Key, added.Agent);
        Assert.True(Validate(result.Steps, agents).IsSuccess);
    }

    [Fact]
    public void FR239_A_new_fix_step_for_a_preview_failure_is_owned_by_the_deploy_engineer_or_the_generalist_without_a_stack()
    {
        var repo = new HashSet<string>(["frontend/src/app/page.tsx", "README.md"], StringComparer.OrdinalIgnoreCase);
        var vercel = new FailureEvidence("failure:preview:vercel", "Vercel preview build failed", "./src/app/page.tsx\n" + ModuleNotFound);
        var stacked = PlanAgents.ForStackOptimization(NextVercel(), EmptyBase);

        var withStack = FailureEvidenceSteps.Attach(
            Validate([Step(1, "Update the docs.", "README.md", PlanAgents.Generalist)], stacked).Value, new FailureEvidenceCheck([vercel], repo), stacked, s => Validate(s, stacked));
        var noStack = FailureEvidenceSteps.Attach(
            Validate([Step(1, "Update the docs.", "README.md")], PlanAgents.None).Value, new FailureEvidenceCheck([vercel], repo), PlanAgents.None, s => Validate(s, PlanAgents.None));

        Assert.Equal((StackAgents.DeployEngineer, "frontend/src/app/page.tsx"), (withStack.Steps[1].Agent, withStack.Steps[1].Files[0].Path));
        Assert.Equal(PlanAgents.Generalist, noStack.Steps[1].Agent);
    }

    [Fact]
    public void FR239_When_the_excerpt_names_no_branch_file_the_block_goes_to_the_first_step()
    {
        var failure = new FailureEvidence("failure:preview:vercel", "Vercel preview build failed", "Error: out of memory");

        var result = FailureEvidenceSteps.Attach(
            Validate([Step(1, "Do it.", "README.md"), Step(2, "Do more.", "docs/a.md")], PlanAgents.None).Value,
            new FailureEvidenceCheck([failure], new HashSet<string>(["README.md"], StringComparer.OrdinalIgnoreCase)),
            PlanAgents.None,
            s => Validate(s, PlanAgents.None));

        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("Failure id: failure:preview:vercel", result.Steps[0].Task, StringComparison.Ordinal);
        Assert.Contains("\n\nAlso fix failure:preview:vercel: Vercel preview build failed\n\n**Failure evidence**", result.Steps[0].Task, StringComparison.Ordinal);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void FR239_Evidence_that_cannot_fit_is_reported_as_dropped()
    {
        var failure = new FailureEvidence("failure:check:build", "required check build failed", "Error: x");
        var task = "Fix failure:check:build. " + new string('t', FailureEvidenceSteps.MaxTaskLength - 40);

        var result = FailureEvidenceSteps.Attach(
            Validate([Step(1, task, "README.md")], PlanAgents.None).Value, new FailureEvidenceCheck([failure], null), PlanAgents.None, s => Validate(s, PlanAgents.None));

        Assert.Equal(["failure:check:build"], result.Dropped);
        Assert.Equal(task, result.Steps[0].Task);
    }

    [Fact]
    public void FR239_A_block_that_would_pass_the_step_text_limit_is_cut_from_the_top_with_the_cut_noted()
    {
        var excerpt = Lines(400, i => i.ToString("D3", CultureInfo.InvariantCulture) + " Error: " + new string('z', 40));
        var failure = new FailureEvidence("failure:check:build", "required check build failed", excerpt);
        var task = "Fix failure:check:build. " + new string('t', 5000);

        var result = FailureEvidenceSteps.Attach(
            Validate([Step(1, task, "README.md")], PlanAgents.None).Value, new FailureEvidenceCheck([failure], null), PlanAgents.None, s => Validate(s, PlanAgents.None));

        var text = result.Steps[0].Task;
        Assert.InRange(text.Length, task.Length + 1, FailureEvidenceSteps.MaxTaskLength);
        Assert.Matches(@"\n    … \d+ lines omitted\n", text);
        Assert.EndsWith("400 Error: " + new string('z', 40), text, StringComparison.Ordinal);
        Assert.DoesNotContain("001 Error", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR239_A_block_an_agent_copied_from_the_previous_plan_is_replaced_by_the_current_one()
    {
        var failure = new FailureEvidence("failure:check:build", "required check build failed", "Error: new");
        var copied = "Fix failure:check:build again.\n\n**Failure evidence** (added by Tevrin)\nFailure id: failure:check:build\nSource: CI check\nExcerpt:\n\n    Error: old";

        var result = FailureEvidenceSteps.Attach(
            Validate([Step(1, copied, "README.md")], PlanAgents.None).Value, new FailureEvidenceCheck([failure], null), PlanAgents.None, s => Validate(s, PlanAgents.None));

        Assert.DoesNotContain("Error: old", result.Steps[0].Task, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Steps[0].Task, @"\*\*Failure evidence\*\*"));
        Assert.EndsWith("    Error: new", result.Steps[0].Task, StringComparison.Ordinal);
    }

    [Fact]
    public void FR239_An_empty_plan_is_left_empty() =>
        Assert.Empty(FailureEvidenceSteps.Attach([], new FailureEvidenceCheck([new FailureEvidence("failure:check:x", "m", "Error")], null), null, s => Result.Success(s)).Steps);

    // ---------- FR-240: repeated failures ----------

    private static ReviewFinding VercelFailure(string firstError) =>
        Assert.Single(VercelPreviewFailure("> next build\n" + firstError + "\nError: Command \"npm run build\" exited with 1"));

    [Fact]
    public void FR240_Cycles_2_and_3_with_the_same_Vercel_error_mark_cycle_3_repeated_2_cycles_with_cycle_2s_fix_step()
    {
        var cycle2 = FailureHistory.Records([VercelFailure("12:01:03 Error: Cannot find module 'zod' (/vercel/path0/a1b2c3d4e5/x.js)")], previous: null);
        var cycle2Plan = new[]
        {
            new PlanStep(1, "Fix failure:preview:vercel", "Add zod to package.json.\n\n**Failure evidence** (added by Tevrin)\nFailure id: failure:preview:vercel", [new DeclaredFile("package.json", FileAction.Modify)], []),
            new PlanStep(2, "Docs", "Update the README.", [new DeclaredFile("README.md", FileAction.Modify)], []),
        };
        var finding3 = VercelFailure("12:09:44 Error: Cannot find module 'zod' (/vercel/path0/9f8e7d6c5b/x.js)");

        var cycle3 = FailureHistory.Records([finding3], cycle2);
        var repeats = FailureHistory.Repeats(cycle3, cycle2Plan);
        var text = ReviewCode.RenderFindings([finding3], false, 0, repeats);

        Assert.Equal(1, Assert.Single(cycle2).Cycles);
        Assert.Equal(2, Assert.Single(cycle3).Cycles);
        Assert.Equal("Step 1: Fix failure:preview:vercel\nAdd zod to package.json.", repeats["failure:preview:vercel"].PreviousStepText);
        Assert.Contains("Vercel preview build failed — repeated (2 cycles)\n", text, StringComparison.Ordinal);
        Assert.Contains("plan a different approach and say in the step what was tried", text, StringComparison.Ordinal);
        Assert.Contains("Previous fix step:\n```\nStep 1: Fix failure:preview:vercel\nAdd zod to package.json.\n```", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR240_A_different_error_in_cycle_3_is_not_marked_repeated()
    {
        var cycle2 = FailureHistory.Records([VercelFailure("Error: Cannot find module 'zod'")], previous: null);
        var finding3 = VercelFailure("Type error: Property 'id' does not exist on type 'User'");

        var cycle3 = FailureHistory.Records([finding3], cycle2);

        Assert.Equal(1, Assert.Single(cycle3).Cycles);
        Assert.Empty(FailureHistory.Repeats(cycle3, null));
        Assert.DoesNotContain("repeated", ReviewCode.RenderFindings([finding3], false, 0, FailureHistory.Repeats(cycle3, null)), StringComparison.Ordinal);
    }

    [Fact]
    public void FR240_Repeats_count_consecutive_cycles()
    {
        var finding = VercelFailure("Error: Cannot find module 'zod'");
        var history = FailureHistory.Records([finding], null);
        history = FailureHistory.Records([finding], history);
        history = FailureHistory.Records([finding], history);

        Assert.Equal(3, Assert.Single(history).Cycles);
    }
}
