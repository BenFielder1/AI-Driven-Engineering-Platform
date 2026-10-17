using System.Net;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Releases;

/// <summary>
/// M13b contract (FR-64 GitHub part, FR-65, FR-84, FR-85): merge strategy and tag prefix in the Git settings, the
/// merge-readiness stub and the awaiting-merge fields on release attempts.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MergeContractTests(AideApiFactory factory)
{
    private const string PrUrl = "https://github.com/acme/widgets/pull/7";
    private const string ReleaseUrl = "https://github.com/acme/widgets/releases/tag/v0.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-65 / §4.10: Git settings ----------

    [Fact]
    public async Task FR65_Owner_sets_merge_strategy_and_tag_prefix_which_also_applies_to_the_version_in_development()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var defaults = await ProjectApi.ReadAsync<ProjectSettingsDto>(await GetAsync(fixture.Owner.Client, $"{fixture.Path}/settings"));
        var patched = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(
            fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual", tagPrefix = "release/" }));
        var bare = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(
            fixture.Owner.Client, $"{fixture.Path}/settings/git", new { tagPrefix = "" }));

        Assert.Equal((MergeStrategy.AideMerges, "v"), (defaults.Git.MergeStrategy, defaults.Git.TagPrefix));
        Assert.Equal((MergeStrategy.Manual, "release/"), (patched.MergeStrategy, patched.TagPrefix));
        Assert.Equal((MergeStrategy.Manual, ""), (bare.MergeStrategy, bare.TagPrefix));
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == id, Ct);
            var version = await db.ReleaseVersions.AsNoTracking().FirstAsync(v => v.Id == fixture.Project.ReleaseVersionId, Ct);
            Assert.Equal((MergeStrategy.Manual, "", ""), (project.MergeStrategy, project.TagPrefix, version.TagPrefix));
        });
    }

    [Theory]
    [InlineData("this-prefix-is-too-long")]
    [InlineData("-v")]
    [InlineData("v 1")]
    [InlineData("a..b")]
    public async Task FR64_An_invalid_tag_prefix_is_VALIDATION(string tagPrefix)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { tagPrefix }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR65_The_tag_prefix_cannot_change_while_the_version_is_awaiting_merge_and_only_Owners_change_it()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await AwaitingMergeAsync(fixture);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { tagPrefix = "rel-" }),
            HttpStatusCode.Conflict,
            ErrorCodes.Conflict);
        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }),
            HttpStatusCode.Forbidden,
            ErrorCodes.Forbidden);
        var strategy = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(
            fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        Assert.Equal((MergeStrategy.Manual, "v"), (strategy.MergeStrategy, strategy.TagPrefix));
    }

    // ---------- FR-84: merge readiness (contract stub) ----------

    [Fact]
    public async Task FR84_Merge_readiness_is_WRONG_PHASE_outside_review_and_release_and_Local_can_merge_in_them()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var path = $"{fixture.Path}/git/merge-readiness";

        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, path), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        await Problems.AssertAsync(await GetAsync(fixture.Outsider.Client, path), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        using (var anonymous = factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(anonymous, path)).StatusCode);
        }

        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory);
        var review = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, path));
        Assert.Equal((true, true, MergeStrategy.AideMerges), (review.CanMerge, review.WillAutoMerge, review.MergeStrategy));
        Assert.Empty(review.Reasons);
        Assert.Empty(review.Checks);

        // Fresh results are reused, and refresh=true within 30 seconds of the last check is too (no GitHub calls, no event).
        var cached = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, path));
        var throttled = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, $"{path}?refresh=true"));
        Assert.Equal((review.CheckedAt, review.CheckedAt), (cached.CheckedAt, throttled.CheckedAt));

        // After the window refresh=true checks again; an unchanged result writes no new MERGE_READINESS_CHECKED.
        factory.Services.GetRequiredService<MergeReadinessThrottle>().Forget(id);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Release, 6, ApprovalMode.Advisory);
        var refreshed = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, $"{path}?refresh=true"));
        Assert.True(refreshed.CheckedAt > review.CheckedAt);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.MergeReadinessChecked));

        // FR-65: the manual strategy can merge but won't (manual_strategy): a changed result, stored.
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        var manual = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, path));
        Assert.Equal((true, false), (manual.CanMerge, manual.WillAutoMerge));
        Assert.Equal([MergeReadinessReasonCode.ManualStrategy], manual.Reasons.Select(r => r.Code));
        Assert.Equal(2, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.MergeReadinessChecked)).Count);
    }

    // ---------- FR-64 / FR-65: release attempt fields ----------

    [Fact]
    public async Task FR65_An_awaiting_merge_attempt_carries_its_PR_reasons_and_strategy_in_the_history_and_the_state()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var since = await AwaitingMergeAsync(fixture);

        var latest = await ProjectApi.ReadAsync<ReleaseDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/releases/latest"));
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/state"));
        var reasons = new[] { new MergeReadinessReasonDto(MergeReadinessReasonCode.ReviewRequired, "1 approving review is required.") };

        Assert.Equal((ReleaseStatus.AwaitingMerge, PrUrl, (string?)null, MergeStrategy.AideMerges), (latest.Status, latest.PullRequestUrl, latest.GithubReleaseUrl, latest.MergeStrategy));
        Assert.Equal(since, latest.AwaitingMergeSince);
        Assert.Equal(reasons, latest.MergeReasons);
        Assert.Equal(ProjectStatus.AwaitingMerge, state.Status);
        var summary = state.LatestRelease!;
        Assert.Equal((ReleaseStatus.AwaitingMerge, PrUrl, since, MergeStrategy.AideMerges), (summary.Status, summary.PullRequestUrl, summary.AwaitingMergeSince, summary.MergeStrategy));
        Assert.Equal(reasons, summary.MergeReasons);
    }

    [Fact]
    public async Task FR64_A_released_attempt_links_its_GitHub_Release_and_has_no_merge_reasons()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.Releases.Add(new Release
            {
                ProjectId = fixture.Project.Id,
                ReleaseVersionId = fixture.Project.ReleaseVersionId!.Value,
                Checkpoint = 1,
                Tag = "v0.1.0",
                Status = ReleaseStatus.Succeeded,
                PrUrl = PrUrl,
                GithubReleaseUrl = ReleaseUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });

        var releases = await ProjectApi.ReadAsync<List<ReleaseDto>>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/releases"));

        var release = Assert.Single(releases);
        Assert.Equal((ReleaseUrl, (DateTimeOffset?)null, (IReadOnlyList<MergeReadinessReasonDto>?)null), (release.GithubReleaseUrl, release.AwaitingMergeSince, release.MergeReasons));
    }

    /// <summary>Puts the project in <c>release</c> / <c>awaiting_merge</c> with a waiting attempt; returns when it started waiting.</summary>
    private async Task<DateTimeOffset> AwaitingMergeAsync(ProjectFixture fixture)
    {
        var since = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await LifecycleSeed.SetProjectAsync(factory, fixture.Project.Id, Phase.Release, 6, ApprovalMode.Advisory, ProjectStatus.AwaitingMerge);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var versionId = fixture.Project.ReleaseVersionId!.Value;
            await db.ReleaseVersions.Where(v => v.Id == versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, ReleaseVersionStatus.AwaitingMerge).SetProperty(v => v.PullRequestUrl, PrUrl), Ct);
            db.Releases.Add(new Release
            {
                ProjectId = fixture.Project.Id,
                ReleaseVersionId = versionId,
                Checkpoint = 6,
                Tag = "v0.1.0",
                Status = ReleaseStatus.AwaitingMerge,
                PrUrl = PrUrl,
                AwaitingMergeSince = since,
                MergeReasons = """[{"code":"review_required","message":"1 approving review is required."}]""",
                CreatedAt = since,
            });
            await db.SaveChangesAsync(Ct);
        });
        return since;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), Ct);
}
