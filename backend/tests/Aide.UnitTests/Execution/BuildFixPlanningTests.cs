using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Review;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.UnitTests.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Execution;

/// <summary>
/// FR-256, §4.48: the build-fix agent's inputs (failures with repeat marks, the error summary, stack guidance), the
/// processing of its reply (validation, evidence blocks, missing deployment files, numbering after the version's steps)
/// and the <c>build_fix.md</c> v1 prompt.
/// </summary>
public sealed class BuildFixPlanningTests
{
    private const string ModuleNotFound = "Module not found: Can't resolve '@/lib/db'";

    private static readonly HashSet<string> EmptyBase = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Repo = new(["package.json", "app/page.tsx", "lib/utils.ts", "next.config.ts"], StringComparer.OrdinalIgnoreCase);

    private static string VercelLog(string error = ModuleNotFound) =>
        "Cloning github.com/acme/shop (Branch: tevrin/shop/v1.0.0)\nRunning \"npm run build\"\n> next build\n"
        + "Failed to compile.\n./app/page.tsx\n" + error + "\n\nBuild failed because of webpack errors\nError: Command \"npm run build\" exited with 1";

    private static BuildCheckFailure VercelFailure(string error = ModuleNotFound) =>
        BuildCheckFailures.From(PreviewFinding(VercelLog(error)), VercelLog(error));

    private static ReviewFinding PreviewFinding(string log)
    {
        var excerpt = FailureExcerpt.Compose(log);
        return new ReviewFinding(ReviewCategory.Architecture, Severity.Critical, "Vercel preview build failed", Source: FindingSource.Deterministic, Evidence: excerpt.Text)
        {
            FailureId = "failure:preview:vercel",
            FailureErrorLine = excerpt.ErrorLine,
        };
    }

    private static string Reply(params object[] steps) => JsonSerializer.Serialize(new { steps, summary = "The page imports a module that doesn't exist." });

    private static object ReplyStep(int number, string title, string task, string path, int[]? dependencies = null, string action = "modify", string agent = "generalist") =>
        new { number, title, task, files = new[] { new { path, action } }, dependencies = dependencies ?? [], agent };

    private static BuildFixPlanCheck Check(params BuildCheckFailure[] failures) =>
        new(HighestStepNumber: 6, MaxFilesPerStep: 5, PlanAgents.None, failures, Repo);

    // ---------- §4.48 ERROR_SUMMARY ----------

    [Fact]
    public void FR256_The_error_summary_has_each_distinct_error_line_once_after_normalisation()
    {
        var log = "12:01:03 Error: Cannot find module 'zod' (/tmp/a1b2c3d4e5/x.js)\nok line\n12:09:44 Error: Cannot find module 'zod' (/tmp/9f8e7d6c5b/x.js)\n"
            + "Type error: Property 'id' does not exist on type 'Todo'.\n\u001b[31mnpm ERR! code 1\u001b[0m";

        var lines = BuildErrorSummary.Lines(log);

        Assert.Equal(
            ["12:01:03 Error: Cannot find module 'zod' (/tmp/a1b2c3d4e5/x.js)", "Type error: Property 'id' does not exist on type 'Todo'.", "npm ERR! code 1"],
            lines);
    }

