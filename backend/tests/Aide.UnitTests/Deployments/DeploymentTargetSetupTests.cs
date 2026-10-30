using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-143, FR-144, §4.20: target setup checks, derived build configuration, readiness, capabilities and the GitHub Pages state mapping.</summary>
public class DeploymentTargetSetupTests
{
    private const string MigrateCommand = "./efbundle --connection \"$DATABASE_URL\"";

    private static readonly Guid Connection = Guid.CreateVersion7();

    private static StackComposition PagesAndRailway() => StackTestCatalog.Valid(StackTestCatalog.Sel(
        frontend: "fe-react-vite", backend: "be-fastapi", vcs: "vcs-github", frontendHost: "deploy-github-pages", backendHost: "deploy-railway"));

    private static StackComposition AspNetOnRender() => StackTestCatalog.Valid(StackTestCatalog.Sel(
        backend: "be-aspnet", data: "data-efcore-postgres", vcs: "vcs-github", backendHost: "deploy-render", databaseHost: "render-postgres"));

    private static StackComposition NextOnVercel() => StackTestCatalog.Valid(StackTestCatalog.Sel(
        frontend: "fe-nextjs", vcs: "vcs-github", frontendHost: "deploy-vercel"));

    // ------------------------------------------------------------------ FR-144 setup checks

    [Fact]
    public void FR144_The_composition_names_the_target_providers_per_component()
    {
        var composition = PagesAndRailway();

        Assert.Equal(DeploymentProvider.GithubPages, DeploymentPlatforms.ProviderOf(composition, DeploymentComponent.Frontend));
        Assert.Equal(DeploymentProvider.Railway, DeploymentPlatforms.ProviderOf(composition, DeploymentComponent.Backend));
        Assert.Null(DeploymentPlatforms.ProviderOf(composition with { Backend = null }, DeploymentComponent.Backend));
        Assert.Null(DeploymentPlatforms.ProviderOf(null, DeploymentComponent.Frontend));
        Assert.Equal("github", DeploymentPlatforms.VcsHost(composition));
    }

