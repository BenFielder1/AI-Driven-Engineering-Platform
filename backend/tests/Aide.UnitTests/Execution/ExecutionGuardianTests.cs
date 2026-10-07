using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.UnitTests.Execution;

/// <summary>§4.6, FR-32, D18.</summary>
public class ExecutionGuardianTests
{
    private static readonly StepState[] Plan =
    [
        new(1, StepStatus.Completed, [], ["src/app.ts", "package.json"]),
        new(2, StepStatus.Completed, [1], ["src/db.ts"]),
        new(3, StepStatus.Pending, [], ["src/other.ts"]),
        new(4, StepStatus.InProgress, [2], ["src/api.ts", "src/api.test.ts"]),
    ];

    private static GuardianInput Input(params ProducedFile[] files) => new(4, 4, Plan, files, 20);

    private static ProducedFile File(string path, string content = "export {};") => new(path, content);

    [Fact]
    public void FR32_Clean_output_is_accepted_with_no_violations()
    {
        var result = ExecutionGuardian.Validate(Input(File("src/api.ts"), File("./src/api.test.ts")));

        Assert.True(result.Accepted);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void FR32_Output_touching_25_files_with_max_20_is_rejected_with_FILE_LIMIT()
    {
        var files = Enumerable.Range(1, 25).Select(i => File($"src/f{i}.ts")).ToArray();

        var result = ExecutionGuardian.Validate(Input(files));

        Assert.False(result.Accepted);
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.FileLimit && v.Check == GuardianCheck.FileLimit);
    }

