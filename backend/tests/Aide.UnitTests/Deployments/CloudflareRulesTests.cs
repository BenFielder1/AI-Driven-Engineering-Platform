using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-149, FR-150, §4.20: Cloudflare Workers Builds and Pages rules.</summary>
public class CloudflareRulesTests
{
    [Theory]
    [InlineData("queued", null, DeploymentStatus.Queued)]
    [InlineData("initializing", null, DeploymentStatus.Queued)]
    [InlineData("running", null, DeploymentStatus.Building)]
    [InlineData("stopped", "success", DeploymentStatus.Ready)]
    [InlineData("stopped", "fail", DeploymentStatus.Error)]
    [InlineData("stopped", "terminated", DeploymentStatus.Error)]
    [InlineData("stopped", "cancelled", DeploymentStatus.Canceled)]
    [InlineData("stopped", "skipped", DeploymentStatus.Canceled)]
    [InlineData("stopped", null, DeploymentStatus.Building)]
    public void FR149_Workers_Builds_states_map_as_in_the_4_20_table(string status, string? outcome, DeploymentStatus expected) =>
        Assert.Equal(expected, CloudflareRules.BuildStateOf(status, outcome));

    [Theory]
    [InlineData("deploy", "success", DeploymentStatus.Ready)]
    [InlineData("build", "success", DeploymentStatus.Building)]
    [InlineData("build", "active", DeploymentStatus.Building)]
    [InlineData("queued", "idle", DeploymentStatus.Building)]
    [InlineData("build", "failure", DeploymentStatus.Error)]
    [InlineData("build", "canceled", DeploymentStatus.Canceled)]
    [InlineData("deploy", "skipped", DeploymentStatus.Canceled)]
    public void FR149_Pages_states_map_on_latest_stage(string stage, string status, DeploymentStatus expected) =>
        Assert.Equal(expected, CloudflareRules.PagesStateOf(stage, status));

    [Fact]
    public void FR144_The_production_trigger_builds_main_only_with_wrangler_deploy()
    {
        var trigger = CloudflareRules.ProductionTrigger("main", "frontend", "npm run build");

        Assert.Equal(["main"], trigger.BranchIncludes);
        Assert.Empty(trigger.BranchExcludes);
        Assert.Equal("npx wrangler deploy", trigger.DeployCommand);
        Assert.Equal("frontend", trigger.RootDirectory);
        Assert.Equal("npm run build", trigger.BuildCommand);
    }

    [Fact]
    public void FR144_The_preview_trigger_excludes_aide_branches_and_uploads_versions()
    {
        var trigger = CloudflareRules.PreviewTrigger(".", null);

        Assert.Equal(["aide/*"], trigger.BranchExcludes);
        Assert.Equal("npx wrangler versions upload", trigger.DeployCommand);
        Assert.Equal("/", trigger.RootDirectory);
    }

    [Fact]
    public void FR144_Both_trigger_ids_are_kept_preview_first()
    {
        Assert.Equal(("p1", "t1"), CloudflareRules.ParseTriggerIds(CloudflareRules.TriggerIds("p1", "t1")));
        Assert.Equal(("p1", (string?)null), CloudflareRules.ParseTriggerIds("p1"));
        Assert.Equal(((string?)null, (string?)null), CloudflareRules.ParseTriggerIds(null));
    }

    [Fact]
    public void FR145_A_preview_only_secret_is_refused_for_Workers()
    {
        var refused = CloudflareRules.ValidateRuntimeSecret(true, [DeploymentEnvironment.Preview]);

        Assert.Equal(ErrorCodes.Validation, refused.Error!.Code);
        Assert.True(CloudflareRules.ValidateRuntimeSecret(true, [DeploymentEnvironment.Preview, DeploymentEnvironment.Production]).IsSuccess);
        Assert.True(CloudflareRules.ValidateRuntimeSecret(false, [DeploymentEnvironment.Preview]).IsSuccess);
    }

    [Fact]
    public void FR150_Rollback_deploys_the_version_at_100_percent()
    {
        var body = CloudflareRules.RollbackDeployment("v-1");

        Assert.Equal("percentage", body["strategy"]);
        var version = Assert.Single((Dictionary<string, object>[])body["versions"]);
        Assert.Equal("v-1", version["version_id"]);
        Assert.Equal(100, version["percentage"]);
    }

    [Fact]
    public void FR150_A_version_matches_the_build_commit_by_its_annotations()
    {
        Assert.True(CloudflareRules.VersionMatchesCommit(["deploy abc1234 from main"], "abc1234def"));
        Assert.False(CloudflareRules.VersionMatchesCommit(["other"], "abc1234def"));
        Assert.False(CloudflareRules.VersionMatchesCommit(["abc"], "abc"));
    }

    [Fact]
    public void FR145_Env_ids_round_trip()
    {
        Assert.Equal(("KEY", DeploymentEnvironment.Production), CloudflareRules.ParseEnvId(CloudflareRules.EnvId("KEY", DeploymentEnvironment.Production)));
        Assert.Null(CloudflareRules.ParseEnvId("netlify:production:KEY"));
    }

    [Theory]
    [InlineData("https://a1b2c3d4.todo-web.pages.dev", "https://todo-web.pages.dev")]
    [InlineData("https://a1b2c3d4.todo-web-x9.pages.dev/", "https://todo-web-x9.pages.dev")]
    [InlineData("https://todo-web.pages.dev", null)]
    [InlineData("https://todo.example.com", null)]
    [InlineData(null, null)]
    public void FR151_The_Pages_production_URL_is_the_project_host_not_the_deployment_hash_URL(string? url, string? expected) =>
        Assert.Equal(expected, CloudflareRules.PagesProductionUrl(url));

    [Fact]
    public void FR149_A_redeploy_adopts_only_builds_created_after_its_row_and_auto_adoption_any_build()
    {
        var row = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CloudflareRules.Adoptable(row.AddMinutes(-30), null));
        Assert.True(CloudflareRules.Adoptable(null, null));
        Assert.True(CloudflareRules.Adoptable(row.AddSeconds(5), row));
        Assert.True(CloudflareRules.Adoptable(row, row));
        Assert.False(CloudflareRules.Adoptable(row.AddMinutes(-30), row));
        Assert.False(CloudflareRules.Adoptable(null, row));
    }
}
