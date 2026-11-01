using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Execution;
using Aide.Domain.Review;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-89: the deterministic vercel.json checks (plan warning and review finding).</summary>
public class VercelConfigRulesTests
{
    private const string Good = "{\n  \"git\": {\n    \"deploymentEnabled\": {\n      \"aide/**\": false\n    }\n  }\n}\n";

    /// <summary>FR-129: the stack deploys the frontend to Vercel (formerly a preset's deploy target).</summary>
    private const bool Vercel = true;

    private const bool NoTargets = false;

    private static readonly HashSet<string> EmptyBase = new(StringComparer.OrdinalIgnoreCase);

    private static PlanStep Step(int number, string agent, params string[] paths) =>
        new(number, $"Step {number}", "Do it.", [.. paths.Select(p => new DeclaredFile(p, FileAction.Create))], number == 1 ? [] : [number - 1], null, agent);

    private static List<PlanStep> Plan(params string[] scaffoldFiles) =>
    [
        Step(1, "project-scaffolder", ["package.json", "app/layout.tsx", .. scaffoldFiles]),
        Step(2, "db-engineer", "supabase/migrations/0001_tasks.sql"),
        Step(3, "domain-logic-engineer", "lib/tasks/rules.ts"),
        Step(4, "api-engineer", "app/api/tasks/route.ts"),
        Step(5, "ui-engineer", "app/tasks/page.tsx", "components/TaskList.tsx"),
    ];

    [Fact]
    public void FR89_A_vercel_stack_plan_on_an_empty_repo_without_a_vercel_json_step_warns_MISSING_VERCEL_CONFIG()
    {
        var warning = VercelConfigRules.PlanWarning(Plan(), Vercel, EmptyBase);

        Assert.NotNull(warning);
        Assert.Equal(ErrorCodes.MissingVercelConfig, warning.Code);
        Assert.Contains("aide/**", Assert.Single(warning.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void FR89_The_warning_is_one_of_the_plan_warnings_and_never_an_error()
    {
        var plan = Plan();

        var warning = VercelConfigRules.PlanWarning(plan, Vercel, EmptyBase);

        Assert.Equal(ErrorCodes.MissingVercelConfig, warning?.Code);
        Assert.Null(VercelConfigRules.PlanWarning(plan, Vercel, baseFiles: null)); // base unknown
    }

    [Theory]
    [InlineData("vercel.json")]
    [InlineData("./vercel.json")]
    public void FR89_A_step_declaring_vercel_json_clears_the_warning(string path) =>
        Assert.Null(VercelConfigRules.PlanWarning(Plan(path), Vercel, EmptyBase));

    [Fact]
    public void FR89_No_warning_when_the_base_has_vercel_json_the_base_is_unknown_or_the_stack_does_not_deploy_to_vercel()
    {
        Assert.Null(VercelConfigRules.PlanWarning(Plan(), Vercel, new HashSet<string>(["vercel.json", "package.json"], StringComparer.OrdinalIgnoreCase)));
        Assert.Null(VercelConfigRules.PlanWarning(Plan(), Vercel, baseFiles: null));
        Assert.Null(VercelConfigRules.PlanWarning(Plan(), NoTargets, EmptyBase));
        Assert.Null(VercelConfigRules.PlanWarning(Plan(), deploysToVercel: false, EmptyBase));
        Assert.NotNull(VercelConfigRules.PlanWarning(Plan(), Vercel, new HashSet<string>(["web/vercel.json"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void FR89_The_plan_warning_looks_in_the_Vercel_root_directory_with_the_same_case_sensitive_match_as_review()
    {
        Assert.Null(VercelConfigRules.PlanWarning(Plan("web/vercel.json"), Vercel, EmptyBase, rootDirectory: "./web/"));
        Assert.Null(VercelConfigRules.PlanWarning(Plan(), Vercel, new HashSet<string>(["web/vercel.json"], StringComparer.Ordinal), rootDirectory: "web"));
        var warning = VercelConfigRules.PlanWarning(Plan("vercel.json"), Vercel, EmptyBase, rootDirectory: "web");
        Assert.NotNull(warning);
        Assert.Contains("web/vercel.json", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(VercelConfigRules.PlanWarning(Plan("Vercel.json"), Vercel, EmptyBase));
        Assert.NotNull(VercelConfigRules.PlanWarning(Plan("web/Vercel.json"), Vercel, EmptyBase, rootDirectory: "web"));
        Assert.True(VercelConfigRules.IsConfigPath("./web//vercel.json", VercelConfigRules.ConfigPath("web")));
        Assert.False(VercelConfigRules.IsConfigPath("WEB/vercel.json", VercelConfigRules.ConfigPath("web")));
    }

    [Fact]
    public void FR89_A_branch_vercel_json_disabling_aide_branches_gives_no_finding()
    {
        Assert.Empty(VercelConfigRules.ReviewFindings("vercel.json", Good));
        Assert.Empty(VercelConfigRules.ReviewFindings("vercel.json", "{ \"$schema\": \"https://openapi.vercel.sh/vercel.json\", // comment\n \"git\": { \"deploymentEnabled\": { \"main\": true, \"aide/**\": false, } } }"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{ \"git\": { \"deploymentEnabled\": false } }")]
    [InlineData("{ \"git\": { \"deploymentEnabled\": { \"aide/**\": true } } }")]
    [InlineData("{ \"git\": { \"deploymentEnabled\": { \"aide/*\": false } } }")]
    [InlineData("{ \"git\": { \"deploymentEnabled\": { \"aide/**\": \"false\" } } }")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void FR89_A_missing_or_wrong_branch_vercel_json_is_one_major_architecture_finding(string? content)
    {
        var finding = Assert.Single(VercelConfigRules.ReviewFindings("web/vercel.json", content));

        Assert.Equal((ReviewCategory.Architecture, Severity.Major, FindingSource.Deterministic, "web/vercel.json"), (finding.Category, finding.Severity, finding.Source, finding.Path));
        Assert.Contains(content is null ? "missing" : "doesn't set", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".", "vercel.json")]
    [InlineData(null, "vercel.json")]
    [InlineData("", "vercel.json")]
    [InlineData("./web/", "web/vercel.json")]
    [InlineData("apps\\site", "apps/site/vercel.json")]
    public void FR89_The_config_path_follows_the_Vercel_root_directory(string? root, string expected) =>
        Assert.Equal(expected, VercelConfigRules.ConfigPath(root));
}
