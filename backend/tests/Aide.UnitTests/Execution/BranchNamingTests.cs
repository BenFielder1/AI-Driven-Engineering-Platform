using Aide.Domain.Execution;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Execution;

/// <summary>FR-232, §4.35: the branch name choice (new branches use tevrin/, recorded branches are kept).</summary>
public sealed class BranchNamingTests
{
    private static readonly Guid ShopId = Guid.Parse("0192f3a4-5b6c-7d8e-9f01-23456789abcd");

    private static readonly SemVer V120 = SemVer.Parse("1.2.0");

    // ---------- §4.35 table, one test per row ----------

    [Fact]
    public void FR232_Row1_A_recorded_aide_version_branch_is_kept() =>
        Assert.Equal("aide/shop/v1.2.0", BranchNaming.Choose("aide/shop/v1.2.0", hasReleaseVersion: true, latestStepBranch: null, "shop", ShopId, V120));

    [Fact]
    public void FR232_Row2_A_version_without_a_recorded_branch_gets_tevrin_slug_v_version() =>
        Assert.Equal("tevrin/shop/v1.2.0", BranchNaming.Choose(null, hasReleaseVersion: true, latestStepBranch: null, "shop", ShopId, V120));

    [Fact]
    public void FR232_Row3_A_project_without_release_versions_keeps_its_latest_step_branch() =>
        Assert.Equal("aide/shop-0192f3a4", BranchNaming.Choose(null, hasReleaseVersion: false, latestStepBranch: "aide/shop-0192f3a4", "shop", ShopId, version: null));

    [Fact]
    public void FR232_Row4_A_project_without_release_versions_or_steps_gets_tevrin_slug_short_id() =>
        Assert.Equal("tevrin/shop-0192f3a4", BranchNaming.Choose(null, hasReleaseVersion: false, latestStepBranch: null, "shop", ShopId, version: null));

    // ---------- FR-232 acceptance ----------

    [Fact]
    public void FR232_A_new_project_branch_is_tevrin_slug_short_id() =>
        Assert.Equal("tevrin/shop-app-0192f3a4", BranchNaming.Choose(null, hasReleaseVersion: false, null, "shop-app", ShopId, version: null));

    [Fact]
    public void FR232_A_new_release_version_1_1_0_gets_tevrin_slug_v1_1_0() =>
        Assert.Equal("tevrin/shop-app/v1.1.0", BranchNaming.Choose(null, hasReleaseVersion: true, null, "shop-app", ShopId, SemVer.Parse("1.1.0")));

    [Fact]
    public void FR232_A_recorded_aide_version_branch_wins_over_any_step_branch() =>
        Assert.Equal("aide/shop-app/v1.0.0", BranchNaming.Choose("aide/shop-app/v1.0.0", hasReleaseVersion: true, "tevrin/other", "shop-app", ShopId, SemVer.Parse("1.0.0")));

    [Fact]
    public void FR232_Step_branches_are_ignored_when_the_project_has_a_release_version() =>
        Assert.Equal("tevrin/shop/v1.2.0", BranchNaming.Choose(null, hasReleaseVersion: true, "aide/shop/v1.1.0", "shop", ShopId, V120));

    [Theory]
    [InlineData("bad..slug")]
    [InlineData("  ")]
    public void FR232_An_unsafe_slug_falls_back_to_tevrin_project_short_id(string slug)
    {
        Assert.Equal("tevrin/project-0192f3a4", BranchNaming.Choose(null, false, null, slug, ShopId, version: null));
        Assert.Equal("tevrin/project-0192f3a4/v1.2.0", BranchNaming.Choose(null, true, null, slug, ShopId, V120));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FR232_A_blank_recorded_or_step_branch_counts_as_none(string blank)
    {
        Assert.Equal("tevrin/shop/v1.2.0", BranchNaming.Choose(blank, true, null, "shop", ShopId, V120));
        Assert.Equal("tevrin/shop-0192f3a4", BranchNaming.Choose(null, false, blank, "shop", ShopId, version: null));
    }

    [Theory]
    [InlineData("aide/shop-0192f3a4", true)]
    [InlineData("aide/shop/v1.0.0", true)]
    [InlineData("tevrin/shop/v1.0.0", false)]
    [InlineData("main", false)]
    [InlineData("Aide/shop", false)]
    [InlineData(null, false)]
    public void FR233_IsLegacy_is_true_only_for_aide_branches(string? branch, bool legacy) =>
        Assert.Equal(legacy, BranchNaming.IsLegacy(branch));
}
