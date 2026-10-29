using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Platforms;

namespace Aide.UnitTests.Platforms;

/// <summary>FR-142, FR-143, FR-144, FR-145, §4.20, §4.21, §5: platform connection, target and Pages permission rules.</summary>
public class PlatformConnectionRulesTests
{
    [Fact]
    public void FR142_A_connection_needs_a_label_a_well_formed_token_and_a_well_formed_account_id()
    {
        Assert.True(PlatformConnectionRules.ValidateNew("Team", "nfp_abc", null).IsSuccess);
        Assert.True(PlatformConnectionRules.ValidateNew("Team", "rw_abc", "ws_1-a.b:c").IsSuccess);
        AssertProblem(PlatformConnectionRules.ValidateNew(" ", "abc", null), "label");
        AssertProblem(PlatformConnectionRules.ValidateNew("Team", "a b", null), "token");
        AssertProblem(PlatformConnectionRules.ValidateNew("Team", "abc", "not an id"), "accountId");
        AssertProblem(PlatformConnectionRules.ValidateNew("Team", "abc", new string('a', PlatformConnectionRules.MaxAccountIdLength + 1)), "accountId");
        AssertProblem(PlatformConnectionRules.ValidateLabel(""), "label");
    }

    [Fact]
    public void FR142_A_Cloudflare_account_owned_cfat_token_is_CONNECTION_SCOPE_INSUFFICIENT()
    {
        var refused = PlatformConnectionRules.CheckTokenKind(PlatformProvider.Cloudflare, "cfat_abc");
        Assert.Equal(ErrorCodes.ConnectionScopeInsufficient, refused.Error!.Code);
        Assert.Contains("Workers Builds needs a user API token", refused.Error.Message, StringComparison.Ordinal);
        Assert.True(PlatformConnectionRules.CheckTokenKind(PlatformProvider.Cloudflare, "user_token").IsSuccess);
        Assert.True(PlatformConnectionRules.CheckTokenKind(PlatformProvider.Netlify, "cfat_abc").IsSuccess);
    }

    [Theory]
    [InlineData(PlatformProvider.Netlify, 401, null, PlatformFailure.Rejected)]
    [InlineData(PlatformProvider.Supabase, 403, null, PlatformFailure.MissingPermissions)]
    [InlineData(PlatformProvider.Cloudflare, 403, "1000", PlatformFailure.Rejected)]
    [InlineData(PlatformProvider.Cloudflare, 401, "10000", PlatformFailure.Rejected)]
    [InlineData(PlatformProvider.Cloudflare, 403, "9109", PlatformFailure.MissingPermissions)]
    [InlineData(PlatformProvider.Cloudflare, 403, "10000", PlatformFailure.MissingPermissions)]
    [InlineData(PlatformProvider.Cloudflare, 403, "9999", PlatformFailure.MissingPermissions)]
    [InlineData(PlatformProvider.Render, 429, null, PlatformFailure.RateLimited)]
    [InlineData(PlatformProvider.Render, 404, null, PlatformFailure.NotFound)]
    [InlineData(PlatformProvider.Railway, 500, null, PlatformFailure.Upstream)]
    [InlineData(PlatformProvider.Railway, null, null, PlatformFailure.Upstream)]
    public void FR142_Platform_answers_are_classified(PlatformProvider provider, int? status, string? code, PlatformFailure expected) =>
        Assert.Equal(expected, PlatformConnectionRules.Classify(provider, status, code));

    [Fact]
    public void FR142_Vercel_keeps_its_v2_classification_and_Railway_reads_GraphQL_errors()
    {
        Assert.Equal(PlatformFailure.Rejected, PlatformConnectionRules.FromVercel(VercelFailure.CredentialRejected));
        Assert.Equal(PlatformFailure.MissingPermissions, PlatformConnectionRules.FromVercel(VercelFailure.Forbidden));
        Assert.Equal(PlatformFailure.RateLimited, PlatformConnectionRules.FromVercel(VercelFailure.RateLimited));
        Assert.Equal(PlatformFailure.Upstream, PlatformConnectionRules.FromVercel(VercelFailure.PlanLimit));
        Assert.Equal(PlatformFailure.Rejected, PlatformConnectionRules.ClassifyRailwayError("Not Authorized"));
        Assert.Equal(PlatformFailure.RateLimited, PlatformConnectionRules.ClassifyRailwayError("Rate limit exceeded"));
        Assert.Equal(PlatformFailure.NotFound, PlatformConnectionRules.ClassifyRailwayError("Project not found"));
        Assert.Equal(PlatformFailure.Upstream, PlatformConnectionRules.ClassifyRailwayError("boom"));
    }

