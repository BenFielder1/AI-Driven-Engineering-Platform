using Aide.Domain.Agents;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-64, §4.14: AIDE's GitHub Release notes.</summary>
public class ReleaseNotesTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void FR64_S4_14_Notes_have_the_version_changes_plan_steps_with_commit_links_review_score_and_AIDE_link()
    {
        var notes = ReleaseNotes.Build(new ReleaseNotesInput(
            "Todo API",
            "1.2.0",
            "v1.2.0",
            "- Add CSV export (FR-12) [new]",
            [new ReleaseNoteStep(2, "Export endpoint", null), new ReleaseNoteStep(1, "CSV writer", Sha1)],
            "https://github.com/acme/widgets",
            0.93m,
            2,
            "http://localhost:8080/projects/p1/release"));

        Assert.Equal(
            """
            ## Todo API 1.2.0

            ### Changes in this version

            - Add CSV export (FR-12) [new]

            ### Plan steps

            1. CSV writer ([`0123456`](https://github.com/acme/widgets/commit/0123456789abcdef0123456789abcdef01234567))
            2. Export endpoint

            ### Review

            Compliance score **0.930** (review cycle 2).

            ---
            Released as `v1.2.0` with [AIDE](http://localhost:8080/projects/p1/release).

            """.ReplaceLineEndings("\n"),
            notes);
        Assert.Equal("1.2.0", ReleaseNotes.Name("1.2.0"));
    }

    [Fact]
    public void FR64_A_first_version_without_changes_section_steps_or_score_says_so()
    {
        var notes = ReleaseNotes.Build(new ReleaseNotesInput("P", "0.1.0", "v0.1.0", null, [], null, null, null, "http://x/projects/1/release"));

        Assert.Contains("no \"Changes in this version\" section", notes, StringComparison.Ordinal);
        Assert.Contains("_No plan steps were recorded._", notes, StringComparison.Ordinal);
        Assert.Contains("_No review score was recorded._", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void FR64_The_changes_section_is_extracted_from_the_spec_up_to_the_next_heading_of_its_level()
    {
        const string spec = """
            # Spec
            ## 1. Overview
            Text.
            ## 12. Changes in this version
            - Added export.
            ### Detail
            More.
            ## Open Questions
            None.
            """;

        Assert.Equal("- Added export.\n### Detail\nMore.", SpecSections.Extract(spec.ReplaceLineEndings("\n"), SpecSections.ChangesInThisVersion));
        Assert.Null(SpecSections.Extract("# Spec\n## Overview\nx", SpecSections.ChangesInThisVersion));
    }
}

/// <summary>FR-65: the awaiting-merge notification (release_awaiting_merge, FR-92) names the PR and the reasons.</summary>
public class AwaitingMergeNotificationTests
{
    [Fact]
    public void FR65_Owners_are_told_the_release_awaits_a_merge_with_the_PR_link_and_reasons()
    {
        var text = Aide.Domain.Notifications.NotificationMessages.Compose(new Aide.Domain.Notifications.NotificationFacts(Aide.Domain.Notifications.NotificationType.ReleaseAwaitingMerge, "Todo API")
        {
            Tag = "v1.2.0",
            PullRequestUrl = "https://github.com/acme/widgets/pull/7",
            MergeReasons = ["1 approving review is required."],
        });

        Assert.Equal("Todo API: release v1.2.0 awaits a merge on GitHub", text.Title);
        Assert.Contains("https://github.com/acme/widgets/pull/7", text.Body, StringComparison.Ordinal);
        Assert.Contains("Reasons: 1 approving review is required.", text.Body, StringComparison.Ordinal);
    }
}
