using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Agents;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-233, FR-234, §4.36: the required branch-control prefixes and the config Tevrin writes or suggests.</summary>
public sealed class BranchControlRulesTests
{
    private static IReadOnlyList<string> TevrinOnly => BranchControlRules.RequiredPrefixes(usesLegacyBranch: false);

    private static IReadOnlyList<string> WithAide => BranchControlRules.RequiredPrefixes(usesLegacyBranch: true);

    private static string Vercel(string rules) => $"{{ \"git\": {{ \"deploymentEnabled\": {{ {rules} }} }} }}";

    // ---------- §4.36 required set ----------

    [Fact]
    public void FR233_Required_is_tevrin_plus_aide_only_when_a_recorded_branch_starts_with_aide()
    {
        Assert.Equal(["tevrin/"], BranchControlRules.RequiredPrefixes(["tevrin/shop/v1.0.0", null, "tevrin/shop-0192f3a4"]));
        Assert.Equal(["tevrin/"], BranchControlRules.RequiredPrefixes([]));
        Assert.Equal(["tevrin/", "aide/"], BranchControlRules.RequiredPrefixes(["tevrin/shop/v1.1.0", "aide/shop/v1.0.0"]));
        Assert.Equal(["tevrin/", "aide/"], BranchControlRules.RequiredPrefixes(["aide/shop-0192f3a4"]));
    }

    // ---------- §4.36 examples (Vercel) ----------

    [Fact]
    public void FR233_Example1_Only_tevrin_branches_with_tevrin_false_passes() =>
        Assert.Empty(BranchControlRules.VercelMissing(Vercel("\"tevrin/**\": false"), TevrinOnly));

    [Fact]
    public void FR233_Example2_Only_tevrin_branches_with_only_aide_false_fails_naming_tevrin() =>
        Assert.Equal(["tevrin/**"], BranchControlRules.VercelMissing(Vercel("\"aide/**\": false"), TevrinOnly));

    [Fact]
    public void FR233_Example3_An_aide_branch_with_both_false_passes() =>
        Assert.Empty(BranchControlRules.VercelMissing(Vercel("\"aide/**\": false, \"tevrin/**\": false"), WithAide));

    [Fact]
    public void FR233_Example4_A_true_rule_matching_the_prefix_fails() =>
        Assert.Equal(["tevrin/**"], BranchControlRules.VercelMissing(Vercel("\"tevrin/**\": false, \"tevrin/shop*\": true"), TevrinOnly));

    [Fact]
    public void FR233_An_aide_branch_with_only_tevrin_false_fails_naming_aide() =>
        Assert.Equal(["aide/**"], BranchControlRules.VercelMissing(Vercel("\"tevrin/**\": false"), WithAide));

    [Theory]
    [InlineData("\"**\": true")]
    [InlineData("\"tev*/**\": true")]
    [InlineData("\"tevrin/*\": true")]
    public void FR233_Broader_true_globs_that_match_tevrin_branches_fail(string extra) =>
        Assert.Equal(["tevrin/**"], BranchControlRules.VercelMissing(Vercel($"\"tevrin/**\": false, {extra}"), TevrinOnly));