    [Fact]
    public void FR256_The_error_summary_is_capped_at_30_lines()
    {
        var log = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"error TS2304: Cannot find name 'n{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}'."));

        Assert.Equal(BuildErrorSummary.MaxLines, BuildErrorSummary.Lines(log).Count);
    }

    [Fact]
    public void FR256_The_error_summary_is_capped_at_3000_characters()
    {
        var log = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"Error {(char)('a' + i % 26)}{(char)('a' + i / 26)}: " + new string('x', 400)));

        var lines = BuildErrorSummary.Lines(log);

        Assert.True(string.Join('\n', lines).Length <= BuildErrorSummary.MaxChars);
        Assert.InRange(lines.Count, 1, 29);
    }

    [Fact]
    public void FR256_The_error_summary_is_redacted_like_an_excerpt()
    {
        var lines = BuildErrorSummary.Lines("Error: auth failed with token ghp_abcdefghijklmnopqrstuvwxyz0123456789");

        var line = Assert.Single(lines);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", line, StringComparison.Ordinal);
    }

    // ---------- failures ----------

    [Fact]
    public void FR256_A_failure_finding_becomes_a_build_check_failure_with_its_fingerprint_excerpt_and_error_summary()
    {
        var failure = VercelFailure();

        Assert.Equal("failure:preview:vercel", failure.FailureId);
        Assert.Equal(FailureIds.Preview, failure.Source);
        Assert.Equal("Vercel preview build failed", failure.Message);
        Assert.Equal(FailureFingerprint.Of(PreviewFinding(VercelLog())), failure.Fingerprint);
        Assert.Contains(ModuleNotFound, failure.Excerpt, StringComparison.Ordinal);
        Assert.Contains(ModuleNotFound, failure.ErrorSummary);
        Assert.Contains("Build failed because of webpack errors", failure.ErrorSummary);
    }

    [Fact]
    public void FR256_Collect_keeps_failure_findings_only_makes_ids_unique_and_adds_timed_out_sources()
    {
        var advisor = new ReviewFinding(ReviewCategory.Security, Severity.Major, "Supabase advisor: RLS disabled") { FindingId = "finding:advisor:rls" };
        var check = new ReviewFinding(ReviewCategory.Testing, Severity.Major, "required check build failed", Evidence: "build: https://ci") { FailureId = "failure:check:build" };
        var timedOut = new BuildCheckFailure("failure:check:test", "failure:check:test", "required check test didn't finish within 15 minutes", FailureIds.Check, null);

        var failures = BuildCheckFailures.Collect(
            [new(PreviewFinding(VercelLog()), VercelLog()), new(advisor, null), new(check, null), new(check, null)],
            [timedOut]);

        Assert.Equal(["failure:preview:vercel", "failure:check:build", "failure:check:build-2", "failure:check:test"], failures.Select(f => f.FailureId));
        Assert.Equal("failure:check:build", failures[1].Fingerprint);
    }

    // ---------- §4.48 FAILURES with repeat marks ----------

    [Fact]
    public void FR256_Attempts_1_and_2_failing_with_the_same_fingerprint_mark_the_failure_repeated_2_attempts_with_attempt_2s_step_text()
    {
        var typeError = "Type error: Property 'id' does not exist on type 'Todo'.";
        var fix1 = new BuildFixStepText(7, "Fix failure:preview:vercel: add lib/db", "Create lib/db.ts for failure:preview:vercel.");
        var fix2 = new BuildFixStepText(8, "Fix failure:preview:vercel: Todo id", "Add id to Todo for failure:preview:vercel.\n\n**Failure evidence** (added by Tevrin)\nFailure id: failure:preview:vercel");
        var history = new[]
        {
            new BuildCheckHistory(1, 0, BuildCheckStatus.Failed, [VercelFailure()], [fix1]),
            new BuildCheckHistory(1, 1, BuildCheckStatus.Failed, [VercelFailure(typeError)], [fix2]),
        };
        var current = VercelFailure(typeError); // attempt 2, planning fix 3

        var repeats = BuildFixPlanning.Repeats([current], history);
        var rendered = BuildFixPlanning.RenderFailures([current], repeats);

        var repeat = repeats["failure:preview:vercel"];
        Assert.Equal(2, repeat.Attempts);
        Assert.Equal("Step 8: Fix failure:preview:vercel: Todo id\nAdd id to Todo for failure:preview:vercel.", repeat.PreviousStepText);
        Assert.Contains("failure id failure:preview:vercel (preview deployment): Vercel preview build failed — repeated (2 attempts)", rendered, StringComparison.Ordinal);
        Assert.Contains("Add id to Todo", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Create lib/db.ts", rendered, StringComparison.Ordinal);
        Assert.Contains(typeError, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_Repeats_count_consecutive_checks_across_review_cycles_and_skip_cancelled_ones()
    {
        var history = new[]
        {
            new BuildCheckHistory(1, 0, BuildCheckStatus.Exhausted, [VercelFailure()], []),
            new BuildCheckHistory(2, 0, BuildCheckStatus.Cancelled, [], []),
        };

        Assert.Equal(2, BuildFixPlanning.Repeats([VercelFailure()], history)["failure:preview:vercel"].Attempts);
    }

    [Fact]
    public void FR256_A_different_error_or_a_passed_check_in_between_is_not_a_repeat()
    {
        var other = new[] { new BuildCheckHistory(1, 0, BuildCheckStatus.Failed, [VercelFailure("Type error: x")], []) };
        var broken = new[]
        {
            new BuildCheckHistory(1, 0, BuildCheckStatus.Failed, [VercelFailure()], []),
            new BuildCheckHistory(1, 1, BuildCheckStatus.Passed, [], []),
        };

        Assert.Empty(BuildFixPlanning.Repeats([VercelFailure()], other));
        Assert.Empty(BuildFixPlanning.Repeats([VercelFailure()], broken));
        Assert.DoesNotContain("repeated", BuildFixPlanning.RenderFailures([VercelFailure()]), StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_The_prompt_inputs_give_the_failure_id_the_excerpt_and_the_error_summary()
    {
        var failure = VercelFailure();

        var failures = BuildFixPlanning.RenderFailures([failure]);
        var summary = BuildFixPlanning.RenderErrorSummary([failure]);

        Assert.Contains("failure id failure:preview:vercel", failures, StringComparison.Ordinal);
        Assert.Contains("Evidence excerpt:\n```\n", failures, StringComparison.Ordinal);
        Assert.Contains(ModuleNotFound, failures, StringComparison.Ordinal);
        Assert.StartsWith("failure:preview:vercel:\n- ", summary, StringComparison.Ordinal);
        Assert.Contains("- " + ModuleNotFound, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_Stack_guidance_lists_the_build_commands_and_required_files()
    {
        var stack = StackResolverTests.Resolve(Sel("fe-nextjs", "none", "none", "vcs-github", frontendHost: "deploy-vercel"));

        var guidance = BuildFixPlanning.RenderStackGuidance(StackRequiredFiles.For(stack), [new BuildCommandHint("frontend (Vercel)", "npm run build")]);

        Assert.Contains("- frontend (Vercel): `npm run build`", guidance, StringComparison.Ordinal);
        Assert.Contains("frontend/vercel.json", guidance, StringComparison.Ordinal);
        Assert.Null(BuildFixPlanning.RenderStackGuidance([], []));
    }

    // ---------- §4.48 Processing ----------

    [Fact]
    public void FR256_The_vercel_module_not_found_fix_is_numbered_after_the_versions_steps_and_carries_the_evidence_block()
    {
        var reply = Reply(ReplyStep(1, "Fix failure:preview:vercel: create lib/db", $"`{ModuleNotFound}` in app/page.tsx: create lib/db.ts. Verify with npm run build.", "lib/db.ts", action: "create"));

        var result = BuildFixPlanning.Process(reply, Check(VercelFailure()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var step = Assert.Single(result.Value.Steps);
        Assert.Equal(7, step.Number);
        Assert.Equal(1, Assert.Single(result.Value.LocalSteps).Number);
        Assert.Contains("**Failure evidence** (added by Tevrin)\nFailure id: failure:preview:vercel", step.Task, StringComparison.Ordinal);
        Assert.Contains(ModuleNotFound, step.Task, StringComparison.Ordinal);
        Assert.Null(step.AddedBy);
        Assert.Equal("The page imports a module that doesn't exist.", result.Value.Summary);
    }

    [Fact]
    public void FR256_A_reply_naming_no_failure_id_gets_the_Fix_step_as_FR239_does()
    {
        var reply = Reply(ReplyStep(1, "Tidy utils", "Clean up lib/utils.ts.", "lib/utils.ts"));

        var result = BuildFixPlanning.Process(reply, Check(VercelFailure()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([7, 8], result.Value.Steps.Select(s => s.Number));
        var added = result.Value.Steps[1];
        Assert.Equal("Fix: Vercel preview build failed", added.Title);
        Assert.Equal(PlanStep.AddedByTevrin, added.AddedBy);
        Assert.Equal(["app/page.tsx"], added.Files.Select(f => f.Path));
        Assert.Contains("Failure id: failure:preview:vercel", added.Task, StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_Dependencies_are_renumbered_with_the_steps()
    {
        var reply = Reply(
            ReplyStep(1, "Fix failure:preview:vercel: create lib/db", "Create lib/db.ts.", "lib/db.ts", action: "create"),
            ReplyStep(2, "Fix failure:preview:vercel: use it", "Import lib/db in app/page.tsx.", "app/page.tsx", [1]));

        var steps = BuildFixPlanning.Process(reply, Check(VercelFailure())).Value.Steps;

        Assert.Equal([7, 8], steps.Select(s => s.Number));
        Assert.Equal([[], [7]], steps.Select(s => s.Dependencies.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void FR256_A_fix_plan_must_have_1_to_5_steps(int count)
    {
        var steps = Enumerable.Range(1, count).Select(i => ReplyStep(i, $"Fix failure:preview:vercel part {i}", "Fix it.", $"lib/f{i}.ts", action: "create")).ToArray();

        var result = BuildFixPlanning.Process(Reply(steps), Check(VercelFailure()));

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error?.Code);
        Assert.Contains("plan 1 to 5 steps", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_A_step_on_a_protected_path_is_invalid_with_reasons_for_the_one_retry()
    {
        var result = BuildFixPlanning.Process(Reply(ReplyStep(1, "Fix failure:preview:vercel", "Regenerate the lockfile.", "package-lock.json")), Check(VercelFailure()));

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error?.Code);
        var feedback = BuildFixPlanning.CorrectiveFeedback(result.Error!);
        Assert.StartsWith("Your previous fix plan was rejected with INVALID_PLAN", feedback, StringComparison.Ordinal);
        Assert.Contains("package-lock.json", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void FR256_A_reply_that_is_not_json_is_invalid() =>
        Assert.Equal(ErrorCodes.InvalidPlan, BuildFixPlanning.Process("I could not find the problem.", Check(VercelFailure())).Error?.Code);

    [Fact]
    public void FR256_A_missing_deployment_file_gets_its_step_first_FR243()
    {
        var stack = StackResolverTests.Resolve(Sel("fe-nextjs", "none", "none", "vcs-github", frontendHost: "deploy-vercel"));
        var agents = PlanAgents.ForStackOptimization(stack, EmptyBase);
        var repo = new HashSet<string>(["frontend/package.json", "frontend/src/app/page.tsx"], StringComparer.OrdinalIgnoreCase);
        var reply = Reply(ReplyStep(1, "Fix failure:preview:vercel: create db", "Create frontend/src/lib/db.ts.", "frontend/src/lib/db.ts", action: "create", agent: StackAgents.FrontendEngineer));
        var check = new BuildFixPlanCheck(10, 5, agents, [VercelFailure()], repo, new DeployFileCheck(StackRequiredFiles.For(stack), repo, Optimisation: false));

        var result = BuildFixPlanning.Process(reply, check);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(["Add frontend/vercel.json for Vercel", "Fix failure:preview:vercel: create db"], result.Value.Steps.Select(s => s.Title));
        Assert.Equal([11, 12], result.Value.Steps.Select(s => s.Number));
        Assert.Empty(result.Value.Steps[0].Dependencies);
    }

    [Fact]
    public void FR256_Renumber_moves_numbers_and_dependencies_by_the_same_offset()
    {
        PlanStep[] steps =
        [
            new(1, "a", "a", [new DeclaredFile("a.ts", FileAction.Create)], []),
            new(2, "b", "b", [new DeclaredFile("b.ts", FileAction.Create)], [1]),
        ];

        var renumbered = BuildFixPlanning.Renumber(steps, 41);

        Assert.Equal([42, 43], renumbered.Select(s => s.Number));
        Assert.Equal([[], [42]], renumbered.Select(s => s.Dependencies.ToArray()));
    }

    // ---------- agent and prompt ----------

    [Fact]
    public void FR256_Build_fix_uses_the_code_review_model_settings()
    {
        var models = new Dictionary<string, string> { ["code_review"] = "review-model", ["build_fix"] = "ignored" };

        Assert.Equal("build_fix", BuildFixPlanning.AgentKey);
        Assert.Equal("code_review", AgentRunRules.ModelKey(AgentKind.BuildFix));
        Assert.Equal("review-model", AgentRunRules.ChooseModel(AgentKind.BuildFix, LlmProvider.Anthropic, "default", models).Model);
        Assert.Equal("default", AgentRunRules.ChooseModel(AgentKind.BuildFix, LlmProvider.Anthropic, "default", null).Model);
    }

    [Fact]
    public void FR256_The_build_fix_prompt_v1_has_the_inputs_rules_and_output_schema()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.BuildFix);
        var rendered = template.Render(template.RequiredInputs.ToDictionary(n => n, n => (string?)$"<{n}>", StringComparer.Ordinal)).Value;

        Assert.Equal("1", template.Version);
        Assert.Contains(new PromptPlaceholder("STACK_GUIDANCE", Optional: true), template.Placeholders);
        Assert.Contains(new PromptPlaceholder("REPO_FILES", Optional: true), template.Placeholders);
        Assert.Contains("Fix **every** error in the error summary", rendered.System, StringComparison.Ordinal);
        Assert.Contains("Plan 1 to 5 steps", rendered.System, StringComparison.Ordinal);
        Assert.Contains("at most 25 calls", rendered.System, StringComparison.Ordinal);
        Assert.Contains("**Never hide errors.**", rendered.System, StringComparison.Ordinal);
        foreach (var listed in new[] { "ignoreBuildErrors", "ignoreDuringBuilds", "@ts-nocheck", "@ts-ignore", "@ts-expect-error", "eslint-disable-next-line", "--no-verify", "continue-on-error: true", "|| true", ".skip(", "xit(", "xdescribe(", "[Fact(Skip", "[Theory(Skip", "@pytest.mark.skip" })
        {
            Assert.Contains(listed, rendered.System, StringComparison.Ordinal);
        }

        Assert.Contains("\"summary\":", rendered.System, StringComparison.Ordinal);
        Assert.Contains("\"steps\": [{\"number\": 1", rendered.System, StringComparison.Ordinal);
    }
}
