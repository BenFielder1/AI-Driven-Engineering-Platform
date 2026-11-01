using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;

namespace Aide.UnitTests.Notifications;

/// <summary>FR-155: the v4 events and notification types: which events raise them, who gets them, their wording, and the expiry rule.</summary>
public class Fr155NotificationTypesTests
{
    private static readonly Guid Project = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EventTypes.StackChanged, EventCategory.Project)]
    [InlineData(EventTypes.StackDetected, EventCategory.Project)]
    [InlineData(EventTypes.GitHostInstanceChanged, EventCategory.Project)]
    [InlineData(EventTypes.GitBindingCleared, EventCategory.Project)]
    [InlineData(EventTypes.DeploymentTargetConfigured, EventCategory.Release)]
    [InlineData(EventTypes.DeploymentTargetFailed, EventCategory.Release)]
    [InlineData(EventTypes.DatabaseProvisioned, EventCategory.Release)]
    [InlineData(EventTypes.DatabaseProvisioningFailed, EventCategory.Release)]
    [InlineData(EventTypes.DatabaseExpiring, EventCategory.Release)]
    [InlineData(EventTypes.DatabaseReset, EventCategory.Release)]
    [InlineData(EventTypes.MigrationRunStarted, EventCategory.Release)]
    [InlineData(EventTypes.MigrationRunCompleted, EventCategory.Release)]
    [InlineData(EventTypes.MigrationRunFailed, EventCategory.Release)]
    [InlineData(EventTypes.PreviewRolloutCompleted, EventCategory.Release)]
    public void FR155_Every_new_event_has_a_category_for_the_activity_feed(string type, EventCategory expected) =>
        Assert.Equal(expected, EventTypes.CategoryOf(type));

    [Theory]
    [InlineData(EventTypes.DeploymentTargetFailed)]
    [InlineData(EventTypes.DatabaseProvisioningFailed)]
    [InlineData(EventTypes.DatabaseExpiring)]
    [InlineData(EventTypes.MigrationRunFailed)]
    public void FR155_Failures_and_alerts_have_outcome_failure(string type) =>
        Assert.Equal(EventOutcome.Failure, EventTypes.OutcomeOf(type));

    [Theory]
    [InlineData(EventTypes.GitBindingCleared, NotificationType.GitBindingCleared, "git_binding_cleared")]
    [InlineData(EventTypes.DatabaseProvisioningFailed, NotificationType.DatabaseProvisioningFailed, "database_provisioning_failed")]
    [InlineData(EventTypes.DatabaseExpiring, NotificationType.DatabaseExpiring, "database_expiring")]
    [InlineData(EventTypes.MigrationRunFailed, NotificationType.MigrationFailed, "migration_failed")]
    [InlineData(EventTypes.DeploymentTargetFailed, NotificationType.DeploymentTargetAttention, "deployment_target_attention")]
    public void FR155_Each_notifying_event_raises_its_type_for_Owners_and_project_channels(string eventType, NotificationType type, string wire)
    {
        Assert.Equal(type, NotificationRules.ForEvent(eventType));
        Assert.Equal(RecipientScope.Owners, NotificationRules.Recipients(type));
        Assert.Equal(wire, type.ToWire());
        Assert.Equal(wire, NotificationRules.ChannelEvent(type));
        Assert.Contains(type, NotificationRules.ChannelTypes);
    }

    [Theory]
    [InlineData(EventTypes.StackDetected)]
    [InlineData(EventTypes.StackChanged)]
    [InlineData(EventTypes.GitHostInstanceChanged)]
    [InlineData(EventTypes.DeploymentTargetConfigured)]
    [InlineData(EventTypes.DatabaseProvisioned)]
    [InlineData(EventTypes.DatabaseReset)]
    [InlineData(EventTypes.MigrationRunStarted)]
    [InlineData(EventTypes.MigrationRunCompleted)]
    [InlineData(EventTypes.PreviewRolloutCompleted)]
    [InlineData(EventTypes.PresetChanged)]
    public void FR155_The_other_events_raise_no_notification(string eventType) =>
        Assert.Null(NotificationRules.ForEvent(eventType));

    [Theory]
    [InlineData(DeploymentTargetFailureReasons.GitAccessLost, true)]
    [InlineData(DeploymentTargetFailureReasons.TokenInvalid, true)]
    [InlineData(DeploymentTargetFailureReasons.SetupFailed, false)]
    [InlineData(DeploymentTargetFailureReasons.ReadinessFailed, false)]
    [InlineData(null, false)]
    public void FR155_A_failed_target_notifies_only_for_lost_Git_access_or_an_invalid_token(string? reason, bool notifies) =>
        Assert.Equal(notifies, NotificationRules.Notifies(new NotificationFacts(NotificationType.DeploymentTargetAttention, "Todo") { Reason = reason }));

    [Fact]
    public void FR155_A_failed_migration_notifies_once_as_migration_failed_not_also_as_deployment_failed()
    {
        Assert.True(NotificationRules.Notifies(new NotificationFacts(NotificationType.MigrationFailed, "Todo") { ErrorCode = ErrorCodes.MigrationFailed }));
        Assert.False(NotificationRules.Notifies(new NotificationFacts(NotificationType.DeploymentFailed, "Todo") { Component = NotificationRules.MigrationsComponent }));
        Assert.False(NotificationRules.Notifies(new NotificationFacts(NotificationType.DeploymentFailed, "Todo") { ErrorCode = ErrorCodes.MigrationFailed }));
        Assert.True(NotificationRules.Notifies(new NotificationFacts(NotificationType.DeploymentFailed, "Todo") { ErrorCode = ErrorCodes.PlatformError }));
    }

    [Fact]
    public void FR155_Links_open_the_page_that_acts_on_the_notification()
    {
        var settings = $"/projects/{Project}/settings/deployment";
        Assert.Equal(settings, NotificationRules.LinkPath(NotificationType.DatabaseProvisioningFailed, Project));
        Assert.Equal(settings, NotificationRules.LinkPath(NotificationType.DatabaseExpiring, Project));
        Assert.Equal(settings, NotificationRules.LinkPath(NotificationType.DeploymentTargetAttention, Project));
        Assert.Equal($"/projects/{Project}/migrations", NotificationRules.LinkPath(NotificationType.MigrationFailed, Project, deploymentTarget: "production", hasMigrationRun: true));
        Assert.Equal($"/projects/{Project}/release", NotificationRules.LinkPath(NotificationType.MigrationFailed, Project, deploymentTarget: "production"));
        Assert.Equal($"/projects/{Project}/review", NotificationRules.LinkPath(NotificationType.MigrationFailed, Project, deploymentTarget: "preview"));
    }

    [Fact]
    public void FR155_Render_free_database_expiring_wording_carries_the_date()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.DatabaseExpiring, "Todo")
        {
            DeploymentEnvironment = "production",
            Provider = "render",
            Reason = ManagedDatabaseRules.ExpiringReasonExpiry,
            ExpiresAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
        });

        Assert.Equal("Todo: production database expires on 2026-11-07", text.Title);
        Assert.Contains("Render Postgres database of Todo expires on 2026-11-07", text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR155_A_paused_Supabase_project_wording_points_to_the_restore()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.DatabaseExpiring, "Todo")
        {
            DeploymentEnvironment = "preview",
            Provider = "supabase",
            Reason = ManagedDatabaseRules.ExpiringReasonPaused,
        });

        Assert.Equal("Todo: preview database is paused", text.Title);
        Assert.Contains("Supabase project", text.Body, StringComparison.Ordinal);
        Assert.Contains("restore", text.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeploymentTargetFailureReasons.TokenInvalid, "Render rejected the token")]
    [InlineData(DeploymentTargetFailureReasons.GitAccessLost, "Render can no longer access the repository")]
    public void FR155_Deployment_target_attention_wording_says_why(string reason, string expected)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.DeploymentTargetAttention, "Todo") { Component = "backend", Provider = "render", Reason = reason });

        Assert.Equal("Todo: backend target needs attention", text.Title);
        Assert.Contains(expected, text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR155_Database_setup_and_migration_failures_name_the_error_code()
    {
        var database = NotificationMessages.Compose(new NotificationFacts(NotificationType.DatabaseProvisioningFailed, "Todo") { DeploymentEnvironment = "production", Provider = "supabase", ErrorCode = ErrorCodes.PlatformPlanLimit });
        Assert.Equal("Todo: production database setup failed", database.Title);
        Assert.Contains($"({ErrorCodes.PlatformPlanLimit})", database.Body, StringComparison.Ordinal);

        var migration = NotificationMessages.Compose(new NotificationFacts(NotificationType.MigrationFailed, "Todo") { DeploymentEnvironment = "preview", ErrorCode = ErrorCodes.MigrationFailed });
        Assert.Equal("Todo: preview migration failed", migration.Title);
        Assert.Contains($"({ErrorCodes.MigrationFailed})", migration.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NotificationType.DeploymentFailed, "production")]
    [InlineData(NotificationType.DeploymentFailed, "preview")]
    [InlineData(NotificationType.PreviewReady, "preview")]
    public void FR155_Deployment_copy_names_no_host(NotificationType type, string target)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(type, "Todo") { DeploymentEnvironment = target, ReleaseVersion = "1.2.0" });

        Assert.DoesNotContain("Vercel", text.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Vercel", text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR155_Deployment_failed_copy_of_the_migrations_component_says_migrations()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.DeploymentFailed, "Todo")
        {
            Component = NotificationRules.MigrationsComponent,
            DeploymentEnvironment = "production",
            ReleaseVersion = "1.2.0",
            ErrorCode = ErrorCodes.MigrationFailed,
        });

        Assert.Equal("Todo: production migrations 1.2.0 failed", text.Title);
        Assert.Contains("database migrations", text.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("deployment of", text.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gitlab", "GitLab")]
    [InlineData("bitbucket", "Bitbucket")]
    [InlineData("github", "GitHub")]
    public void FR155_Release_awaiting_merge_copy_names_the_projects_Git_host(string provider, string host)
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.ReleaseAwaitingMerge, "Todo") { Tag = "v1.2.0", Provider = provider });

        Assert.Equal($"Todo: release v1.2.0 awaits a merge on {host}", text.Title);
        Assert.Contains($"Merge it on {host}", text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FR155_A_Render_free_database_7_days_from_expiry_warns_with_the_date()
    {
        var expiresAt = Now.AddDays(7);

        var change = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, expiresAt, new DatabaseLiveState(ManagedDatabaseStatus.Available, expiresAt), Now);

        Assert.Equal(new DatabaseSweepChange(ManagedDatabaseStatus.Expiring, expiresAt, ManagedDatabaseRules.ExpiringReasonExpiry), change);
    }

    [Fact]
    public void FR155_A_database_more_than_7_days_from_expiry_or_without_one_stays_quiet()
    {
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, Now.AddDays(7).AddSeconds(1), null, Now));
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Railway, ManagedDatabaseStatus.Available, null, null, Now));
    }

    [Fact]
    public void FR155_A_second_sweep_doesnt_warn_again() =>
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Expiring, Now.AddDays(3), null, Now));

    [Fact]
    public void FR155_An_unreachable_provider_leaves_the_stored_expiry_standing()
    {
        var expiresAt = Now.AddDays(2);

        var change = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, expiresAt, null, Now);

        Assert.Equal(ManagedDatabaseStatus.Expiring, change?.Status);
    }

    [Fact]
    public void FR155_An_upgraded_plan_clears_the_warning_silently_and_a_later_expiry_warns_again()
    {
        var upgraded = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, Now.AddDays(2), new DatabaseLiveState(ManagedDatabaseStatus.Available, null), Now);
        Assert.Equal(new DatabaseSweepChange(ManagedDatabaseStatus.Available, null, null), upgraded);

        var cleared = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Expiring, Now.AddDays(2), new DatabaseLiveState(ManagedDatabaseStatus.Available, Now.AddDays(30)), Now);
        Assert.Equal(new DatabaseSweepChange(ManagedDatabaseStatus.Available, Now.AddDays(30), null), cleared);

        var later = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, Now.AddDays(30), null, Now.AddDays(24));
        Assert.Equal(ManagedDatabaseRules.ExpiringReasonExpiry, later?.ExpiringReason);
    }

    [Fact]
    public void FR155_An_expiry_moved_within_the_window_is_stored_and_announced()
    {
        var change = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Expiring, Now.AddDays(3), new DatabaseLiveState(ManagedDatabaseStatus.Available, Now.AddDays(5)), Now);

        Assert.Equal(new DatabaseSweepChange(ManagedDatabaseStatus.Expiring, Now.AddDays(5), ManagedDatabaseRules.ExpiringReasonExpiry), change);
    }

    [Fact]
    public void FR155_A_near_stored_date_the_provider_moved_far_away_is_replaced_silently()
    {
        var change = ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, Now.AddDays(3), new DatabaseLiveState(ManagedDatabaseStatus.Available, Now.AddDays(30)), Now);

        Assert.Equal(new DatabaseSweepChange(ManagedDatabaseStatus.Available, Now.AddDays(30), null), change);
    }

    [Theory]
    [InlineData(ManagedDatabaseStatus.Available, false, DatabaseMigrationTarget.Usable)]
    [InlineData(ManagedDatabaseStatus.Expiring, true, DatabaseMigrationTarget.Usable)]
    [InlineData(ManagedDatabaseStatus.Paused, true, DatabaseMigrationTarget.Unavailable)]
    [InlineData(ManagedDatabaseStatus.Error, true, DatabaseMigrationTarget.Unavailable)]
    [InlineData(ManagedDatabaseStatus.Provisioning, true, DatabaseMigrationTarget.Unavailable)]
    [InlineData(ManagedDatabaseStatus.Error, false, DatabaseMigrationTarget.Absent)]
    [InlineData(ManagedDatabaseStatus.Provisioning, false, DatabaseMigrationTarget.Absent)]
    [InlineData(ManagedDatabaseStatus.Removed, false, DatabaseMigrationTarget.Absent)]
    public void FR153_FR155_A_real_database_that_cant_take_migrations_is_never_absent(ManagedDatabaseStatus status, bool hasDatabase, DatabaseMigrationTarget expected) =>
        Assert.Equal(expected, ManagedDatabaseRules.MigrationTarget(status, hasDatabase));

    [Theory]
    [InlineData(new[] { "deployment_failed" }, true)]
    [InlineData(new[] { "migration_failed" }, true)]
    [InlineData(new[] { "deployment_failed", "migration_failed" }, true)]
    [InlineData(new[] { "run_failed" }, false)]
    [InlineData(new string[0], false)]
    public void FR155_A_channel_subscribed_to_deployment_failed_also_receives_migration_failed(string[] events, bool delivered) =>
        Assert.Equal(delivered, NotificationRules.ChannelReceives(NotificationType.MigrationFailed, events));

    [Fact]
    public void FR155_Subscribing_to_migration_failed_doesnt_bring_deployment_failures_and_personal_types_reach_no_channel()
    {
        Assert.False(NotificationRules.ChannelReceives(NotificationType.DeploymentFailed, ["migration_failed"]));
        Assert.True(NotificationRules.ChannelReceives(NotificationType.DeploymentFailed, ["deployment_failed"]));
        Assert.False(NotificationRules.ChannelReceives(NotificationType.ConnectionInvalid, ["deployment_failed", "connection_invalid"]));
    }

    [Fact]
    public void FR155_A_paused_Supabase_project_warns_once_and_a_restored_one_goes_back_silently()
    {
        var paused = new DatabaseLiveState(ManagedDatabaseStatus.Paused, null);
        var healthy = new DatabaseLiveState(ManagedDatabaseStatus.Available, null);

        Assert.Equal(
            new DatabaseSweepChange(ManagedDatabaseStatus.Paused, null, ManagedDatabaseRules.ExpiringReasonPaused),
            ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Available, null, paused, Now));
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Paused, null, paused, Now));
        Assert.Equal(
            new DatabaseSweepChange(ManagedDatabaseStatus.Available, null, null),
            ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Paused, null, healthy, Now));
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Available, null, null, Now));
    }

    [Fact]
    public void FR155_A_suspended_Render_database_isnt_reported_as_a_paused_Supabase_project() =>
        Assert.Null(ManagedDatabaseRules.ExpirySweep(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Available, null, new DatabaseLiveState(ManagedDatabaseStatus.Paused, null), Now));
}