    [Theory]
    [InlineData("\"main\": true")]
    [InlineData("\"*\": true")] // one segment: never a tevrin/… branch
    [InlineData("\"feature/**\": true")]
    [InlineData("\"tevrin/shop*\": false")]
    public void FR233_True_rules_that_cannot_match_tevrin_branches_and_false_rules_pass(string extra) =>
        Assert.Empty(BranchControlRules.VercelMissing(Vercel($"\"tevrin/**\": false, {extra}"), TevrinOnly));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"git\": { \"deploymentEnabled\": false } }")]
    public void FR233_Unreadable_or_wrong_shaped_config_blocks_nothing(string content) =>
        Assert.Equal(["tevrin/**", "aide/**"], BranchControlRules.VercelMissing(content, WithAide));

    // ---------- FR-233: readiness names the patterns ----------

    [Fact]
    public void FR233_The_readiness_branch_control_message_names_the_missing_patterns()
    {
        var input = new ReadinessInput(
            DeploymentProvider.Vercel, true, ReadinessConnection.Valid, null, true, null, null, [], BranchControlState.Missing, "web/vercel.json", true, ["aide/**"]);

        var item = Assert.Single(ReadinessRules.Checklist(input), i => i.Id == ReadinessRules.BranchControl);

        Assert.Equal(ReadinessState.Error, item.State);
        Assert.Equal("web/vercel.json doesn't stop automatic deploys of aide/**; the deploy step adds it, or add it yourself.", item.Message);
        var ok = Assert.Single(ReadinessRules.Checklist(input with { BranchControl = BranchControlState.Ok, BranchPatterns = ["tevrin/**", "aide/**"] }), i => i.Id == ReadinessRules.BranchControl);
        Assert.Equal("Automatic deploys of tevrin/** and aide/** are disabled (web/vercel.json).", ok.Message);
        var unnamed = Assert.Single(ReadinessRules.Checklist(input with { BranchControl = BranchControlState.Ok, BranchPatterns = null }), i => i.Id == ReadinessRules.BranchControl);
        Assert.Contains("tevrin/**", unnamed.Message, StringComparison.Ordinal);
    }

    // ---------- FR-234: config Tevrin writes or suggests blocks both prefixes ----------

    [Fact]
    public void FR234_The_suggested_Vercel_and_Netlify_config_blocks_both_prefixes_and_passes_branch_control()
    {
        Assert.Equal("{ \"tevrin/**\": false, \"aide/**\": false }", BranchControlRules.VercelSuggestion());
        Assert.Equal("case \"$BRANCH\" in tevrin/*|aide/*) exit 0 ;; *) exit 1 ;; esac", BranchControlRules.NetlifyIgnoreCommand());
        Assert.Empty(BranchControlRules.VercelMissing(Vercel(BranchControlRules.VercelSuggestion().Trim('{', '}', ' ')), WithAide));
        Assert.Empty(BranchControlRules.NetlifyMissing($"[build]\n  ignore = '{BranchControlRules.NetlifyIgnoreCommand()}'\n", WithAide));
    }

    [Fact]
    public void FR234_The_fake_stack_scripts_write_config_that_blocks_both_prefixes()
    {
        var vercel = FakeStackScripts.Content("deploy-engineer", "frontend/vercel.json", "shop", 2, "Deploy config");
        var netlify = FakeStackScripts.Content("deploy-engineer", "frontend/netlify.toml", "shop", 2, "Deploy config");

        Assert.NotNull(vercel);
        Assert.NotNull(netlify);
        Assert.Empty(BranchControlRules.VercelMissing(vercel, WithAide));
        Assert.Empty(BranchControlRules.NetlifyMissing(netlify, WithAide));
    }

    [Fact]
    public void FR234_The_deploy_engineer_text_and_Vercel_pack_show_both_tevrin_and_aide_rules()
    {
        var files = StackTestCatalog.ShippedFiles();
        string File(string suffix) => Assert.Single(files, f => f.Key.EndsWith(suffix, StringComparison.Ordinal)).Value;

        var engineer = File("agents/deploy-engineer.md");
        Assert.Contains("\"tevrin/**\": false", engineer, StringComparison.Ordinal);
        Assert.Contains("\"aide/**\": false", engineer, StringComparison.Ordinal);
        Assert.Contains("\"tevrin/**\": false, \"aide/**\": false", File("deploy-vercel/skill.md"), StringComparison.Ordinal);
        Assert.Contains("tevrin/** or aide/**", File("deploy-vercel/pack.json"), StringComparison.Ordinal);
        Assert.Contains("tevrin/*|aide/*", File("deploy-netlify/skill.md"), StringComparison.Ordinal);
        Assert.Contains("tevrin/* and aide/*", File("deploy-netlify/pack.json"), StringComparison.Ordinal);
        Assert.Contains("`tevrin/*` and `aide/*`", File("deploy-cloudflare/skill.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void FR234_The_Cloudflare_preview_exclusions_cover_both_prefixes() =>
        Assert.Equal(["tevrin/*", "aide/*"], CloudflareRules.TevrinBranchPatterns);
}