    [Fact]
    public void FR144_A_provider_other_than_the_compositions_host_is_STACK_INCOMPATIBLE()
    {
        var result = Validate(DeploymentComponent.Frontend, DeploymentProvider.Vercel, PagesAndRailway(), connectionId: Connection, createName: "todo");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.StackIncompatible, result.Error!.Code);
    }

    [Fact]
    public void FR144_A_component_without_a_host_is_STACK_INCOMPATIBLE()
    {
        var result = Validate(DeploymentComponent.Frontend, DeploymentProvider.Render, AspNetOnRender(), connectionId: Connection, createName: "todo");

        Assert.Equal(ErrorCodes.StackIncompatible, result.Error!.Code);
    }

    [Fact]
    public void FR144_S9_The_Fake_provider_is_accepted_only_in_Fake_mode_and_Local_Git_only_in_Fake_mode()
    {
        Assert.True(Validate(DeploymentComponent.Backend, DeploymentProvider.Fake, AspNetOnRender(), GitProvider.Local, fakeMode: true, Connection, "api").IsSuccess);
        Assert.Equal(ErrorCodes.StackIncompatible, Validate(DeploymentComponent.Backend, DeploymentProvider.Fake, AspNetOnRender(), connectionId: Connection, createName: "api").Error!.Code);
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, AspNetOnRender(), GitProvider.Local, connectionId: Connection, createName: "api"), "provider");
    }

    [Fact]
    public void FR143_GitHub_Pages_needs_a_GitHub_repository_but_no_connection_create_or_link()
    {
        Assert.True(Validate(DeploymentComponent.Frontend, DeploymentProvider.GithubPages, PagesAndRailway(), GitProvider.GithubApp, create: false).IsSuccess);
        Assert.True(Validate(DeploymentComponent.Frontend, DeploymentProvider.GithubPages, PagesAndRailway(), GitProvider.GithubPat, create: true).IsSuccess);
        Assert.Equal(ErrorCodes.StackIncompatible, Validate(DeploymentComponent.Frontend, DeploymentProvider.GithubPages, PagesAndRailway(), GitProvider.Gitlab).Error!.Code);
    }

    [Fact]
    public void FR144_A_platform_needs_a_connection_and_exactly_one_of_create_or_link()
    {
        var composition = AspNetOnRender();
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, createName: "api"), "connectionId");
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, connectionId: Connection, createName: "api", create: true, link: "srv-1"), "create");
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, connectionId: Connection, create: false), "create");
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, connectionId: Connection, createName: "Bad Name"), "create.name");
        AssertField(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, connectionId: Connection, create: false, link: "srv 1"), "link.resourceId");
        Assert.True(Validate(DeploymentComponent.Backend, DeploymentProvider.Render, composition, connectionId: Connection, create: false, link: "srv-d0abc123").IsSuccess);
    }

    [Fact]
    public void FR144_Neither_create_nor_link_is_a_rebind_only_when_the_target_keeps_its_resource()
    {
        var composition = AspNetOnRender();

        Assert.True(DeploymentTargetSetupRules.Validate(
            DeploymentComponent.Backend, DeploymentProvider.Render, composition, GitProvider.GithubApp, false, Connection, null, false, null, keepsResource: true).IsSuccess);
        AssertField(DeploymentTargetSetupRules.Validate(
            DeploymentComponent.Backend, DeploymentProvider.Render, composition, GitProvider.GithubApp, false, Connection, null, false, null, keepsResource: false), "create");

        // A rebind still needs a connection where the platform has one.
        AssertField(DeploymentTargetSetupRules.Validate(
            DeploymentComponent.Backend, DeploymentProvider.Render, composition, GitProvider.GithubApp, false, null, null, false, null, keepsResource: true), "connectionId");
    }

    // ------------------------------------------------------------------ FR-144 build configuration

    [Fact]
    public void FR144_An_ASP_NET_backend_on_Render_is_a_Docker_web_service_with_the_ready_check_and_the_migration_pre_deploy()
    {
        var plan = DeploymentBuildRules.Derive(AspNetOnRender(), DeploymentComponent.Backend, DeploymentProvider.Render, MigrateCommand);

        Assert.Equal("backend", plan.RootDirectory);
        Assert.Equal(("backend/Dockerfile", "/api/health/ready", MigrateCommand), (plan.Config.DockerfilePath, plan.Config.HealthCheckPath, plan.Config.PreDeployCommand));
        Assert.Null(plan.Config.BuildCommand);
    }

    [Fact]
    public void FR144_Without_a_database_host_the_backend_has_no_pre_deploy()
    {
        var composition = AspNetOnRender() with { Deploy = AspNetOnRender().Deploy with { DatabaseHost = null } };

        Assert.Null(DeploymentBuildRules.Derive(composition, DeploymentComponent.Backend, DeploymentProvider.Railway, MigrateCommand).Config.PreDeployCommand);
    }

    [Fact]
    public void FR144_A_Next_js_frontend_on_Vercel_builds_with_the_nextjs_preset()
    {
        var plan = DeploymentBuildRules.Derive(NextOnVercel(), DeploymentComponent.Frontend, DeploymentProvider.Vercel, null);

        Assert.Equal(("nextjs", DeploymentBuildRules.NpmBuild, (string?)null), (plan.Config.Framework, plan.Config.BuildCommand, plan.Config.OutputDirectory));
    }

    [Fact]
    public void FR144_A_React_SPA_on_GitHub_Pages_publishes_dist_from_its_layer_path()
    {
        var composition = PagesAndRailway();

        var plan = DeploymentBuildRules.Derive(composition, DeploymentComponent.Frontend, DeploymentProvider.GithubPages, null);

        Assert.Equal(composition.Frontend!.Path.TrimEnd('/'), plan.RootDirectory);
        Assert.Equal(("vite", DeploymentBuildRules.NpmBuild, "dist"), (plan.Config.Framework, plan.Config.BuildCommand, plan.Config.OutputDirectory));
    }

    [Fact]
    public void FR144_A_component_the_composition_lacks_builds_nothing_from_the_repository_root()
    {
        var plan = DeploymentBuildRules.Derive(AspNetOnRender(), DeploymentComponent.Frontend, DeploymentProvider.Vercel, null);

        Assert.Equal((DeploymentTarget.DefaultRootDirectory, (string?)null), (plan.RootDirectory, plan.Config.Framework));
    }

    // ------------------------------------------------------------------ FR-144 readiness checklist

    [Fact]
    public void FR144_The_checklist_has_stable_item_ids_and_before_verify_platform_items_say_not_checked()
    {
        var items = ReadinessRules.Checklist(Input(DeploymentProvider.Vercel, isChecked: false));

        Assert.Equal(
            [ReadinessRules.Connection, ReadinessRules.GitAccess, ReadinessRules.RequiredEnv, ReadinessRules.BranchControl, ReadinessRules.PreviewEnvironment, ReadinessRules.PlanNotes],
            items.Select(i => i.Id).ToArray());
        Assert.Equal(ReadinessState.Ok, Item(items, ReadinessRules.Connection).State);
        Assert.Equal(ReadinessState.Warning, Item(items, ReadinessRules.GitAccess).State);
        Assert.Contains("Verify", Item(items, ReadinessRules.GitAccess).Message, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Warning, Item(items, ReadinessRules.BranchControl).State);
        Assert.Equal(ReadinessState.Ok, Item(items, ReadinessRules.PlanNotes).State);
    }

    [Fact]
    public void FR144_After_verify_failures_are_errors_with_their_fix_links()
    {
        var items = ReadinessRules.Checklist(Input(DeploymentProvider.Vercel, isChecked: true) with
        {
            Connection = ReadinessConnection.Missing,
            GitAccess = false,
            GitFixUrl = "https://github.com/apps/vercel/installations/new",
            MissingEnv = ["SECRET_KEY"],
            BranchControl = BranchControlState.Missing,
            PreviewReady = false,
        });

        Assert.Equal((ReadinessState.Error, "/account/connections"), (Item(items, ReadinessRules.Connection).State, Item(items, ReadinessRules.Connection).FixUrl));
        Assert.Equal((ReadinessState.Error, "https://github.com/apps/vercel/installations/new"), (Item(items, ReadinessRules.GitAccess).State, Item(items, ReadinessRules.GitAccess).FixUrl));
        Assert.Equal(ReadinessState.Error, Item(items, ReadinessRules.RequiredEnv).State);
        Assert.Contains("SECRET_KEY", Item(items, ReadinessRules.RequiredEnv).Message, StringComparison.Ordinal);
        Assert.Contains("vercel.json", Item(items, ReadinessRules.BranchControl).Message, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Error, Item(items, ReadinessRules.PreviewEnvironment).State);
    }

    [Fact]
    public void FR143_GitHub_Pages_has_no_previews_or_branch_control_and_uses_the_projects_GitHub_access()
    {
        var items = ReadinessRules.Checklist(Input(DeploymentProvider.GithubPages, isChecked: true) with { BranchControl = BranchControlState.NotApplicable, GitAccess = true });

        Assert.Equal(ReadinessState.Ok, Item(items, ReadinessRules.Connection).State);
        Assert.Contains("GitHub App", Item(items, ReadinessRules.Connection).Message, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Na, Item(items, ReadinessRules.BranchControl).State);
        Assert.Equal(ReadinessState.Na, Item(items, ReadinessRules.PreviewEnvironment).State);
        Assert.Equal(ReadinessState.Na, Item(items, ReadinessRules.PlanNotes).State);
    }

    [Fact]
    public void FR144_Verify_moves_a_ready_target_to_attention_on_an_error_and_back_when_clear()
    {
        IReadOnlyList<ReadinessItem> failing = [new(ReadinessRules.Connection, ReadinessState.Ok, "ok"), new(ReadinessRules.GitAccess, ReadinessState.Error, "no")];
        IReadOnlyList<ReadinessItem> clear = [new(ReadinessRules.Connection, ReadinessState.Ok, "ok"), new(ReadinessRules.PlanNotes, ReadinessState.Warning, "w")];

        Assert.Equal((DeploymentTargetStatus.Attention, ReadinessRules.GitAccess), ReadinessRules.StatusAfterVerify(DeploymentTargetStatus.Ready, null, failing));
        Assert.Equal((DeploymentTargetStatus.Ready, (string?)null), ReadinessRules.StatusAfterVerify(DeploymentTargetStatus.Attention, ReadinessRules.GitAccess, clear));
        Assert.Equal((DeploymentTargetStatus.Attention, DeploymentTargetRules.ConnectionMissing),
            ReadinessRules.StatusAfterVerify(DeploymentTargetStatus.Attention, DeploymentTargetRules.ConnectionMissing, clear));
        Assert.Equal((DeploymentTargetStatus.Error, ErrorCodes.PlatformPlanLimit), ReadinessRules.StatusAfterVerify(DeploymentTargetStatus.Error, ErrorCodes.PlatformPlanLimit, clear));
    }

    // ------------------------------------------------------------------ §4.20 providers

    [Fact]
    public void FR144_S4_20_Capabilities_connections_and_real_implementations_per_provider()
    {
        var pages = DeploymentPlatforms.Capabilities(DeploymentProvider.GithubPages);
        Assert.Equal((false, false, false), (pages.Previews, pages.Rollback, pages.ShaPinned));
        Assert.True(DeploymentPlatforms.Capabilities(DeploymentProvider.Vercel).Previews);
        Assert.False(DeploymentPlatforms.Capabilities(DeploymentProvider.Netlify).ShaPinned);

        Assert.False(DeploymentPlatforms.NeedsConnection(DeploymentProvider.GithubPages));
        Assert.Equal(PlatformProvider.Vercel, DeploymentPlatforms.ConnectionProvider(DeploymentProvider.Fake));
        Assert.Equal(PlatformProvider.Railway, DeploymentPlatforms.ConnectionProvider(DeploymentProvider.Railway));

        Assert.True(DeploymentPlatforms.ImplementedForReal(DeploymentProvider.Vercel));
        Assert.True(DeploymentPlatforms.ImplementedForReal(DeploymentProvider.GithubPages));
        Assert.True(DeploymentPlatforms.ImplementedForReal(DeploymentProvider.Netlify));
        Assert.True(DeploymentPlatforms.ImplementedForReal(DeploymentProvider.Cloudflare));
        Assert.False(DeploymentPlatforms.ImplementedForReal(DeploymentProvider.Render));

        foreach (var provider in DeploymentPlatforms.Hosted)
        {
            Assert.Equal(provider, DeploymentPlatforms.FromHostOption(DeploymentPlatforms.HostOption(provider)));
        }

        Assert.Equal("github-pages", DeploymentPlatforms.HostLabel(DeploymentProvider.GithubPages));
    }

    [Theory]
    [InlineData(null, null, null, DeploymentStatus.Queued)]
    [InlineData("queued", null, null, DeploymentStatus.Queued)]
    [InlineData("in_progress", null, null, DeploymentStatus.Building)]
    [InlineData("completed", "failure", null, DeploymentStatus.Error)]
    [InlineData("completed", "cancelled", null, DeploymentStatus.Canceled)]
    [InlineData("completed", "success", null, DeploymentStatus.Building)]
    [InlineData("completed", "success", "syncing_files", DeploymentStatus.Building)]
    [InlineData("completed", "success", "succeed", DeploymentStatus.Ready)]
    [InlineData("completed", "success", "deployment_failed", DeploymentStatus.Error)]
    public void FR143_S4_20_GitHub_Pages_states_map_from_the_workflow_run_and_the_Pages_deployment(string? run, string? conclusion, string? pages, DeploymentStatus expected) =>
        Assert.Equal(expected, GitHubPagesRules.StateOf(run, conclusion, pages));

    [Fact]
    public void FR143_A_Pages_deployment_id_carries_its_commit()
    {
        var id = GitHubPagesRules.DeploymentId("ABCDEF1234");

        Assert.Equal(("pages:abcdef1234", "abcdef1234"), (id, GitHubPagesRules.ShaOf(id)));
        Assert.Null(GitHubPagesRules.ShaOf("dpl_123"));
    }

    private static Result Validate(
        DeploymentComponent component,
        DeploymentProvider provider,
        StackComposition composition,
        GitProvider git = GitProvider.GithubApp,
        bool fakeMode = false,
        Guid? connectionId = null,
        string? createName = null,
        bool? create = null,
        string? link = null) =>
        DeploymentTargetSetupRules.Validate(component, provider, composition, git, fakeMode, connectionId, createName, create ?? link is null, link);

    private static ReadinessInput Input(DeploymentProvider provider, bool isChecked) => new(
        provider,
        isChecked,
        ReadinessConnection.Valid,
        DeploymentPlatforms.NeedsConnection(provider) ? "/account/connections" : null,
        null,
        null,
        null,
        [],
        BranchControlState.Unknown,
        "vercel.json",
        null);

    private static ReadinessItem Item(IReadOnlyList<ReadinessItem> items, string id) => Assert.Single(items, i => i.Id == id);

    private static void AssertField(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith(field + ":", StringComparison.Ordinal));
    }
}
