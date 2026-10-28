using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-117 (the stack lock, stack changes, pinning and <c>stack_packs</c>), FR-119 (layer paths) and §4.17 R5 (the VCS layer
/// is the repository's host): the pure rules of <see cref="StackPinning"/> and <see cref="StackPaths"/>.
/// </summary>
public sealed class StackPinningTests
{
    private static readonly DateTimeOffset Approved = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>§4.17 worked example 1 on GitHub: React + Vite on Netlify, ASP.NET on Render, Render Postgres.</summary>
    private static StackComposition Example1(string backendPath = "backend")
    {
        var selection = Sel("fe-react-vite", vcs: "vcs-github", data: "data-efcore-postgres", frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres");
        return Valid(selection with { Backend = new StackComponentPick("be-aspnet", backendPath) });
    }

    // ── STACK_LOCKED ─────────────────────────────────────────────────────

    [Fact]
    public void FR117_The_stack_can_change_until_the_versions_first_implementation_plan_is_locked()
    {
        Assert.Equal(StackLock.Unlocked, StackPinning.Lock(ReleaseVersionStatus.InDevelopment, planWasLocked: false));
        Assert.True(StackPinning.CanChange(ReleaseVersionStatus.InDevelopment, planWasLocked: false).IsSuccess);

        Assert.Equal(new StackLock(true, StackLockReason.PlanLocked), StackPinning.Lock(ReleaseVersionStatus.InDevelopment, planWasLocked: true));
        Assert.Equal(new StackLock(true, StackLockReason.NotInDevelopment), StackPinning.Lock(ReleaseVersionStatus.Released, planWasLocked: false));
        Assert.Equal(new StackLock(true, StackLockReason.NotInDevelopment), StackPinning.Lock(ReleaseVersionStatus.AwaitingMerge, planWasLocked: false));
        Assert.Equal(new StackLock(true, StackLockReason.NotInDevelopment), StackPinning.Lock(ReleaseVersionStatus.Imported, planWasLocked: false));
        Assert.Equal(new StackLock(true, StackLockReason.NotInDevelopment), StackPinning.Lock(null, planWasLocked: false));
    }

    [Fact]
    public void FR117_Given_a_locked_implementation_plan_when_an_Owner_changes_the_backend_then_STACK_LOCKED()
    {
        var locked = StackPinning.CanChange(ReleaseVersionStatus.InDevelopment, StackPinning.PlanWasLocked([(ArtifactStatus.Locked, Approved)]));

        Assert.Equal(ErrorCodes.StackLocked, locked.Error!.Code);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.StackLocked));
        Assert.Equal(ErrorCodes.StackLocked, StackPinning.CanChange(ReleaseVersionStatus.Released, planWasLocked: false).Error!.Code);
    }

    [Fact]
    public void FR117_A_draft_plan_doesnt_lock_the_stack_but_an_approved_one_does_even_after_invalidation()
    {
        Assert.False(StackPinning.PlanWasLocked([]));
        Assert.False(StackPinning.PlanWasLocked([(ArtifactStatus.Draft, null), (ArtifactStatus.Rejected, null)]));
        Assert.True(StackPinning.PlanWasLocked([(ArtifactStatus.Draft, null), (ArtifactStatus.Locked, Approved)]));
        Assert.True(StackPinning.PlanWasLocked([(ArtifactStatus.Invalidated, Approved)]));
        Assert.True(StackPinning.PlanWasLocked([(ArtifactStatus.Superseded, Approved)]));
    }

    // ── Stack change ─────────────────────────────────────────────────────

    [Fact]
    public void FR117_Layers_changed_names_each_catalog_layer_whose_choice_differs()
    {
        var before = Example1();

        Assert.Empty(StackPinning.LayersChanged(before, Example1()));

        var express = Valid(StackPinning.ToSelection(before) with { Backend = new StackComponentPick("be-express"), Data = new StackDataPick("data-prisma") });
        Assert.Equal([StackLayers.Backend, StackLayers.Data], StackPinning.LayersChanged(before, express));

        // A moved layer is a change too (FR-119: its path is part of the layer).
        Assert.Equal([StackLayers.Backend], StackPinning.LayersChanged(before, Example1("api")));

        var vercel = Valid(StackPinning.ToSelection(before) with { Deploy = new StackDeployPick("deploy-vercel", "deploy-render", "render-postgres", "compose"), OriginModel = "cross-origin" });
        Assert.Equal([StackLayers.Deploy], StackPinning.LayersChanged(before, vercel));

        // From no stack (general, or a legacy preset): every layer that isn't None.
        var apiOnly = Valid(Sel("none", "be-fastapi", vcs: "vcs-github", local: "none"));
        Assert.Equal([StackLayers.Backend, StackLayers.Vcs, StackLayers.Deploy], StackPinning.LayersChanged(null, apiOnly));
        Assert.Equal(StackLayers.CatalogLayers, StackPinning.LayersChanged(null, before));
        Assert.Empty(StackPinning.LayersChanged(null, null));
    }

    [Fact]
    public void FR117_Changing_a_layer_is_a_stack_change_only_when_the_base_commit_has_code()
    {
        var before = Example1();
        var after = Valid(StackPinning.ToSelection(before) with { Data = new StackDataPick("data-efcore-sqlserver"), Deploy = new StackDeployPick("deploy-netlify", "deploy-render", "external", "compose") });

        var iteration = StackPinning.Change(baseHasCode: true, before, after);
        Assert.Equal([StackLayers.Data, StackLayers.Deploy], iteration.LayersChanged);
        Assert.True(iteration.IsStackChange);

        // An empty repository (a first version from scratch) has nothing to migrate.
        var scratch = StackPinning.Change(baseHasCode: false, before, after);
        Assert.Equal([StackLayers.Data, StackLayers.Deploy], scratch.LayersChanged);
        Assert.False(scratch.IsStackChange);

        Assert.False(StackPinning.Change(baseHasCode: true, before, Example1()).IsStackChange);

        // No baseline composition (legacy preset or no stack): nothing is compared.
        Assert.False(StackPinning.Change(baseHasCode: true, null, after).IsStackChange);
        Assert.False(StackChange.None.IsStackChange);
    }

    // ── R5: VCS ──────────────────────────────────────────────────────────

    [Fact]
    public void FR117_R5_The_VCS_layer_is_the_repository_host()
    {
        Assert.Equal("vcs-github", StackPinning.RepoVcs(GitProvider.GithubApp));
        Assert.Equal("vcs-github", StackPinning.RepoVcs(GitProvider.GithubPat));
        Assert.Null(StackPinning.RepoVcs(GitProvider.Local));
    }

    [Fact]
    public void FR117_R5_A_left_out_VCS_is_set_from_the_repository_and_another_host_is_VCS_MISMATCH()
    {
        var noVcs = Sel("fe-vue", "be-express");

        Assert.Equal("vcs-github", StackPinning.FixVcs(noVcs, "vcs-github").Value.Vcs!.Pack);
        Assert.Equal("vcs-github", StackPinning.FixVcs(noVcs, repoVcs: null).Value.Vcs!.Pack);
        Assert.Equal("vcs-github", StackPinning.FixVcs(Sel("fe-vue", vcs: "vcs-github"), "vcs-github").Value.Vcs!.Pack);

        var mismatch = StackPinning.FixVcs(Sel("fe-vue", vcs: "vcs-gitlab"), "vcs-github");
        Assert.Equal(ErrorCodes.VcsMismatch, mismatch.Error!.Code);
        Assert.Equal(400, ErrorCodes.StatusCodeFor(ErrorCodes.VcsMismatch));
        Assert.StartsWith("vcs.pack: must be 'vcs-github'", Assert.Single(mismatch.Error.Details), StringComparison.Ordinal);

        // A Local repository has no host: any VCS may be chosen.
        Assert.Equal("vcs-bitbucket", StackPinning.FixVcs(Sel("fe-vue", vcs: "vcs-bitbucket"), repoVcs: null).Value.Vcs!.Pack);
    }

    // ── Pinning ──────────────────────────────────────────────────────────

    [Fact]
    public void FR117_The_pinned_composition_round_trips_through_its_stored_JSON_and_revalidates_unchanged()
    {
        var composition = Example1();

        var json = StackCompositionJson.Serialize(composition);
        var stored = StackCompositionJson.Deserialize(json);

        Assert.NotNull(stored);
        Assert.Equal(json, StackCompositionJson.Serialize(stored));
        Assert.Contains("\"catalogVersion\":\"1.1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frontend\":{\"pack\":\"fe-react-vite\",\"path\":\"frontend\"}", json, StringComparison.Ordinal);
        Assert.Equal(json, StackCompositionJson.Serialize(Valid(StackPinning.ToSelection(stored))));

        // Implicit defaults never change a re-pinned composition: None stays None.
        var bare = Valid(Sel("fe-vanilla", "none", "none", "vcs-github", "deploy-github-pages", local: "none"));
        Assert.Equal(StackCompositionJson.Serialize(bare), StackCompositionJson.Serialize(Valid(StackPinning.ToSelection(bare))));

        Assert.Null(StackCompositionJson.Deserialize(null));
        Assert.Null(StackCompositionJson.Deserialize("  "));
        Assert.Null(StackCompositionJson.Deserialize("{\"not\": \"a composition\"}"));
    }

    [Fact]
    public void FR117_Stack_packs_are_the_pack_ids_of_the_composition_in_choice_order()
    {
        Assert.Equal(
            ["fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", "deploy-netlify", "deploy-render", "local-docker-desktop"],
            StackPinning.PackIds(Example1(), Catalog));

        // None and External have no pack; Supabase BaaS is one pack for the data layer and its host.
        var baas = Valid(Sel("fe-vue", "none", "data-supabase", "vcs-github", "deploy-netlify", local: "none"));
        Assert.Equal(["fe-vue", "data-supabase", "vcs-github", "deploy-netlify"], StackPinning.PackIds(baas, Catalog));
        Assert.Empty(StackPinning.PackIds(null, Catalog));
    }

    // ── FR-119: layer paths ──────────────────────────────────────────────

    [Fact]
    public void FR119_Given_a_Nextjs_full_stack_app_at_the_repo_root_then_its_path_is_dot()
    {
        var nextjs = Valid(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-nextjs", "./"), Backend = new StackComponentPick("none") });

        Assert.Equal(new StackComponent("fe-nextjs", "."), nextjs.Frontend);
        Assert.Null(nextjs.Backend);
        Assert.Equal("full-stack", nextjs.Derived.NextjsMode);
    }

    [Fact]
    public void FR119_Paths_default_to_frontend_and_backend_and_are_normalised()
    {
        var defaults = Example1();
        Assert.Equal(("frontend", "backend"), (defaults.Frontend!.Path, defaults.Backend!.Path));

        var custom = Valid(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-vue", "apps\\web/"), Backend = new StackComponentPick("be-express", "./api") });
        Assert.Equal(("apps/web", "api"), (custom.Frontend!.Path, custom.Backend!.Path));

        // A frontend-only repo and an API-only repo may use the root.
        Assert.Equal(".", Valid(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-vanilla", ".") }).Frontend!.Path);
        Assert.Equal(".", Valid(Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("none"), Backend = new StackComponentPick("be-fastapi", ".") }).Backend!.Path);
    }

    [Theory]
    [InlineData("../web", "backend", "frontend.path")]
    [InlineData("/srv/web", "backend", "frontend.path")]
    [InlineData("web", "web/", "backend.path")]
    [InlineData(".", "backend", "frontend.path")]
    [InlineData("frontend", ".", "backend.path")]
    [InlineData("web", "web/api", "backend.path")]
    [InlineData("services/web", "services", "frontend.path")]
    public void FR119_Paths_may_not_escape_the_repo_or_overlap_when_both_layers_exist(string frontend, string backend, string field)
    {
        var selection = Sel(vcs: "vcs-github") with { Frontend = new StackComponentPick("fe-vue", frontend), Backend = new StackComponentPick("be-express", backend) };

        var validation = StackCompatibility.Validate(selection, Catalog);

        Assert.Equal(field, Assert.Single(validation.InputErrors).Field);
        Assert.Equal(ErrorCodes.Validation, StackCompatibility.Evaluate(selection, Catalog).Error!.Code);
    }

    [Fact]
    public void FR119_Sibling_paths_with_a_shared_prefix_are_fine()
    {
        Assert.Empty(StackPaths.Check("web", "webapi"));
        Assert.Empty(StackPaths.Check("apps/web", "apps/api"));
        Assert.Empty(StackPaths.Check(".", null));
        Assert.Empty(StackPaths.Check(null, "."));
    }
}
