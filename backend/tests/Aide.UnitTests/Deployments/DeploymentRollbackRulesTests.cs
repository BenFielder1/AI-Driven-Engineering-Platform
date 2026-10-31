using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-150, FR-151: rollback per provider capability, valid rollback targets and the Cloudflare mode.</summary>
public class DeploymentRollbackRulesTests
{
    [Theory]
    [InlineData(DeploymentProvider.Vercel, "prj_1", RollbackMethod.VercelPromote)]
    [InlineData(DeploymentProvider.Netlify, "site-1", RollbackMethod.NetlifyRestore)]
    [InlineData(DeploymentProvider.Cloudflare, "my-worker", RollbackMethod.CloudflareWorkersVersion)]
    [InlineData(DeploymentProvider.Cloudflare, "pages:my-site", RollbackMethod.CloudflarePagesRollback)]
    [InlineData(DeploymentProvider.Render, "srv-1", RollbackMethod.RenderRollback)]
    [InlineData(DeploymentProvider.Railway, "svc", RollbackMethod.RailwayRollback)]
    [InlineData(DeploymentProvider.GithubPages, "o/r", RollbackMethod.None)]
    public void FR150_Rollback_dispatches_on_the_provider_capability(DeploymentProvider platform, string resourceId, RollbackMethod expected) =>
        Assert.Equal(expected, DeploymentRollbackRules.MethodFor(platform, resourceId));

    [Fact]
    public void FR150_The_Fake_of_a_platform_rolls_back_only_where_the_platform_can()
    {
        Assert.Equal(RollbackMethod.Fake, DeploymentRollbackRules.MethodFor(DeploymentProvider.Netlify, "x", fake: true));
        Assert.Equal(RollbackMethod.None, DeploymentRollbackRules.MethodFor(DeploymentProvider.GithubPages, "x", fake: true));
    }

    [Fact]
    public void FR150_Vercel_records_promote_and_the_others_rollback()
    {
        Assert.Equal(DeploymentTrigger.Promote, DeploymentRollbackRules.TriggerFor(DeploymentProvider.Vercel));
        Assert.Equal(DeploymentTrigger.Rollback, DeploymentRollbackRules.TriggerFor(DeploymentProvider.Netlify));
        Assert.Equal(DeploymentTrigger.Rollback, DeploymentRollbackRules.TriggerFor(DeploymentProvider.Cloudflare));
        Assert.Equal(DeploymentKind.Rollback, DeploymentTargetRules.KindOf(DeploymentTrigger.Rollback));
        Assert.Equal(DeploymentKind.Promote, DeploymentTargetRules.KindOf(DeploymentTrigger.Promote));
    }

    [Fact]
    public void FR150_GitHub_Pages_rollback_is_DEPLOYMENT_ACTION_UNSUPPORTED()
    {
        var result = DeploymentRollbackRules.CanRollback(DeploymentProvider.GithubPages, DeploymentEnvironment.Production, DeploymentStatus.Ready, "pages:abc", false);

        Assert.Equal(ErrorCodes.DeploymentActionUnsupported, result.Error!.Code);
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Preview, DeploymentStatus.Ready, "d1", false, DeploymentRollbackRules.ReasonNotProduction)]
    [InlineData(DeploymentEnvironment.Production, DeploymentStatus.Error, "d1", false, DeploymentRollbackRules.ReasonNotReady)]
    [InlineData(DeploymentEnvironment.Production, DeploymentStatus.Ready, "", false, DeploymentRollbackRules.ReasonUnknownAtPlatform)]
    [InlineData(DeploymentEnvironment.Production, DeploymentStatus.Ready, "d1", true, DeploymentRollbackRules.ReasonCurrent)]
    [InlineData(DeploymentEnvironment.Production, DeploymentStatus.Ready, "d1", false, null)]
    public void FR151_canRollback_is_false_with_a_reason_for_invalid_targets(
        DeploymentEnvironment environment, DeploymentStatus status, string providerId, bool current, string? reason)
    {
        Assert.Equal(reason, DeploymentRollbackRules.BlockedReason(DeploymentProvider.Netlify, environment, status, providerId, current));
        var result = DeploymentRollbackRules.CanRollback(DeploymentProvider.Netlify, environment, status, providerId, current);
        Assert.Equal(reason is null, result.IsSuccess);
        if (reason is not null)
        {
            Assert.Equal(ErrorCodes.InvalidDeployment, result.Error!.Code);
        }
    }

    [Fact]
    public void FR150_Vercel_may_promote_the_current_deployment_again_as_in_v2()
    {
        Assert.True(DeploymentRollbackRules.CanRollback(DeploymentProvider.Vercel, DeploymentEnvironment.Production, DeploymentStatus.Ready, "dpl_1", true).IsSuccess);
        Assert.Equal(
            DeploymentRollbackRules.ReasonUnsupported,
            DeploymentRollbackRules.BlockedReason(DeploymentProvider.GithubPages, DeploymentEnvironment.Production, DeploymentStatus.Ready, "x", false));
    }

    [Fact]
    public void FR144_A_Cloudflare_target_is_in_Pages_mode_when_its_resource_id_is_pages_prefixed()
    {
        Assert.True(DeploymentRollbackRules.IsCloudflarePages("pages:site"));
        Assert.Equal("site", DeploymentRollbackRules.CloudflarePagesProject("pages:site"));
        Assert.False(DeploymentRollbackRules.IsCloudflarePages("worker"));
        Assert.False(DeploymentRollbackRules.IsCloudflarePages("pages:"));
        Assert.Equal("pages:site", DeploymentRollbackRules.CloudflarePagesResourceId("site"));
    }
}
