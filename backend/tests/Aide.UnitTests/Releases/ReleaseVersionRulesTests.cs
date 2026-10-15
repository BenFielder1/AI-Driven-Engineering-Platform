using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>§4.10, FR-60, FR-61, FR-62, FR-66: semver, release version statuses and starting a new version.</summary>
public class ReleaseVersionRulesTests
{
    private static readonly ReleaseVersionRow[] Released110 = [new("1.0.0", ReleaseVersionStatus.Released), new("1.1.0", ReleaseVersionStatus.Released)];

    // ---- SemVer ----

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("V1.2.0", "1.2.0")]
    [InlineData("  v1.2.0 ", "1.2.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("2.0.0-beta.1", "2.0.0-beta.1")]
    [InlineData("1.2.0-rc.1", "1.2.0-rc.1")]
    [InlineData("1.0.0-alpha-1.x.0", "1.0.0-alpha-1.x.0")]
    [InlineData("10.20.30", "10.20.30")]
    public void S4_10_Semver_parses_and_normalises(string input, string expected)
    {
        Assert.True(SemVer.TryParse(input, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.0.0")]
    [InlineData("01.2.0")]
    [InlineData("1.02.0")]
    [InlineData("1.2.00")]
    [InlineData("1.2.0+build.5")]
    [InlineData("1.2.0-")]
    [InlineData("1.2.0-01")]
    [InlineData("1.2.0-rc..1")]
    [InlineData("vv1.2.0")]
    [InlineData("-1.2.0")]
    [InlineData("1.2.0 beta")]
    [InlineData("99999999999999999999.0.0")]
    public void S4_10_Invalid_semver_is_refused(string? input) => Assert.False(SemVer.TryParse(input, out _));

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.1.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.10.0", "2.0.0")]
    public void S4_10_Semver_precedence_follows_semver_2(string lower, string higher)
    {
        var a = SemVer.Parse(lower);
        var b = SemVer.Parse(higher);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void S4_10_Equal_versions_compare_equal_whatever_the_input_form()
    {
        Assert.Equal(SemVer.Parse("v1.2.0"), SemVer.Parse("1.2.0"));
        Assert.Equal(0, SemVer.Parse("1.2.0-rc.1").CompareTo(SemVer.Parse("v1.2.0-rc.1")));
    }

    [Theory]
    [InlineData("1.1.0", "1.2.0")]
    [InlineData("1.1.3", "1.2.0")]
    [InlineData("2.0.0-beta.1", "2.1.0")]
    [InlineData("0.1.0", "0.2.0")]
    public void FR60_Next_minor_is_the_suggestion(string released, string next) =>
        Assert.Equal(next, SemVer.Parse(released).NextMinor().ToString());

    // ---- §4.10 worked examples: released 1.1.0 ----

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.1.1", true)]
    [InlineData("2.0.0-beta.1", true)]
    [InlineData("1.1.0", false)]
    [InlineData("1.0.9", false)]
    [InlineData("1.1.0-rc.1", false)]
    public void S4_10_Worked_example_released_1_1_0(string requested, bool allowed)
    {
        var result = ReleaseVersionRules.CanStart(ProjectStatus.Released, Released110, requested);

        if (allowed)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(requested, result.Value.ToString());
        }
        else
        {
            Assert.Equal(ErrorCodes.VersionNotGreater, result.Error!.Code);
        }
    }

    [Fact]
    public void S4_10_Worked_example_1_2_0_rc_1_then_1_2_0_are_separate_versions()
    {
        ReleaseVersionRow[] afterRc = [.. Released110, new("1.2.0-rc.1", ReleaseVersionStatus.Released)];

        Assert.True(ReleaseVersionRules.CanStart(ProjectStatus.Released, Released110, "1.2.0-rc.1").IsSuccess);
        Assert.Equal("1.2.0-rc.1", ReleaseVersionRules.ReleasedVersion(afterRc)!.ToString());
        Assert.True(ReleaseVersionRules.CanStart(ProjectStatus.Released, afterRc, "1.2.0").IsSuccess);
    }

    [Fact]
    public void S4_10_Worked_example_01_2_0_is_invalid() =>
        Assert.Equal(ErrorCodes.VersionInvalid, ReleaseVersionRules.CanStart(ProjectStatus.Released, Released110, "01.2.0").Error!.Code);

    [Fact]
    public void FR61_Released_1_1_0_refuses_1_0_5_and_normalises_v1_2_0()
    {
        Assert.Equal(ErrorCodes.VersionNotGreater, ReleaseVersionRules.CanStart(ProjectStatus.Released, Released110, "1.0.5").Error!.Code);
        Assert.Equal("1.2.0", ReleaseVersionRules.CanStart(ProjectStatus.Released, Released110, "v1.2.0").Value.ToString());
    }

    [Fact]
    public void FR61_Starting_while_a_version_is_in_development_is_VERSION_IN_PROGRESS()
    {
        ReleaseVersionRow[] developing = [.. Released110, new("1.2.0", ReleaseVersionStatus.InDevelopment)];

        Assert.Equal(ErrorCodes.VersionInProgress, ReleaseVersionRules.CanStart(ProjectStatus.Active, developing, "1.3.0").Error!.Code);
    }

    [Fact]
    public void FR60_A_release_awaiting_merge_is_RELEASE_NOT_MERGED()
    {
        ReleaseVersionRow[] awaiting = [new("1.0.0", ReleaseVersionStatus.Released), new("1.1.0", ReleaseVersionStatus.AwaitingMerge)];

        Assert.Equal(ErrorCodes.ReleaseNotMerged, ReleaseVersionRules.CanStart(ProjectStatus.Releasing, awaiting, "1.2.0").Error!.Code);
    }

    [Theory]
    [InlineData(ProjectStatus.Active)]
    [InlineData(ProjectStatus.Building)]
    [InlineData(ProjectStatus.Reviewing)]
    [InlineData(ProjectStatus.Releasing)]
    [InlineData(ProjectStatus.RolledBack)]
    public void FR60_A_project_that_is_not_released_is_NOT_RELEASED(ProjectStatus status) =>
        Assert.Equal(ErrorCodes.NotReleased, ReleaseVersionRules.CanStart(status, Released110, "1.2.0").Error!.Code);

    [Fact]
    public void FR61_Checks_run_in_order_invalid_then_state_then_greater()
    {
        ReleaseVersionRow[] developing = [new("1.2.0", ReleaseVersionStatus.InDevelopment)];

        Assert.Equal(ErrorCodes.VersionInvalid, ReleaseVersionRules.CanStart(ProjectStatus.Active, developing, "nope").Error!.Code);
        Assert.Equal(ErrorCodes.VersionInProgress, ReleaseVersionRules.CanStart(ProjectStatus.Active, developing, "0.0.1").Error!.Code);
        Assert.Equal(ErrorCodes.NotReleased, ReleaseVersionRules.CanStart(ProjectStatus.Active, Released110, "0.0.1").Error!.Code);
    }

    [Fact]
    public void FR61_A_version_that_already_exists_is_not_greater()
    {
        // An imported baseline below a released one: the highest wins, and the existing text is refused anyway.
        ReleaseVersionRow[] versions = [new("2.0.0", ReleaseVersionStatus.Imported), new("1.0.0", ReleaseVersionStatus.Released)];

        Assert.Equal(ErrorCodes.VersionNotGreater, ReleaseVersionRules.CanStart(ProjectStatus.Released, versions, "2.0.0").Error!.Code);
        Assert.True(ReleaseVersionRules.CanStart(ProjectStatus.Released, versions, "2.0.1").IsSuccess);
    }

    [Fact]
    public void FR66_A_first_version_has_nothing_released_and_suggests_0_1_0()
    {
        ReleaseVersionRow[] first = [new("1.0.0", ReleaseVersionStatus.InDevelopment)];

        Assert.Null(ReleaseVersionRules.ReleasedVersion(first));
        Assert.Equal("1.0.0", ReleaseVersionRules.DevelopmentVersion(first)!.Version);
        Assert.Equal("0.1.0", ReleaseVersionRules.SuggestNext([]).ToString());
        Assert.Equal("1.2.0", ReleaseVersionRules.SuggestNext(Released110).ToString());
    }

    // ---- Released and development versions ----

    [Fact]
    public void S4_10_Released_version_is_the_highest_released_or_imported_by_semver_not_insertion()
    {
        // v1 migration: versions 0.1.0, 0.2.0 inserted in any order; 0.10.0 sorts above 0.9.0.
        ReleaseVersionRow[] versions =
        [
            new("0.10.0", ReleaseVersionStatus.Released),
            new("0.9.0", ReleaseVersionStatus.Released),
            new("0.11.0", ReleaseVersionStatus.InDevelopment),
            new("not-semver", ReleaseVersionStatus.Released),
        ];

        Assert.Equal("0.10.0", ReleaseVersionRules.ReleasedVersion(versions)!.ToString());
        Assert.Equal("0.11.0", ReleaseVersionRules.DevelopmentVersion(versions)!.Version);
        Assert.Equal("3.0.0", ReleaseVersionRules.ReleasedVersion([new("2.4.0", ReleaseVersionStatus.Released), new("3.0.0", ReleaseVersionStatus.Imported)])!.ToString());
    }

    [Fact]
    public void S4_10_Awaiting_merge_counts_as_the_development_version_not_released()
    {
        ReleaseVersionRow[] versions = [new("1.0.0", ReleaseVersionStatus.Released), new("1.1.0", ReleaseVersionStatus.AwaitingMerge)];

        Assert.Equal("1.0.0", ReleaseVersionRules.ReleasedVersion(versions)!.ToString());
        Assert.Equal("1.1.0", ReleaseVersionRules.DevelopmentVersion(versions)!.Version);
    }

    public static TheoryData<ReleaseVersionStatus, ReleaseVersionStatus> AllStatusPairs()
    {
        var data = new TheoryData<ReleaseVersionStatus, ReleaseVersionStatus>();
        foreach (var from in Enum.GetValues<ReleaseVersionStatus>())
        {
            foreach (var to in Enum.GetValues<ReleaseVersionStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllStatusPairs))]
    public void S4_10_Release_version_status_matrix(ReleaseVersionStatus from, ReleaseVersionStatus to)
    {
        var allowed = (from, to) is (ReleaseVersionStatus.InDevelopment, ReleaseVersionStatus.AwaitingMerge)
            or (ReleaseVersionStatus.InDevelopment, ReleaseVersionStatus.Released)
            or (ReleaseVersionStatus.AwaitingMerge, ReleaseVersionStatus.Released);

        Assert.Equal(allowed, ReleaseVersionRules.CanTransition(from, to));
    }

    [Fact]
    public void S4_10_Release_version_status_wire_values() =>
        Assert.Equal(["in_development", "awaiting_merge", "released", "imported"], EnumStrings.Values<ReleaseVersionStatus>());

    // ---- Tags and branches ----

    [Theory]
    [InlineData("v", "v1.2.0")]
    [InlineData("", "1.2.0")]
    [InlineData("release/", "release/1.2.0")]
    [InlineData("my-app_v", "my-app_v1.2.0")]
    [InlineData("A.b-c_d/e", "A.b-c_d/e1.2.0")]
    public void S4_10_Tag_is_prefix_plus_version(string prefix, string tag)
    {
        Assert.True(ReleaseVersionRules.IsValidTagPrefix(prefix));
        Assert.Equal(tag, ReleaseVersionRules.Tag(prefix, SemVer.Parse("1.2.0")));
        Assert.True(ReleaseRules.IsReleaseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v ")]
    [InlineData("v@")]
    [InlineData("-v")]
    [InlineData(".v")]
    [InlineData("/v")]
    [InlineData("a..b")]
    [InlineData("a//")]
    [InlineData("a/.b")]
    [InlineData("x.lock/")]
    [InlineData("abcdefghijklmnopqrstu")]
    public void S4_10_Invalid_tag_prefixes_are_refused(string? prefix)
    {
        Assert.False(ReleaseVersionRules.IsValidTagPrefix(prefix));
        if (prefix is not null)
        {
            Assert.Throws<ArgumentException>(() => ReleaseVersionRules.Tag(prefix, SemVer.Parse("1.2.0")));
        }
    }

    [Fact]
    public void FR60_The_version_branch_is_aide_slug_v_version()
    {
        var id = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        Assert.Equal("aide/todo-api-4a5b0c/v1.2.0", ReleaseVersionRules.BranchName("todo-api-4a5b0c", id, SemVer.Parse("1.2.0")));
        Assert.Equal("aide/project-0199a1b2/v1.2.0-rc.1", ReleaseVersionRules.BranchName("bad..slug", id, SemVer.Parse("1.2.0-rc.1")));
    }
}
