using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-87, FR-88, FR-90, FR-91, §4.15: Vercel connections, deployment settings, env vars and deployment actions.</summary>
public class DeploymentRulesTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Ben = Guid.CreateVersion7();

    // ------------------------------------------------------------------ FR-87 connections

    [Fact]
    public void FR87_A_connection_needs_a_label_and_a_well_formed_token()
    {
        Assert.True(VercelConnectionRules.ValidateNew("Team", "vcp_AbC123xyz").IsSuccess);
        Assert.True(VercelConnectionRules.ValidateNew("  Personal  ", "abc").IsSuccess);
        AssertProblem(VercelConnectionRules.ValidateNew(" ", "abc"), "label");
        AssertProblem(VercelConnectionRules.ValidateNew(new string('a', VercelConnectionRules.MaxLabelLength + 1), "abc"), "label");
        AssertProblem(VercelConnectionRules.ValidateNew("Team", ""), "token");
        AssertProblem(VercelConnectionRules.ValidateNew("Team", new string('t', VercelConnectionRules.MaxTokenLength + 1)), "token");
    }

    [Theory]
    [InlineData("abc def")]
    [InlineData("abc\n")]
    [InlineData("abc\u0001")]
    public void FR87_Tokens_with_whitespace_or_control_characters_are_refused(string token) =>
        AssertProblem(VercelConnectionRules.ValidateNew("Team", token), "token");

    [Fact]
    public void FR87_Renaming_needs_a_label()
    {
        Assert.True(VercelConnectionRules.ValidateLabel("Renamed").IsSuccess);
        AssertProblem(VercelConnectionRules.ValidateLabel(null), "label");
    }

    [Fact]
    public void FR87_S5_Vercel_connection_wire_values()
    {
        Assert.Equal(["account", "team"], EnumStrings.Values<VercelTokenScope>());
        Assert.Equal(["valid", "invalid", "unchecked"], EnumStrings.Values<VercelConnectionStatus>());
    }

    // ------------------------------------------------------------------ FR-88 settings

    [Fact]
    public void FR88_Link_an_existing_Vercel_project_or_create_one()
    {
        Assert.True(Settings(DeploymentProvider.Vercel, Ana, "prj_abc123").IsSuccess);
        Assert.True(Settings(DeploymentProvider.Vercel, Ana, null, create: true, name: "todo-app", root: "apps/web").IsSuccess);
        Assert.True(Settings(DeploymentProvider.Vercel, null, null).IsSuccess);
        AssertProblem(Settings(DeploymentProvider.Vercel, Ana, "prj_abc", create: true, name: "todo-app"), "create");
        AssertProblem(Settings(DeploymentProvider.Vercel, Ana, "prj abc"), "vercelProjectId");
    }

    [Theory]
    [InlineData("Todo-App")]
    [InlineData("todo---app")]
    [InlineData("todo app")]
    [InlineData("")]
    public void FR88_A_new_Vercel_project_name_is_lowercase_without_triple_dashes(string name) =>
        AssertProblem(Settings(DeploymentProvider.Vercel, Ana, null, create: true, name: name), "create.name");

    [Theory]
    [InlineData("/apps/web")]
    [InlineData("../web")]
    [InlineData("apps/../web")]
    [InlineData("apps//web")]
    [InlineData("apps/w b")]
    public void FR88_The_root_directory_is_a_relative_path_inside_the_repo(string root) =>
        AssertProblem(Settings(DeploymentProvider.Vercel, Ana, null, create: true, name: "todo", root: root), "create.rootDirectory");

    [Theory]
    [InlineData(".")]
    [InlineData("apps/web")]
    [InlineData("apps/web/")]
    public void FR88_Valid_root_directories(string root) => Assert.True(DeploymentSettingsRules.IsRootDirectory(root));

    [Fact]
    public void FR88_Local_Git_projects_cannot_deploy()
    {
        var result = DeploymentSettingsRules.Validate(DeploymentProvider.Vercel, Ana, "prj_a", false, null, null, GitProvider.Local, false, false);

        AssertProblem(result, "provider");
        Assert.True(DeploymentSettingsRules.Validate(DeploymentProvider.Vercel, Ana, "prj_a", false, null, null, GitProvider.GithubPat, false, false).IsSuccess);
    }

    [Fact]
    public void FR88_Turning_deployment_off_takes_no_other_field()
    {
        Assert.True(Settings(null, null, null).IsSuccess);
        AssertProblem(Settings(null, Ana, null), "provider");
        AssertProblem(Settings(null, null, "prj_a"), "provider");
    }

    [Fact]
    public void FR88_The_fake_provider_only_where_allowed()
    {
        AssertProblem(Settings(DeploymentProvider.Fake, Ana, "prj_a"), "provider");
        Assert.True(DeploymentSettingsRules.Validate(DeploymentProvider.Fake, Ana, "prj_a", false, null, null, GitProvider.GithubApp, true, false).IsSuccess);
    }

    [Fact]
    public void FR88_The_wizard_needs_a_connection_and_prefixes_its_fields()
    {
        var result = DeploymentSettingsRules.Validate(DeploymentProvider.Vercel, null, null, false, null, null, GitProvider.GithubApp, false, true, "deployment.");

        AssertProblem(result, "deployment.connectionId");
    }

    [Fact]
    public void FR88_An_Owner_binds_only_their_own_connection()
    {
        Assert.True(DeploymentSettingsRules.CanBind(Ana, ProjectRole.Owner, Ana).IsSuccess);
        Assert.Equal(ErrorCodes.CredentialNotOwner, DeploymentSettingsRules.CanBind(Ana, ProjectRole.Owner, Ben).Error!.Code);
        Assert.Equal(ErrorCodes.NotFound, DeploymentSettingsRules.CanBind(Ana, ProjectRole.Owner, null).Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, DeploymentSettingsRules.CanBind(Ana, ProjectRole.Contributor, Ana).Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, DeploymentSettingsRules.CanBind(Ana, null, Ana).Error!.Code);
    }

    [Fact]
    public void FR88_CREDENTIAL_NOT_OWNER_is_403_and_the_Vercel_conflicts_are_409()
    {
        Assert.Equal(403, ErrorCodes.StatusCodeFor(ErrorCodes.CredentialNotOwner));
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.VercelGitNotConnected));
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.VercelPlanLimit));
        Assert.Equal(502, ErrorCodes.StatusCodeFor(ErrorCodes.UpstreamError));
    }

    // ------------------------------------------------------------------ FR-88 env vars

    [Theory]
    [InlineData("NEXT_PUBLIC_SUPABASE_URL")]
    [InlineData("_PRIVATE")]
    [InlineData("a1")]
    public void FR88_Env_keys_letters_digits_and_underscore(string key) =>
        Assert.True(DeploymentEnvRules.Validate(key, "v", [DeploymentTarget.Preview]).IsSuccess);

    [Theory]
    [InlineData("1KEY")]
    [InlineData("MY-KEY")]
    [InlineData("MY KEY")]
    [InlineData("")]
    public void FR88_Invalid_env_keys_are_refused(string key) =>
        AssertProblem(DeploymentEnvRules.Validate(key, "v", [DeploymentTarget.Preview]), "key");

    [Fact]
    public void FR88_Env_key_length_value_size_and_targets()
    {
        Assert.True(DeploymentEnvRules.IsKey(new string('K', DeploymentEnvVar.KeyMaxLength)));
        Assert.False(DeploymentEnvRules.IsKey(new string('K', DeploymentEnvVar.KeyMaxLength + 1)));
        Assert.True(DeploymentEnvRules.Validate("K", new string('v', DeploymentEnvRules.ValueMaxBytes), [DeploymentTarget.Production]).IsSuccess);
        AssertProblem(DeploymentEnvRules.Validate("K", new string('v', DeploymentEnvRules.ValueMaxBytes + 1), [DeploymentTarget.Production]), "value");

        // 64 KB is counted in UTF-8 bytes: 3 bytes per "€".
        AssertProblem(DeploymentEnvRules.Validate("K", new string('€', (DeploymentEnvRules.ValueMaxBytes / 3) + 1), [DeploymentTarget.Production]), "value");
        AssertProblem(DeploymentEnvRules.Validate("K", "", [DeploymentTarget.Production]), "value");
        AssertProblem(DeploymentEnvRules.Validate("K", "v", []), "targets");
        AssertProblem(DeploymentEnvRules.Validate("K", "v", [DeploymentTarget.Preview, DeploymentTarget.Preview]), "targets");
        Assert.True(DeploymentEnvRules.Validate("K", "v", [DeploymentTarget.Preview, DeploymentTarget.Production]).IsSuccess);
    }

    // ------------------------------------------------------------------ FR-90, FR-91, §4.15 deployments

    [Fact]
    public void S4_15_Deployment_wire_values()
    {
        Assert.Equal(["queued", "building", "ready", "error", "canceled"], EnumStrings.Values<DeploymentStatus>());
        Assert.Equal(["preview", "production"], EnumStrings.Values<DeploymentTarget>());
        Assert.Equal(["vercel", "fake"], EnumStrings.Values<DeploymentProvider>());
        Assert.Equal(["auto", "manual", "promote", "redeploy"], EnumStrings.Values<DeploymentTrigger>());
    }

    [Fact]
    public void FR91_Only_a_ready_production_deployment_can_be_promoted()
    {
        Assert.True(DeploymentActionRules.CanPromote(DeploymentTarget.Production, DeploymentStatus.Ready).IsSuccess);
        foreach (var status in Enum.GetValues<DeploymentStatus>().Where(s => s != DeploymentStatus.Ready))
        {
            Assert.Equal(ErrorCodes.InvalidDeployment, DeploymentActionRules.CanPromote(DeploymentTarget.Production, status).Error!.Code);
        }

        Assert.Equal(ErrorCodes.InvalidDeployment, DeploymentActionRules.CanPromote(DeploymentTarget.Preview, DeploymentStatus.Ready).Error!.Code);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.InvalidDeployment));
    }

    [Fact]
    public void FR90_FR91_A_writer_redeploys_a_preview_an_Owner_production()
    {
        Assert.Equal(ProjectAction.TriggerDeployment, DeploymentActionRules.RedeployAction(DeploymentTarget.Preview));
        Assert.Equal(ProjectAction.ManageDeployments, DeploymentActionRules.RedeployAction(DeploymentTarget.Production));
        Assert.Equal(ProjectRole.Contributor, ProjectPermissions.MinimumRole(ProjectAction.TriggerDeployment));
        Assert.Equal(ProjectRole.Owner, ProjectPermissions.MinimumRole(ProjectAction.ManageDeployments));
    }

    [Fact]
    public void FR90_S4_15_Deployment_failure_codes_are_recorded_not_returned()
    {
        Assert.Equal(("DEPLOYMENT_TIMEOUT", "VERCEL_ERROR"), (ErrorCodes.DeploymentTimeout, ErrorCodes.VercelError));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.DeploymentTimeout));
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.VercelError));
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.DeploymentNotConfigured));
        Assert.Equal(("DEPLOYMENT_CREATED", "DEPLOYMENT_UPDATED"), (EventTypes.DeploymentCreated, EventTypes.DeploymentUpdated));
    }

    private static Result Settings(DeploymentProvider? provider, Guid? connectionId, string? vercelProjectId, bool create = false, string? name = null, string? root = null) =>
        DeploymentSettingsRules.Validate(provider, connectionId, vercelProjectId, create, name, root, GitProvider.GithubApp, allowFake: false, requireConnection: false);

    private static void AssertProblem(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith(field + ":", StringComparison.Ordinal));
    }
}
