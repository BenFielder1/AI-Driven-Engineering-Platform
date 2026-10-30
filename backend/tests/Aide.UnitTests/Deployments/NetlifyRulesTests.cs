using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-149, §4.20: Netlify's state mapping, deploy limit, env contexts, ids and branch control.</summary>
public class NetlifyRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("new", DeploymentStatus.Queued)]
    [InlineData("pending_review", DeploymentStatus.Queued)]
    [InlineData("accepted", DeploymentStatus.Queued)]
    [InlineData("enqueued", DeploymentStatus.Queued)]
    [InlineData("building", DeploymentStatus.Building)]
    [InlineData("uploading", DeploymentStatus.Building)]
    [InlineData("uploaded", DeploymentStatus.Building)]
    [InlineData("preparing", DeploymentStatus.Building)]
    [InlineData("prepared", DeploymentStatus.Building)]
    [InlineData("processing", DeploymentStatus.Building)]
    [InlineData("processed", DeploymentStatus.Building)]
    [InlineData("retrying", DeploymentStatus.Building)]
    [InlineData("ready", DeploymentStatus.Ready)]
    [InlineData("error", DeploymentStatus.Error)]
    [InlineData("rejected", DeploymentStatus.Error)]
    [InlineData("skipped", DeploymentStatus.Error)]
    [InlineData("canceled", DeploymentStatus.Error)]
    [InlineData("something_new", DeploymentStatus.Building)]
    [InlineData(null, DeploymentStatus.Building)]
    public void FR149_Netlify_deploy_states_map_as_in_the_4_20_table(string? state, DeploymentStatus expected) =>
        Assert.Equal(expected, NetlifyRules.StateOf(state));

    [Fact]
    public void FR149_Netlify_allows_three_deploys_a_minute_and_waits_for_the_oldest_to_leave_the_window()
    {
        DateTimeOffset[] recent = [Now.AddSeconds(-50), Now.AddSeconds(-30), Now.AddSeconds(-10)];

        Assert.True(NetlifyRules.Decide(recent[..2], Now).Allowed);
        var refused = NetlifyRules.Decide(recent, Now);

        Assert.False(refused.Allowed);
        Assert.Equal(Now.AddSeconds(10), refused.RetryAt);
    }

    [Fact]
    public void FR149_Netlify_allows_100_deploys_a_day_per_account()
    {
        var day = Enumerable.Range(1, 100).Select(i => Now.AddMinutes(-10 * i)).ToList();

        var refused = NetlifyRules.Decide(day, Now);

        Assert.False(refused.Allowed);
        Assert.Equal(day.Min().AddDays(1), refused.RetryAt);
        Assert.True(NetlifyRules.Decide(day.Skip(1).ToList(), Now).Allowed);
    }

    [Fact]
    public void FR149_Deploys_older_than_a_day_do_not_count()
    {
        var old = Enumerable.Range(0, 150).Select(i => Now.AddDays(-2).AddMinutes(-i)).ToList();

        Assert.True(NetlifyRules.Decide(old, Now).Allowed);
    }

    [Fact]
    public void FR145_Preview_values_go_to_deploy_preview_and_branch_deploy_and_production_to_production()
    {
        Assert.Equal(["production"], NetlifyRules.ContextsOf(DeploymentEnvironment.Production));
        Assert.Equal(["deploy-preview", "branch-deploy"], NetlifyRules.ContextsOf(DeploymentEnvironment.Preview));
    }

    [Fact]
    public void FR145_Env_ids_round_trip_key_and_environment()
    {
        var id = NetlifyRules.EnvId("API_ORIGIN", DeploymentEnvironment.Preview);

        Assert.Equal("netlify:preview:API_ORIGIN", id);
        Assert.Equal(("API_ORIGIN", DeploymentEnvironment.Preview), NetlifyRules.ParseEnvId(id));
        Assert.Null(NetlifyRules.ParseEnvId("env_123"));
    }

    [Fact]
    public void FR147_A_preview_deployment_id_carries_the_PR_head_and_branch_to_check_the_built_commit()
    {
        var id = NetlifyRules.DeploymentId("d1", "abc123", 1, "aide/v0.2.0");

        Assert.Equal(("d1", "abc123", 1, "aide/v0.2.0"), NetlifyRules.ParseDeploymentId(id));
        Assert.Equal(("d2", (string?)null, 1, (string?)null), NetlifyRules.ParseDeploymentId("d2"));
        Assert.True(NetlifyRules.BuiltOtherCommit("fff000", "abc123"));
        Assert.False(NetlifyRules.BuiltOtherCommit("abc123def", "abc123"));
        Assert.False(NetlifyRules.BuiltOtherCommit(null, "abc123"));
    }

    [Theory]
    [InlineData(402, "Payment required", true)]
    [InlineData(422, "Private repositories owned by an organization need the Pro plan", true)]
    [InlineData(403, "Please upgrade to continue", true)]
    [InlineData(422, "Name already taken", false)]
    [InlineData(500, "upgrade", false)]
    public void FR144_Netlify_plan_refusals_are_PLATFORM_PLAN_LIMIT(int status, string message, bool planLimit) =>
        Assert.Equal(planLimit, NetlifyRules.IsPlanLimit(status, message));

    [Fact]
    public void FR144_Branch_control_needs_a_build_ignore_rule_naming_aide_branches()
    {
        const string Good = "[build]\n  command = \"npm run build\"\n  ignore = \"[ \\\"${BRANCH#aide/}\\\" != \\\"$BRANCH\\\" ]\"\n";
        const string OtherSection = "[context.production]\n  ignore = \"echo aide/\"\n";

        Assert.True(NetlifyRules.IgnoresAideBranches(Good));
        Assert.False(NetlifyRules.IgnoresAideBranches(OtherSection));
        Assert.False(NetlifyRules.IgnoresAideBranches("[build]\n  ignore = \"git diff --quiet\"\n"));
        Assert.False(NetlifyRules.IgnoresAideBranches(null));
    }

    [Theory]
    [InlineData("skipped", null, true)]
    [InlineData("canceled", null, true)]
    [InlineData("error", "Canceled build due to no content change", true)]
    [InlineData("error", "Build skipped by the ignore command", true)]
    [InlineData("error", "Build script returned non-zero exit code: 2", false)]
    [InlineData("ready", null, false)]
    [InlineData("building", null, false)]
    public void FR147_A_build_an_ignore_rule_skipped_is_recognised(string state, string? message, bool skipped) =>
        Assert.Equal(skipped, NetlifyRules.IsSkipped(state, message));

    [Theory]
    [InlineData("abc1234", "abc1234ffee", true)]
    [InlineData("abc1234ffee", "abc1234", true)]
    [InlineData("ABC1234FFEE", "abc1234ffee", true)]
    [InlineData("", "abc1234ffee", false)]
    [InlineData(null, "abc1234ffee", false)]
    [InlineData("abc", "abc1234ffee", false)]
    [InlineData("abc1234", "abc", false)]
    [InlineData("def5678", "abc1234ffee", false)]
    public void FR149_Adoption_needs_a_known_commit_ref_of_at_least_7_characters(string? commitRef, string sha, bool same) =>
        Assert.Equal(same, NetlifyRules.SameCommit(commitRef, sha));

    [Theory]
    [InlineData("https://todo.netlify.app", "https://d1--todo.netlify.app", "https://old.example", "https://todo.netlify.app")]
    [InlineData(null, "https://d1.site.pages.dev", "https://old.example", "https://d1.site.pages.dev")]
    [InlineData(null, null, "https://todo-web.acme.workers.dev", "https://todo-web.acme.workers.dev")]
    [InlineData("", "", null, null)]
    public void FR151_A_ready_production_deploy_never_wipes_a_known_production_URL(string? alias, string? url, string? known, string? expected) =>
        Assert.Equal(expected, DeploymentTargetRules.ProductionUrlAfterReady(alias, url, known));
}
