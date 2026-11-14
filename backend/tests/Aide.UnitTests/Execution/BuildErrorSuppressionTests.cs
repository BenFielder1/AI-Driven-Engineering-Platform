using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.UnitTests.Execution;

/// <summary>FR-257, §4.48: the Guardian rejects build-fix steps that hide build or test errors; plan steps are unchanged.</summary>
public sealed class BuildErrorSuppressionTests
{
    private static readonly StepState[] Plan =
    [
        new(1, StepStatus.Completed, [], ["next.config.ts"]),
        new(7, StepStatus.InProgress, [], ["next.config.ts", "app/page.tsx", "app/page.test.tsx", "package.json", ".github/workflows/ci.yml", "tests/test_api.py", "Api.Tests/TodoTests.cs", "deploy/build.sh", "README.md"]),
    ];

    private static GuardianResult Validate(bool buildFix, Dictionary<string, string>? previous, params ProducedFile[] files) =>
        ExecutionGuardian.Validate(new GuardianInput(7, 7, Plan, files, 20)
        {
            BuildFix = buildFix,
            PreviousContent = previous is null ? null : path => previous.TryGetValue(path, out var content) ? content : null,
        });

    private static IReadOnlyList<GuardianViolation> Suppressed(GuardianResult result) =>
        [.. result.Violations.Where(v => v.Code == ErrorCodes.Guardian.BuildErrorsSuppressed)];

    private const string NextConfig = "import type { NextConfig } from 'next';\n\nconst config: NextConfig = {};\n\nexport default config;\n";