    [Fact]
    public void FR142_The_status_after_a_check_follows_the_failure_and_the_missing_permissions()
    {
        Assert.Equal(PlatformConnectionStatus.Valid, PlatformConnectionRules.StatusAfter(null, 0));
        Assert.Equal(PlatformConnectionStatus.InsufficientScope, PlatformConnectionRules.StatusAfter(null, 1));
        Assert.Equal(PlatformConnectionStatus.Invalid, PlatformConnectionRules.StatusAfter(PlatformFailure.Rejected, 0));
        Assert.Equal(PlatformConnectionStatus.InsufficientScope, PlatformConnectionRules.StatusAfter(PlatformFailure.MissingPermissions, 0));
        Assert.Null(PlatformConnectionRules.StatusAfter(PlatformFailure.RateLimited, 0));
        Assert.Null(PlatformConnectionRules.StatusAfter(PlatformFailure.Upstream, 0));
    }

    [Fact]
    public void FR142_Save_and_use_errors_map_to_the_spec_codes()
    {
        Assert.Equal(ErrorCodes.CredentialInvalid, PlatformConnectionRules.SaveError(PlatformProvider.Render, PlatformFailure.Rejected)!.Code);
        Assert.Equal(ErrorCodes.UpstreamRateLimited, PlatformConnectionRules.SaveError(PlatformProvider.Render, PlatformFailure.RateLimited)!.Code);
        Assert.Equal(ErrorCodes.UpstreamError, PlatformConnectionRules.SaveError(PlatformProvider.Render, PlatformFailure.NotFound)!.Code);
        Assert.Null(PlatformConnectionRules.SaveError(PlatformProvider.Supabase, PlatformFailure.MissingPermissions));

        Assert.Equal(ErrorCodes.CredentialInvalid, PlatformConnectionRules.UseError(PlatformProvider.Netlify, PlatformFailure.Rejected).Code);
        var scope = PlatformConnectionRules.UseError(PlatformProvider.Supabase, PlatformFailure.MissingPermissions, ["API Key Secrets: Read"]);
        Assert.Equal((ErrorCodes.ConnectionScopeInsufficient, "API Key Secrets: Read"), (scope.Code, scope.Details[0]));
        Assert.Equal(ErrorCodes.NotFound, PlatformConnectionRules.UseError(PlatformProvider.Netlify, PlatformFailure.NotFound).Code);
        Assert.Equal(ErrorCodes.UpstreamError, PlatformConnectionRules.UseError(PlatformProvider.Netlify, PlatformFailure.Upstream).Code);
        Assert.Equal(502, ErrorCodes.StatusCodeFor(ErrorCodes.UpstreamError));
        Assert.Equal(429, ErrorCodes.StatusCodeFor(ErrorCodes.UpstreamRateLimited));
    }

    [Fact]
    public void FR142_A_403_without_names_still_records_a_missing_permission()
    {
        Assert.Equal(["unknown"], PlatformConnectionRules.MissingOrPlaceholder([]));
        Assert.Equal(["unknown"], PlatformConnectionRules.MissingOrPlaceholder(null));
        Assert.Equal(["API Key Secrets: Read"], PlatformConnectionRules.MissingOrPlaceholder([], "API Key Secrets: Read"));
        Assert.Equal(["Projects: Read"], PlatformConnectionRules.MissingOrPlaceholder(["Projects: Read"], "API Key Secrets: Read"));
    }

    [Fact]
    public void FR142_The_owner_is_notified_once_when_a_connection_becomes_invalid()
    {
        Assert.True(PlatformConnectionRules.NotifiesInvalid(PlatformConnectionStatus.Valid, PlatformConnectionStatus.Invalid));
        Assert.True(PlatformConnectionRules.NotifiesInvalid(PlatformConnectionStatus.InsufficientScope, PlatformConnectionStatus.Invalid));
        Assert.False(PlatformConnectionRules.NotifiesInvalid(PlatformConnectionStatus.Invalid, PlatformConnectionStatus.Invalid));
        Assert.False(PlatformConnectionRules.NotifiesInvalid(PlatformConnectionStatus.Valid, PlatformConnectionStatus.InsufficientScope));
    }

    [Fact]
    public void FR142_Picker_types_per_provider_default_to_the_first_and_others_are_VALIDATION()
    {
        Assert.Equal(PlatformResourceType.Projects, PlatformConnectionRules.ResourceType(PlatformProvider.Vercel, null).Value);
        Assert.Equal(PlatformResourceType.Sites, PlatformConnectionRules.ResourceType(PlatformProvider.Netlify, null).Value);
        Assert.Equal(PlatformResourceType.Workers, PlatformConnectionRules.ResourceType(PlatformProvider.Cloudflare, null).Value);
        Assert.Equal(PlatformResourceType.Projects, PlatformConnectionRules.ResourceType(PlatformProvider.Cloudflare, "projects").Value);
        Assert.Equal(PlatformResourceType.Regions, PlatformConnectionRules.ResourceType(PlatformProvider.Render, "regions").Value);
        Assert.Equal(PlatformResourceType.Orgs, PlatformConnectionRules.ResourceType(PlatformProvider.Supabase, "orgs").Value);
        AssertProblem(PlatformConnectionRules.ResourceType(PlatformProvider.Netlify, "workers"), "type");
        AssertProblem(PlatformConnectionRules.ResourceType(PlatformProvider.Vercel, "databases"), "type");
    }

