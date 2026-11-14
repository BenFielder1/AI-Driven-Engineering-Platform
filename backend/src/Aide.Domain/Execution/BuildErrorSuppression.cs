using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>
/// FR-257, §4.48: the Guardian's <c>BUILD_ERRORS_SUPPRESSED</c> rule for build-fix steps (plan steps are unchanged). A
/// written file is rejected when, compared with its previous content, it adds (has more occurrences of) any of:
/// <list type="bullet">
/// <item><c>ignoreBuildErrors</c> or <c>ignoreDuringBuilds</c>;</item>
/// <item><c>@ts-nocheck</c>, <c>@ts-ignore</c> or <c>@ts-expect-error</c>;</item>
/// <item>an <c>eslint-disable</c> comment that isn't <c>eslint-disable-line</c> or <c>eslint-disable-next-line</c>;</item>
/// <item><c>--no-verify</c>, <c>continue-on-error: true</c>, or <c>|| true</c> in a build or test script (a
/// <c>package.json</c> script whose name has build, test, lint, check, verify, ci or typecheck in it; a CI pipeline file;
/// a shell, PowerShell or batch script or Makefile whose name has build, test, ci or check in it);</item>
/// <item><c>.skip(</c>, <c>xit(</c>, <c>xdescribe(</c>, <c>[Fact(Skip</c>, <c>[Theory(Skip</c> or <c>@pytest.mark.skip</c>
/// (which also covers <c>skipif</c>).</item>
/// </list>
/// Emptying a test file that had content (the code generator's only way to delete one) is also rejected. Each violation
/// says why, so the normal retry feedback (FR-33) tells the agent what to undo. Pure.
/// </summary>
public static partial class BuildErrorSuppression
{
    /// <summary>The Guardian check the violations are reported under (the step went beyond fixing the errors).</summary>
    public const GuardianCheck ReportedUnder = GuardianCheck.ScopeCreep;

    private static readonly Rule[] Rules =
    [
        new("ignoreBuildErrors", IgnoreBuildErrors()),
        new("ignoreDuringBuilds", IgnoreDuringBuilds()),
        new("@ts-nocheck", TsNoCheck()),
        new("@ts-ignore", TsIgnore()),
        new("@ts-expect-error", TsExpectError()),
        new("a file-wide eslint-disable comment", EslintDisable()),
        new("--no-verify", NoVerify()),
        new("continue-on-error: true", ContinueOnError()),
        new("|| true in a build or test script", OrTrue(), ScriptsOnly: true),
        new("a skipped test (.skip()", DotSkip()),
        new("a skipped test (xit()", Xit()),
        new("a skipped test (xdescribe()", Xdescribe()),
        new("a skipped test ([Fact(Skip)", FactSkip()),
        new("a skipped test ([Theory(Skip)", TheorySkip()),
        new("a skipped test (@pytest.mark.skip)", PytestSkip()),
    ];

