using Aide.Domain.Common;
using Aide.Domain.Notifications;

namespace Aide.UnitTests.Notifications;

/// <summary>FR-92: the v2 notification types — which events raise them, who gets them, through which channels, and their wording.</summary>
public class Fr92NotificationTypesTests
{
    private static readonly Guid Project = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    [Theory]
    [InlineData(EventTypes.ReleaseAwaitingMerge, NotificationType.ReleaseAwaitingMerge)]
    [InlineData(EventTypes.DeploymentFailed, NotificationType.DeploymentFailed)]
    [InlineData(EventTypes.DeploymentPreviewReady, NotificationType.PreviewReady)]
    [InlineData(EventTypes.ConnectionInvalid, NotificationType.ConnectionInvalid)]
    [InlineData(EventTypes.LlmBindingCleared, NotificationType.LlmBindingCleared)]
    public void FR92_Each_new_event_raises_its_dedicated_notification_type(string eventType, NotificationType expected) =>
        Assert.Equal(expected, NotificationRules.ForEvent(eventType));

    [Theory]
    [InlineData(EventTypes.VersionStarted)]
    [InlineData(EventTypes.CodebaseSummaryCreated)]
    [InlineData(EventTypes.ReleaseMergedExternally)]
    [InlineData(EventTypes.DeploymentCreated)]
    [InlineData(EventTypes.DeploymentUpdated)]
    [InlineData(EventTypes.PresetChanged)]
    public void FR92_Events_without_a_notification_type_raise_none(string eventType) =>
        Assert.Null(NotificationRules.ForEvent(eventType));

    [Fact]
    public void FR92_A_restored_artifact_still_tells_Owners_a_draft_awaits_approval() =>
        Assert.Equal(NotificationType.ArtifactReady, NotificationRules.ForEvent(EventTypes.ArtifactRestored));

    [Theory]
    [InlineData(NotificationType.ReleaseAwaitingMerge, RecipientScope.Owners)]
    [InlineData(NotificationType.DeploymentFailed, RecipientScope.Owners)]
    [InlineData(NotificationType.PreviewReady, RecipientScope.AllMembers)]
    [InlineData(NotificationType.ConnectionInvalid, RecipientScope.ConnectionOwner)]
    [InlineData(NotificationType.LlmBindingCleared, RecipientScope.Owners)]
    public void FR92_Recipients_of_the_new_types(NotificationType type, RecipientScope expected) =>
        Assert.Equal(expected, NotificationRules.Recipients(type));

    [Fact]
    public void FR92_Connection_invalid_is_personal_and_no_project_channel_receives_it()
    {
        Assert.True(NotificationRules.IsPersonal(NotificationType.ConnectionInvalid));
        Assert.Null(NotificationRules.ChannelEvent(NotificationType.ConnectionInvalid));
        Assert.DoesNotContain(NotificationType.ConnectionInvalid, NotificationRules.ChannelTypes);
        Assert.All(NotificationRules.All.Where(t => t != NotificationType.ConnectionInvalid), t => Assert.False(NotificationRules.IsPersonal(t)));
    }

    [Theory]
    [InlineData(NotificationType.ReleaseAwaitingMerge, "release_awaiting_merge")]
    [InlineData(NotificationType.DeploymentFailed, "deployment_failed")]
    [InlineData(NotificationType.PreviewReady, "preview_ready")]
    [InlineData(NotificationType.LlmBindingCleared, "llm_binding_cleared")]
    public void FR92_Project_channels_subscribe_to_the_new_types_by_their_own_wire_value(NotificationType type, string channelEvent)
    {
        Assert.Equal(channelEvent, NotificationRules.ChannelEvent(type));
        Assert.Equal(channelEvent, type.ToWire());
        Assert.Contains(type, NotificationRules.ChannelTypes);
    }

    [Fact]
    public void FR92_Preferences_cover_every_type_and_new_types_default_on()
    {
        Assert.Equal(21, NotificationRules.All.Count);
        foreach (var type in new[] { NotificationType.ReleaseAwaitingMerge, NotificationType.DeploymentFailed, NotificationType.PreviewReady, NotificationType.ConnectionInvalid, NotificationType.LlmBindingCleared, NotificationType.GitBindingCleared })
        {
            Assert.True(NotificationRules.Wants(type, null));
            Assert.True(NotificationRules.Wants(type, [NotificationType.ReviewCompleted, NotificationType.ReleaseFailed]));
            Assert.False(NotificationRules.Wants(type, [type]));
        }
    }