    [Fact]
    public void FR32_All_checks_run_and_every_violation_is_reported()
    {
        var input = new GuardianInput(
            RequestedStep: 4,
            OutputStep: 5,
            PlanSteps: [Plan[0], Plan[1] with { Status = StepStatus.Pending }, Plan[2], Plan[3]],
            Files:
            [
                File("src/a.ts"),
                File("src/b.ts"),
                File("src/other.ts"),
                File("yarn.lock"),
                File("src/key.ts", "const k = \"ghp_abcdefghijklmnopqrstuvwxyz0123456789\";"),
            ],
            MaxFilesPerStep: 2);

        var result = ExecutionGuardian.Validate(input);

        Assert.Equal(
            Enum.GetValues<GuardianCheck>(),
            result.Violations.Select(v => v.Check).Distinct().Order());
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.SingleStep);
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.FileLimit);
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.DependencyOrder);
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.ScopeCreep && v.Path == "src/other.ts");
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Path == "yarn.lock");
        Assert.Contains(result.Violations, v => v.Code == ErrorCodes.Guardian.SecretDetected && v.Path == "src/key.ts");
        Assert.Equal(5, result.Errors.Count(v => v.Check == GuardianCheck.PlanAlignment));
        Assert.Equal(2, result.Warnings.Count());
    }

    [Fact]
    public void S4_6_SingleStep_rejects_a_step_that_is_not_pending_or_in_progress()
    {
        var input = new GuardianInput(1, 1, Plan, [File("src/app.ts"), File("package.json")], 20);

        var result = ExecutionGuardian.Validate(input);

        Assert.Contains(result.Errors, v => v.Check == GuardianCheck.SingleStep && v.Message.Contains("completed", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_6_SingleStep_rejects_output_with_no_files()
    {
        var result = ExecutionGuardian.Validate(Input());

        Assert.Contains(result.Errors, v => v.Check == GuardianCheck.SingleStep);
    }

    [Fact]
    public void S4_6_SingleStep_rejects_an_unknown_step()
    {
        var result = ExecutionGuardian.Validate(new GuardianInput(9, 9, Plan, [File("x.ts")], 20));

        Assert.Contains(result.Errors, v => v.Check == GuardianCheck.SingleStep && v.Message.Contains("not in the plan", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_6_Declared_but_not_produced_is_only_a_warning()
    {
        var result = ExecutionGuardian.Validate(Input(File("src/api.ts")));

        Assert.True(result.Accepted);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("src/api.test.ts", warning.Path);
    }

    [Fact]
    public void D18_Paths_are_compared_exactly_not_by_suffix()
    {
        var result = ExecutionGuardian.Validate(Input(File("lib/src/api.ts"), File("src/api.test.ts")));

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, v => v.Check == GuardianCheck.PlanAlignment && v.Path == "lib/src/api.ts");
    }

    [Fact]
    public void S4_6_Duplicate_normalised_paths_are_rejected()
    {
        var input = new GuardianInput(4, 4, Plan, [File("src/api.ts"), File("./src/api.ts"), File("src\\api.test.ts")], 20);

        var result = ExecutionGuardian.Validate(input);

        Assert.False(result.Accepted);
        var violation = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Guardian.DuplicatePath, violation.Code);
        Assert.Equal(GuardianCheck.SingleStep, violation.Check);
        Assert.Equal("src/api.ts", violation.Path);
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("vendor/lib/.git/config")]
    [InlineData(".GIT/HEAD")]
    [InlineData("sub/.git")]
    public void S4_6_Git_internals_are_rejected_even_when_declared(string path)
    {
        var plan = new[] { new StepState(1, StepStatus.Pending, [], [path]) };

        var result = ExecutionGuardian.Validate(new GuardianInput(1, 1, plan, [File(path)], 20));

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, v => v.Code == ErrorCodes.Guardian.UnsafePath);
    }

    [Fact]
    public void S4_6_Protected_path_is_allowed_when_declared_for_this_step()
    {
        var plan = new[] { new StepState(1, StepStatus.Pending, [], [".github/workflows/ci.yml", ".env.example"]) };

        var result = ExecutionGuardian.Validate(new GuardianInput(1, 1, plan, [File(".github/workflows/ci.yml"), File(".env.example")], 20));

        Assert.True(result.Accepted);
    }

    [Fact]
    public void S4_6_File_shared_by_two_steps_is_fine_when_declared_in_this_step()
    {
        var plan = new[]
        {
            new StepState(1, StepStatus.Completed, [], ["src/app.ts"]),
            new StepState(2, StepStatus.Pending, [1], ["src/app.ts"]),
        };

        Assert.True(ExecutionGuardian.Validate(new GuardianInput(2, 2, plan, [File("src/app.ts")], 20)).Accepted);
    }

    [Theory]
    [InlineData("../outside.ts")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("src/../../x.ts")]
    public void S4_6_Unsafe_paths_are_rejected(string path)
    {
        var plan = new[] { new StepState(1, StepStatus.Pending, [], [path]) };

        var result = ExecutionGuardian.Validate(new GuardianInput(1, 1, plan, [File(path)], 20));

        Assert.Contains(result.Errors, v => v.Code == ErrorCodes.Guardian.UnsafePath);
    }

    [Fact]
    public void D18_Secret_violation_never_contains_the_secret_value()
    {
        const string secret = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
        var plan = new[] { new StepState(1, StepStatus.Pending, [], ["src/llm.ts"]) };

        var result = ExecutionGuardian.Validate(new GuardianInput(1, 1, plan, [File("src/llm.ts", $"line1\nconst key = '{secret}';")], 20));

        var violation = Assert.Single(result.Errors);
        Assert.Equal(GuardianCheck.SecretScan, violation.Check);
        Assert.Contains("line 2", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, violation.Message, StringComparison.Ordinal);
    }
}

public class PathRulesTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git/config", true)]
    [InlineData("a/.Git/b", true)]
    [InlineData("a/b/.git", true)]
    [InlineData(".gitignore", false)]
    [InlineData(".github/CODEOWNERS", false)]
    [InlineData("src/git/x.ts", false)]
    public void S4_6_Any_git_segment_is_unsafe(string path, bool unsafePath) =>
        Assert.Equal(unsafePath, PathRules.IsUnsafe(path));

    [Theory]
    [InlineData("./src/a.ts", "src/a.ts")]
    [InlineData("././src/a.ts", "src/a.ts")]
    [InlineData("src\\sub\\a.ts", "src/sub/a.ts")]
    [InlineData("src//a.ts", "src/a.ts")]
    [InlineData("  src/a.ts  ", "src/a.ts")]
    [InlineData("src/dir/", "src/dir")]
    [InlineData(".\\src\\a.ts", "src/a.ts")]
    public void S4_6_Paths_are_normalised(string input, string expected) =>
        Assert.Equal(expected, PathRules.Normalize(input));

    [Theory]
    [InlineData(".git/config")]
    [InlineData("sub/.git/HEAD")]
    [InlineData(".aide/state.json")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".GitHub/Workflows/release.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("azure-pipelines.yml")]
    [InlineData("Jenkinsfile")]
    [InlineData(".circleci/config.yml")]
    [InlineData("package-lock.json")]
    [InlineData("frontend/package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("backend/src/App/packages.lock.json")]
    [InlineData("Cargo.lock")]
    [InlineData("poetry.lock")]
    [InlineData("go.sum")]
    [InlineData("Gemfile.lock")]
    [InlineData("composer.lock")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("config/.env.production")]
    public void S4_6_Protected_paths(string path) =>
        Assert.True(PathRules.IsProtected(path));

    [Theory]
    [InlineData("src/app.ts")]
    [InlineData(".github/CODEOWNERS")]
    [InlineData("docs/gitlab-ci.yml.md")]
    [InlineData("package.json")]
    [InlineData("src/environment.ts")]
    [InlineData("src/.envrc.ts")]
    [InlineData("nested/Jenkinsfile")]
    public void S4_6_Unprotected_paths(string path) =>
        Assert.False(PathRules.IsProtected(path));
}

public class SecretScannerTests
{
    [Theory]
    [InlineData("aws_access_key_id", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("aws_secret_access_key", "aws_secret_access_key = \"wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\"")]
    [InlineData("github_token", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_token", "gho_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_fine_grained_token", "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("anthropic_api_key", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("openai_api_key", "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCD")]
    [InlineData("openai_api_key", "sk-abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH")]
    [InlineData("slack_token", "xoxb-1234567890-abcdefghij")]
    [InlineData("slack_webhook", "https://hooks.slack.com/services/T0000/B0000/XXXXXXXXXXXX")]
    [InlineData("private_key", "-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("private_key", "-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("private_key", "-----BEGIN PRIVATE KEY-----")]
    [InlineData("google_api_key", "AIzaSyA-abcdefghijklmnopqrstuvwxyz01234")]
    [InlineData("stripe_live_key", "sk_live_abcdefghijklmnopqrstuvwx")]
    [InlineData("aide_api_token", "aide_pat_abcdefghijklmnopqrstuvwxyz")]
    public void D18_Known_secret_patterns_are_detected(string pattern, string text)
    {
        var findings = SecretScanner.Scan($"const x = {text};", "f.ts");

        Assert.Contains(findings, f => f.Pattern == pattern && f.Line == 1 && f.Path == "f.ts");
    }

    [Fact]
    public void D18_Anthropic_keys_are_not_double_reported_as_openai()
    {
        var findings = SecretScanner.Scan("sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789");

        Assert.Equal(["anthropic_api_key"], findings.Select(f => f.Pattern));
    }

    [Theory]
    [InlineData("const apiKey = process.env.OPENAI_API_KEY;")]
    [InlineData("// set ANTHROPIC_API_KEY in your environment")]
    [InlineData("const task = 'sk-short';")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    [InlineData("const id = 'AKIA';")]
    public void D18_Ordinary_code_is_not_flagged(string text) =>
        Assert.Empty(SecretScanner.Scan(text));
}