    [Fact]
    public void FR142_The_account_is_the_one_asked_for_or_the_only_one()
    {
        IReadOnlyList<PlatformAccountRef> one = [new("acc_1", "Acme", "acme")];
        IReadOnlyList<PlatformAccountRef> two = [new("acc_1", "Acme", "acme"), new("acc_2", "Ana")];
        Assert.Equal("acc_1", PlatformConnectionRules.ChooseAccount(one, null)!.Id);
        Assert.Null(PlatformConnectionRules.ChooseAccount(two, null));
        Assert.Equal("acc_1", PlatformConnectionRules.ChooseAccount(two, "acme")!.Id);
        Assert.Equal("acc_2", PlatformConnectionRules.ChooseAccount(two, "acc_2")!.Id);
        Assert.Null(PlatformConnectionRules.ChooseAccount(two, "acc_3"));
    }

    [Fact]
    public void FR142_S5_wire_values()
    {
        Assert.Equal(["vercel", "netlify", "cloudflare", "render", "railway", "supabase"], EnumStrings.Values<PlatformProvider>());
        Assert.Equal(["valid", "invalid", "insufficient_scope", "unchecked"], EnumStrings.Values<PlatformConnectionStatus>());
        Assert.Equal(["sites", "services", "projects", "workers", "orgs", "regions"], EnumStrings.Values<PlatformResourceType>());
        Assert.Equal(["frontend", "backend"], EnumStrings.Values<DeploymentComponent>());
        Assert.Equal(["not_configured", "configuring", "ready", "attention", "error"], EnumStrings.Values<DeploymentTargetStatus>());
        Assert.Equal(["deploy", "rollback", "promote"], EnumStrings.Values<DeploymentKind>());
        Assert.Equal(["user", "derived"], EnumStrings.Values<DeploymentEnvSource>());
    }

    [Theory]
    [InlineData(true, true, DeploymentTargetStatus.Ready, null)]
    [InlineData(false, true, DeploymentTargetStatus.Attention, "connection_missing")]
    [InlineData(true, false, DeploymentTargetStatus.NotConfigured, null)]
    [InlineData(false, false, DeploymentTargetStatus.NotConfigured, null)]
    public void FR144_Target_status_follows_its_binding_and_resource(bool connection, bool resource, DeploymentTargetStatus status, string? reason) =>
        Assert.Equal((status, reason), DeploymentTargetRules.StatusOf(connection, resource));

    [Fact]
    public void FR150_S5_A_promote_row_is_a_promote_and_everything_else_deploys()
    {
        Assert.Equal(DeploymentKind.Promote, DeploymentTargetRules.KindOf(DeploymentTrigger.Promote));
        Assert.Equal(DeploymentKind.Deploy, DeploymentTargetRules.KindOf(DeploymentTrigger.Redeploy));
        Assert.Equal(DeploymentKind.Deploy, DeploymentTargetRules.KindOf(DeploymentTrigger.Auto));
    }

    [Theory]
    [InlineData("NEXT_PUBLIC_API_URL", true)]
    [InlineData("NEXT_PUBLICITY", false)]
    [InlineData("next_public_x", false)]
    [InlineData("SECRET", false)]
    public void FR145_NEXT_PUBLIC_variables_are_public(string key, bool expected) =>
        Assert.Equal(expected, DeploymentEnvRules.IsPublicKey(key));

    [Fact]
    public void FR143_The_Pages_permission_is_granted_only_with_pages_write()
    {
        Assert.Equal(GitHubPagesAccess.Granted, GitHubRules.PagesPermission(new Dictionary<string, string> { ["pages"] = "write" }));
        Assert.Equal(GitHubPagesAccess.Missing, GitHubRules.PagesPermission(new Dictionary<string, string> { ["pages"] = "read" }));
        Assert.Equal(GitHubPagesAccess.Missing, GitHubRules.PagesPermission(new Dictionary<string, string> { ["contents"] = "write" }));
        Assert.Equal(GitHubPagesAccess.Unknown, GitHubRules.PagesPermission(null));
        Assert.Equal("write", GitHubRules.ManifestPermissions["pages"]);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.PlatformPermissionMissing));
    }

    private static void AssertProblem(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith($"{field}:", StringComparison.Ordinal));
    }
}