    [Fact]
    public void FR257_A_build_fix_step_adding_typescript_ignoreBuildErrors_to_next_config_is_rejected_with_a_reason()
    {
        var after = "import type { NextConfig } from 'next';\n\nconst config: NextConfig = { typescript: { ignoreBuildErrors: true } };\n\nexport default config;\n";

        var result = Validate(true, new Dictionary<string, string> { ["next.config.ts"] = NextConfig }, new ProducedFile("next.config.ts", after));

        Assert.False(result.Accepted);
        var violation = Assert.Single(Suppressed(result));
        Assert.Equal(("next.config.ts", ViolationSeverity.Error, BuildErrorSuppression.ReportedUnder), (violation.Path, violation.Severity, violation.Check));
        Assert.Contains("adds ignoreBuildErrors", violation.Message, StringComparison.Ordinal);

        // The retry feedback (FR-33) says why.
        var feedback = CodeGeneration.RetryFeedback(result.Violations);
        Assert.Contains("[BUILD_ERRORS_SUPPRESSED]", feedback, StringComparison.Ordinal);
        Assert.Contains("must fix the errors, not suppress or skip them", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void FR257_A_plan_step_adding_ts_expect_error_with_a_reason_is_not_rejected()
    {
        var content = "// @ts-expect-error the library's types are wrong (issue #12)\nexport const x: number = lib();\n";

        var result = Validate(false, null, new ProducedFile("app/page.tsx", content));

        Assert.Empty(Suppressed(result));
        Assert.True(result.Accepted);
    }

    [Fact]
    public void FR257_A_build_fix_step_adding_ts_expect_error_is_rejected() =>
        Assert.Single(Suppressed(Validate(true, null, new ProducedFile("app/page.tsx", "// @ts-expect-error\nexport const x: number = lib();\n"))));

    [Theory]
    [InlineData("eslint: { ignoreDuringBuilds: true }")]
    [InlineData("// @ts-nocheck")]
    [InlineData("// @ts-ignore")]
    [InlineData("/* eslint-disable */")]
    [InlineData("/* eslint-disable no-console */")]
    [InlineData("it.skip('works', () => {})")]
    [InlineData("describe.skip('suite', () => {})")]
    [InlineData("xit('works', () => {})")]
    [InlineData("xdescribe('suite', () => {})")]
    public void FR257_Each_listed_suppression_added_to_a_source_file_is_rejected(string added)
    {
        var result = Validate(true, new Dictionary<string, string> { ["app/page.tsx"] = "export {};\n" }, new ProducedFile("app/page.tsx", "export {};\n" + added + "\n"));

        Assert.Single(Suppressed(result));
    }

    [Theory]
    [InlineData("const a = b; // eslint-disable-line no-unused-vars")]
    [InlineData("// eslint-disable-next-line no-console")]
    [InlineData("const skipped = list.filter(x => x.skipped);")]
    [InlineData("const exit = 1;")]
    public void FR257_Line_level_eslint_comments_and_lookalikes_are_allowed(string added) =>
        Assert.Empty(Suppressed(Validate(true, null, new ProducedFile("app/page.tsx", added + "\n"))));

    [Fact]
    public void FR257_A_suppression_already_in_the_previous_content_is_not_added_so_it_is_allowed()
    {
        var before = "// @ts-ignore legacy\nexport const a = 1;\n";
        var after = "// @ts-ignore legacy\nexport const a = 2;\n";

        Assert.Empty(Suppressed(Validate(true, new Dictionary<string, string> { ["app/page.tsx"] = before }, new ProducedFile("app/page.tsx", after))));
    }

    [Fact]
    public void FR257_A_second_occurrence_of_an_existing_suppression_is_an_addition()
    {
        var before = "// @ts-ignore legacy\nexport const a = 1;\n";
        var after = before + "// @ts-ignore\nexport const b = 2;\n";

        Assert.Single(Suppressed(Validate(true, new Dictionary<string, string> { ["app/page.tsx"] = before }, new ProducedFile("app/page.tsx", after))));
    }

    [Fact]
    public void FR257_Skip_attributes_in_xunit_and_pytest_are_rejected()
    {
        var csharp = new ProducedFile("Api.Tests/TodoTests.cs", "public class TodoTests\n{\n    [Fact(Skip = \"flaky\")]\n    public void A() { }\n\n    [Theory(Skip = \"later\")]\n    public void B(int x) { }\n}\n");
        var python = new ProducedFile("tests/test_api.py", "import pytest\n\n@pytest.mark.skip(reason='later')\ndef test_a():\n    pass\n");

        var violations = Suppressed(Validate(true, null, csharp, python));

        Assert.Equal(3, violations.Count);
        Assert.Equal(2, violations.Count(v => v.Path == "Api.Tests/TodoTests.cs"));
        Assert.Single(violations, v => v.Path == "tests/test_api.py");
    }

    [Fact]
    public void FR257_No_verify_continue_on_error_and_or_true_in_build_and_test_scripts_are_rejected()
    {
        var package = new ProducedFile("package.json", "{\n  \"scripts\": {\n    \"build\": \"next build || true\",\n    \"start\": \"next start\"\n  }\n}\n");
        var workflow = new ProducedFile(".github/workflows/ci.yml", "jobs:\n  test:\n    continue-on-error: true\n    steps:\n      - run: npm test || true\n");
        var script = new ProducedFile("deploy/build.sh", "#!/bin/sh\ngit commit --no-verify -m wip\n");

        var violations = Suppressed(Validate(true, null, package, workflow, script));

        Assert.Single(violations, v => v.Path == "package.json");
        Assert.Equal(2, violations.Count(v => v.Path == ".github/workflows/ci.yml"));
        Assert.Single(violations, v => v.Path == "deploy/build.sh");
    }

    [Fact]
    public void FR257_Or_true_outside_a_build_or_test_script_is_allowed()
    {
        var package = new ProducedFile("package.json", "{\n  \"scripts\": {\n    \"postinstall\": \"husky install || true\"\n  }\n}\n");
        var readme = new ProducedFile("README.md", "Run `cleanup || true` when needed.\n");

        Assert.Empty(Suppressed(Validate(true, null, package, readme)));
    }

    [Fact]
    public void FR257_Emptying_a_test_file_is_rejected_as_deleting_it()
    {
        var previous = new Dictionary<string, string> { ["app/page.test.tsx"] = "it('renders', () => {});\n" };

        var violation = Assert.Single(Suppressed(Validate(true, previous, new ProducedFile("app/page.test.tsx", "  \n"))));

        Assert.Contains("deletes) a test file", violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app/page.test.tsx", true)]
    [InlineData("src/todo.spec.ts", true)]
    [InlineData("tests/test_api.py", true)]
    [InlineData("pkg/todo_test.go", true)]
    [InlineData("Api.Tests/TodoTests.cs", true)]
    [InlineData("src/__tests__/a.ts", true)]
    [InlineData("frontend/e2e/home.ts", true)]
    [InlineData("app/page.tsx", false)]
    [InlineData("src/contest.ts", false)]
    public void FR257_Test_files_are_recognised_by_folder_and_name(string path, bool expected) =>
        Assert.Equal(expected, BuildErrorSuppression.IsTestFile(path));

    [Fact]
    public void FR257_Plan_steps_are_never_checked_for_suppressions()
    {
        var result = Validate(false, null, new ProducedFile("next.config.ts", "export default { typescript: { ignoreBuildErrors: true } };\n"));

        Assert.Empty(Suppressed(result));
    }

    [Fact]
    public void FR257_The_violation_code_is_listed_with_the_other_guardian_codes() =>
        Assert.Contains(ErrorCodes.Guardian.BuildErrorsSuppressed, ErrorCodes.Guardian.All);
}
