using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Domain.Trackers;

namespace Aide.UnitTests.Notifications;

/// <summary>
/// FR-193: the v5 notification types (<c>stack_profile_ready</c>, <c>tracker_import_failed</c>): which events raise them, who gets
/// them, their links and wording; and FR-165: the 14-day expiry warning of Jira and Linear connections.
/// </summary>
public class Fr193NotificationTypesTests
{
    private static readonly Guid Project = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EventTypes.ArtifactCreated, null, "stack_profile")]
    [InlineData(EventTypes.ArtifactRestored, null, "stack_profile")]
    [InlineData(EventTypes.RunFailed, "stack_analyst", null)]
    public void FR193_A_stack_profile_draft_or_a_failed_Stack_Analyst_raises_stack_profile_ready(string eventType, string? agent, string? artifactType) =>
        Assert.Equal(NotificationType.StackProfileReady, NotificationRules.ForEvent(eventType, agent, artifactType));

    [Fact]
    public void FR193_Given_a_draft_profile_Owners_get_one_notification_not_also_artifact_ready()
    {
        // The agent's draft writes ARTIFACT_CREATED and STACK_PROFILE_CREATED in one save: only the first notifies.
        Assert.Equal(NotificationType.StackProfileReady, NotificationRules.ForEvent(EventTypes.ArtifactCreated, null, "stack_profile"));
        Assert.Null(NotificationRules.ForEvent(EventTypes.StackProfileCreated, null, null));
        Assert.Equal(RecipientScope.Owners, NotificationRules.Recipients(NotificationType.StackProfileReady));
    }

    [Fact]
    public void FR193_Other_drafts_and_runs_keep_their_notifications()
    {
        Assert.Equal(NotificationType.ArtifactReady, NotificationRules.ForEvent(EventTypes.ArtifactCreated, null, "technical_specification"));
        Assert.Equal(NotificationType.RunFailed, NotificationRules.ForEvent(EventTypes.RunFailed, "spec_generator", null));
        Assert.Equal(NotificationType.RunFailed, NotificationRules.ForEvent(EventTypes.RunFailed));
        Assert.Equal(NotificationType.ArtifactReady, NotificationRules.ForEvent(EventTypes.ArtifactCreated));
    }

    [Fact]
    public void FR193_A_failed_import_tells_the_Owners_and_the_importer()
    {
        Assert.Equal(NotificationType.TrackerImportFailed, NotificationRules.ForEvent(EventTypes.TrackerImportFailed));
        Assert.Equal(RecipientScope.OwnersAndUser, NotificationRules.Recipients(NotificationType.TrackerImportFailed));
        Assert.Equal("importedBy", NotificationRules.RecipientField(NotificationType.TrackerImportFailed));
        Assert.False(NotificationRules.IsPersonal(NotificationType.TrackerImportFailed));
    }

    [Theory]
    [InlineData(NotificationType.StackProfileReady, "stack_profile_ready")]
    [InlineData(NotificationType.TrackerImportFailed, "tracker_import_failed")]
    public void FR193_Project_channels_subscribe_to_the_new_types_by_their_wire_value(NotificationType type, string wire)
    {
        Assert.Equal(wire, type.ToWire());
        Assert.Equal(wire, NotificationRules.ChannelEvent(type));
        Assert.Contains(type, NotificationRules.ChannelTypes);
        Assert.True(NotificationRules.ChannelReceives(type, [wire]));
        Assert.False(NotificationRules.ChannelReceives(type, ["artifact_ready", "run_failed"]));
    }

    [Theory]
    [InlineData(EventTypes.TrackerImportCompleted)]
    [InlineData(EventTypes.SourceItemsRemoved)]
    [InlineData(EventTypes.StackProfileCreated)]
    [InlineData(EventTypes.StackProfileApproved)]
    [InlineData(EventTypes.StackProfileRejected)]
    [InlineData(EventTypes.DeploymentGroupConfigured)]
    [InlineData(EventTypes.DeploymentGroupFailed)]
    [InlineData(EventTypes.CommitCreditChanged)]
    public void FR193_The_other_new_events_are_recorded_without_a_notification(string eventType) =>
        Assert.Null(NotificationRules.ForEvent(eventType));

    [Theory]
    [InlineData(EventTypes.TrackerImportCompleted, EventCategory.Artifact)]
    [InlineData(EventTypes.TrackerImportFailed, EventCategory.Artifact)]
    [InlineData(EventTypes.SourceItemsRemoved, EventCategory.Artifact)]
    [InlineData(EventTypes.StackProfileCreated, EventCategory.Artifact)]
    [InlineData(EventTypes.StackProfileApproved, EventCategory.Artifact)]
    [InlineData(EventTypes.StackProfileRejected, EventCategory.Artifact)]
    [InlineData(EventTypes.DeploymentGroupConfigured, EventCategory.Release)]
    [InlineData(EventTypes.DeploymentGroupFailed, EventCategory.Release)]
    [InlineData(EventTypes.CommitCreditChanged, EventCategory.Project)]
    [InlineData(EventTypes.ConnectionExpiring, EventCategory.Run)]
    public void FR193_Every_new_event_has_a_category_for_the_activity_feed(string type, EventCategory expected) =>
        Assert.Equal(expected, EventTypes.CategoryOf(type));

    [Fact]
    public void FR193_Links_open_the_Analysis_and_Refinement_pages()
    {
        Assert.Equal($"/projects/{Project}/analysis", NotificationRules.LinkPath(NotificationType.StackProfileReady, Project));
        Assert.Equal($"/projects/{Project}/refinement", NotificationRules.LinkPath(NotificationType.TrackerImportFailed, Project));
    }

    [Fact]
    public void FR193_Stack_profile_ready_wording_for_a_draft_and_for_a_failed_run()
    {
        var draft = NotificationMessages.Compose(new NotificationFacts(NotificationType.StackProfileReady, "Todo") { ArtifactVersion = 2 });
        Assert.Equal("Todo: stack profile v2 is ready for review", draft.Title);
        Assert.Contains("Analysis page", draft.Body, StringComparison.Ordinal);

        var failed = NotificationMessages.Compose(new NotificationFacts(NotificationType.StackProfileReady, "Todo")
        {
            Reason = NotificationRules.StackProfileRunFailedReason,
            ErrorCode = ErrorCodes.StackProfileInvalid,
        });
        Assert.Equal("Todo: the Stack Analyst run failed", failed.Title);
        Assert.Contains($"({ErrorCodes.StackProfileInvalid})", failed.Body, StringComparison.Ordinal);
        Assert.Contains("fill in the profile", failed.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("jira", "Jira")]
    [InlineData("linear", "Linear")]
    [InlineData("github", "GitHub Issues")]
    [InlineData("gitlab", "GitLab Issues")]
    public void FR193_Tracker_import_failed_wording_names_the_tracker_and_code(string provider, string name)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.TrackerImportFailed, "Todo") { Provider = provider, ErrorCode = ErrorCodes.SourceItemsTooLarge });

        Assert.Equal($"Todo: import from {name} failed", text.Title);
        Assert.Contains($"({ErrorCodes.SourceItemsTooLarge})", text.Body, StringComparison.Ordinal);
        Assert.Contains("Refinement page", text.Body, StringComparison.Ordinal);
    }

    // --- FR-165: the expiry warning ---

    [Fact]
    public void FR165_An_expiring_connection_uses_the_personal_connection_invalid_rules()
    {
        Assert.Equal(NotificationType.ConnectionInvalid, NotificationRules.ForEvent(EventTypes.ConnectionExpiring));
        Assert.True(NotificationRules.IsPersonal(NotificationType.ConnectionInvalid));
        Assert.Equal("userId", NotificationRules.RecipientField(NotificationType.ConnectionInvalid));
        Assert.Equal("/connections", NotificationRules.LinkPath(NotificationType.ConnectionInvalid, null));
    }

    [Theory]
    [InlineData("jira", "Jira")]
    [InlineData("linear", "Linear")]
    public void FR165_The_expiry_warning_names_the_tracker_and_the_date(string provider, string name)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.ConnectionInvalid, string.Empty)
        {
            Provider = provider,
            Reason = NotificationRules.ConnectionExpiringReason,
            ExpiresAt = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero),
        });

        Assert.Equal($"Your {name} connection expires on 2026-11-16", text.Title);
        Assert.Contains("Connections page", text.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid", text.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlatformProvider.Jira, 14, true)]
    [InlineData(PlatformProvider.Linear, 1, true)]
    [InlineData(PlatformProvider.Jira, 15, false)]
    [InlineData(PlatformProvider.Jira, -1, false)]
    [InlineData(PlatformProvider.Vercel, 3, false)]
    public void FR165_Jira_and_Linear_warn_from_14_days_before_the_expiry_until_it_passes(PlatformProvider provider, int days, bool warns) =>
        Assert.Equal(warns, TrackerRules.WarnsOfExpiry(provider, PlatformConnectionStatus.Valid, Now.AddDays(days), Now, warnedOfThisDate: false));

    [Fact]
    public void FR165_The_warning_is_sent_once_per_date_and_not_for_an_invalid_or_undated_connection()
    {
        Assert.False(TrackerRules.WarnsOfExpiry(PlatformProvider.Jira, PlatformConnectionStatus.Valid, Now.AddDays(3), Now, warnedOfThisDate: true));
        Assert.False(TrackerRules.WarnsOfExpiry(PlatformProvider.Jira, PlatformConnectionStatus.Invalid, Now.AddDays(3), Now, warnedOfThisDate: false));
        Assert.False(TrackerRules.WarnsOfExpiry(PlatformProvider.Linear, PlatformConnectionStatus.Valid, null, Now, warnedOfThisDate: false));
        Assert.True(TrackerRules.WarnsOfExpiry(PlatformProvider.Linear, PlatformConnectionStatus.InsufficientScope, Now.AddDays(3), Now, warnedOfThisDate: false));
    }
}