    [Fact]
    public void FR92_Links_open_the_page_that_acts_on_the_notification()
    {
        Assert.Equal($"/projects/{Project}/release", NotificationRules.LinkPath(NotificationType.ReleaseAwaitingMerge, Project));
        Assert.Equal($"/projects/{Project}/release", NotificationRules.LinkPath(NotificationType.DeploymentFailed, Project, deploymentTarget: "production"));
        Assert.Equal($"/projects/{Project}/review", NotificationRules.LinkPath(NotificationType.DeploymentFailed, Project, deploymentTarget: "preview"));
        Assert.Equal($"/projects/{Project}/review", NotificationRules.LinkPath(NotificationType.PreviewReady, Project));
        Assert.Equal("/connections", NotificationRules.LinkPath(NotificationType.ConnectionInvalid, Project));
        Assert.Equal("/connections", NotificationRules.LinkPath(NotificationType.ConnectionInvalid, null));
        Assert.Equal($"/projects/{Project}/settings/llm", NotificationRules.LinkPath(NotificationType.LlmBindingCleared, Project));
    }

    [Fact]
    public void FR92_Release_awaiting_merge_wording_names_the_PR_and_the_reasons()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.ReleaseAwaitingMerge, "Todo API")
        {
            Tag = "v1.2.0",
            PullRequestUrl = "https://github.com/acme/widgets/pull/7",
            MergeReasons = ["1 approving review is required."],
        });

        Assert.Equal("Todo API: release v1.2.0 awaits a merge on GitHub", text.Title);
        Assert.Contains("https://github.com/acme/widgets/pull/7", text.Body, StringComparison.Ordinal);
        Assert.Contains("Reasons: 1 approving review is required.", text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR92_Deployment_failed_wording_names_the_release_version_and_the_error_and_points_to_redeploy()
    {
        var production = NotificationMessages.Compose(new NotificationFacts(NotificationType.DeploymentFailed, "Todo")
        {
            DeploymentEnvironment = "production",
            ReleaseVersion = "1.2.0",
            ErrorCode = "BUILD_FAILED",
        });
        Assert.Equal("Todo: production deployment 1.2.0 failed", production.Title);
        Assert.Contains("(BUILD_FAILED)", production.Body, StringComparison.Ordinal);
        Assert.Contains("redeploy from the Release page", production.Body, StringComparison.Ordinal);

        var preview = NotificationMessages.Compose(new NotificationFacts(NotificationType.DeploymentFailed, "Todo") { DeploymentEnvironment = "preview" });
        Assert.Contains("redeploy from the Review page", preview.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR92_Preview_ready_wording_carries_the_url()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.PreviewReady, "Todo")
        {
            ReleaseVersion = "1.3.0",
            DeploymentUrl = "https://todo-abc.vercel.app",
        });
        Assert.Equal("Todo: preview 1.3.0 is ready", text.Title);
        Assert.Contains("https://todo-abc.vercel.app", text.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic", "Your LLM (Anthropic) connection is invalid")]
    [InlineData("openai", "Your LLM (OpenAI) connection is invalid")]
    [InlineData("github", "Your GitHub connection is invalid")]
    [InlineData("vercel", "Your Vercel connection is invalid")]
    [InlineData(null, "Your LLM connection is invalid")]
    public void FR92_Connection_invalid_wording_says_which_connection(string? provider, string title)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.ConnectionInvalid, "Todo") { Provider = provider });
        Assert.Equal(title, text.Title);
        Assert.Contains("while it was used for Todo", text.Body, StringComparison.Ordinal);
        Assert.Contains("Connections page", text.Body, StringComparison.Ordinal);

        var outside = NotificationMessages.Compose(new NotificationFacts(NotificationType.ConnectionInvalid, string.Empty) { Provider = provider });
        Assert.DoesNotContain("while it was used", outside.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("owner_role_lost", "is no longer an Owner")]
    [InlineData("member_removed", "left the project")]
    [InlineData("connection_deleted", "deleted the connection")]
    public void FR92_Llm_binding_cleared_wording_gives_the_reason(string reason, string expected)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.LlmBindingCleared, "Todo") { Reason = reason });
        Assert.Equal("Todo: LLM connection unbound", text.Title);
        Assert.Contains(expected, text.Body, StringComparison.Ordinal);
        Assert.Contains("LLM settings", text.Body, StringComparison.Ordinal);
    }
}