    /// <summary>The violations of <paramref name="files"/> against their previous content (<c>null</c>: a new file).</summary>
    public static IReadOnlyList<GuardianViolation> Check(IEnumerable<ProducedFile> files, Func<string, string?>? previousContent)
    {
        ArgumentNullException.ThrowIfNull(files);
        var violations = new List<GuardianViolation>();
        foreach (var file in files)
        {
            var path = PathRules.Normalize(file.Path);
            var before = previousContent?.Invoke(path) ?? string.Empty;
            var after = file.Content ?? string.Empty;
            if (IsTestFile(path) && !string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            {
                violations.Add(Violation(path, $"'{path}' empties (deletes) a test file. A build-fix step must make the tests pass, not remove them. Keep the tests and fix the code they test."));
                continue;
            }

            foreach (var rule in Rules)
            {
                if (Count(rule, path, after) > Count(rule, path, before))
                {
                    violations.Add(Violation(path, $"'{path}' adds {rule.Label}, which hides build or test errors. A build-fix step must fix the errors, not suppress or skip them. Remove it and fix the cause."));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a test file: under a <c>test</c>, <c>tests</c>, <c>__tests__</c>, <c>spec</c> or
    /// <c>e2e</c> folder, or named <c>*.test.*</c>, <c>*.spec.*</c>, <c>*_test.*</c>, <c>test_*.py</c>, <c>*Test.*</c>,
    /// <c>*Tests.*</c> or <c>*_spec.rb</c>.
    /// </summary>
    public static bool IsTestFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalised = PathRules.Normalize(path);
        var segments = normalised.Split('/');
        if (segments[..^1].Any(s => s is "test" or "tests" or "__tests__" or "spec" or "e2e"))
        {
            return true;
        }

        return TestFileName().IsMatch(segments[^1]);
    }

    /// <summary>Whether a file is a build or test script for the <c>|| true</c> rule (see the type summary).</summary>
    public static bool IsScriptFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalised = PathRules.Normalize(path);
        var name = normalised.Split('/')[^1];
        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || CiFile().IsMatch(normalised))
        {
            return true;
        }

        return ScriptFileName().IsMatch(name) && ScriptPurpose().IsMatch(name);
    }

    private static int Count(Rule rule, string path, string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        if (!rule.ScriptsOnly)
        {
            return rule.Pattern.Count(content);
        }

        if (!IsScriptFile(path))
        {
            return 0;
        }

        // package.json: only the lines of build and test scripts.
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var packageJson = path.Split('/')[^1].Equals("package.json", StringComparison.OrdinalIgnoreCase);
        return lines.Where(l => !packageJson || PackageScriptLine().IsMatch(l)).Sum(l => rule.Pattern.Count(l));
    }

    private static GuardianViolation Violation(string path, string message) =>
        new(ReportedUnder, ErrorCodes.Guardian.BuildErrorsSuppressed, ViolationSeverity.Error, message, path);

    private sealed record Rule(string Label, Regex Pattern, bool ScriptsOnly = false);

    [GeneratedRegex(@"\bignoreBuildErrors\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IgnoreBuildErrors();

    [GeneratedRegex(@"\bignoreDuringBuilds\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IgnoreDuringBuilds();

    [GeneratedRegex(@"@ts-nocheck\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TsNoCheck();

    [GeneratedRegex(@"@ts-ignore\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TsIgnore();

    [GeneratedRegex(@"@ts-expect-error\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TsExpectError();

    // eslint-disable not followed by "-" or a word character: excludes -line and -next-line (and eslint-disabled).
    [GeneratedRegex(@"\beslint-disable(?![-\w])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EslintDisable();

    [GeneratedRegex(@"--no-verify\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoVerify();

    [GeneratedRegex(@"\bcontinue-on-error\s*:\s*['""]?true\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContinueOnError();

    [GeneratedRegex(@"\|\|\s*true\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OrTrue();

    // Not @pytest.mark.skip(...), which has its own rule.
    [GeneratedRegex(@"(?<!@pytest\.mark)\.skip\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DotSkip();

    [GeneratedRegex(@"(?<![\w.$])xit\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Xit();

    [GeneratedRegex(@"(?<![\w.$])xdescribe\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Xdescribe();

    [GeneratedRegex(@"\[\s*Fact\s*\(\s*Skip\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FactSkip();

    [GeneratedRegex(@"\[\s*Theory\s*\(\s*Skip\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TheorySkip();

    [GeneratedRegex(@"@pytest\.mark\.skip", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PytestSkip();

    [GeneratedRegex(@"(?:\.test|\.spec|_test|Tests?)\.[A-Za-z0-9]+$|^test_.+\.py$|_spec\.rb$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TestFileName();

    [GeneratedRegex(@"^\.github/workflows/[^/]+\.ya?ml$|(?:^|/)\.gitlab-ci\.ya?ml$|(?:^|/)bitbucket-pipelines\.ya?ml$|(?:^|/)azure-pipelines\.ya?ml$|(?:^|/)\.circleci/config\.ya?ml$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CiFile();

    [GeneratedRegex(@"^(?:Makefile|.+\.(?:sh|bash|ps1|cmd|bat|mk))$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptFileName();

    [GeneratedRegex(@"build|test|ci|check|Makefile", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptPurpose();

    [GeneratedRegex(@"^\s*""[^""]*(?:build|test|lint|check|verify|ci|typecheck)[^""]*""\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageScriptLine();
}
