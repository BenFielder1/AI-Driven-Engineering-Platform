using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Domain.Review;

namespace Aide.UnitTests.Deployments;

/// <summary>§4.15, FR-87–FR-91: deployment states, polling, review waiting, production adoption, Vercel errors, env sync,
/// the Fake provider and the deployment notifications.</summary>
public class DeploymentPollingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Ben = Guid.CreateVersion7();

    // ------------------------------------------------------------------ §4.15 states

    [Theory]
    [InlineData("QUEUED", DeploymentStatus.Queued)]
    [InlineData("INITIALIZING", DeploymentStatus.Building)]
    [InlineData("BUILDING", DeploymentStatus.Building)]
    [InlineData("READY", DeploymentStatus.Ready)]
    [InlineData("ready", DeploymentStatus.Ready)]
    [InlineData("ERROR", DeploymentStatus.Error)]
    [InlineData("CANCELED", DeploymentStatus.Canceled)]
    [InlineData("SOMETHING_NEW", DeploymentStatus.Building)]
    [InlineData(null, DeploymentStatus.Building)]
    public void S4_15_Vercel_readyState_maps_to_the_deployment_state(string? readyState, DeploymentStatus expected) =>
        Assert.Equal(expected, DeploymentStates.FromReadyState(readyState));

    [Theory]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Building, true)]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Ready, true)]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Error, true)]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Canceled, true)]
    [InlineData(DeploymentStatus.Queued, DeploymentStatus.Queued, true)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Building, true)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Ready, true)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Error, true)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Canceled, true)]
    [InlineData(DeploymentStatus.Building, DeploymentStatus.Queued, false)]
    [InlineData(DeploymentStatus.Ready, DeploymentStatus.Error, false)]
    [InlineData(DeploymentStatus.Ready, DeploymentStatus.Ready, false)]
    [InlineData(DeploymentStatus.Error, DeploymentStatus.Ready, false)]
    [InlineData(DeploymentStatus.Canceled, DeploymentStatus.Building, false)]
    public void S4_15_Deployment_state_transitions(DeploymentStatus from, DeploymentStatus to, bool allowed) =>
        Assert.Equal(allowed, DeploymentStates.CanTransition(from, to));

    [Fact]
    public void S4_15_Ready_error_and_canceled_are_terminal()
    {
        Assert.Equal(
            [DeploymentStatus.Ready, DeploymentStatus.Error, DeploymentStatus.Canceled],
            Enum.GetValues<DeploymentStatus>().Where(DeploymentStates.IsTerminal));
    }

    // ------------------------------------------------------------------ §4.15 polling

    [Fact]
    public void S4_15_Polls_every_10_seconds_for_2_minutes_then_every_30()
    {
        Assert.Equal(T0.AddSeconds(10), DeploymentPollRules.NextPollAt(T0, T0));
        Assert.Equal(T0.AddSeconds(119 + 10), DeploymentPollRules.NextPollAt(T0, T0.AddSeconds(119)));
        Assert.Equal(T0.AddSeconds(120 + 30), DeploymentPollRules.NextPollAt(T0, T0.AddMinutes(2)));
        Assert.Equal(T0.AddMinutes(10).AddSeconds(30), DeploymentPollRules.NextPollAt(T0, T0.AddMinutes(10)));
    }

    [Fact]
    public void S4_15_The_last_poll_runs_at_the_30_minute_ceiling()
    {
        Assert.Equal(T0.AddMinutes(30), DeploymentPollRules.NextPollAt(T0, T0.AddMinutes(29).AddSeconds(50)));
        Assert.Equal(T0.AddMinutes(31), DeploymentPollRules.NextPollAt(T0, T0.AddMinutes(31)));
    }

    [Fact]
    public void S4_15_A_poll_finishes_waits_or_times_out_with_DEPLOYMENT_TIMEOUT()
    {
        Assert.Equal(DeploymentPollDecisionKind.Wait, DeploymentPollRules.Decide(T0, T0.AddMinutes(5), DeploymentStatus.Building).Kind);
        Assert.Equal(T0.AddMinutes(5).AddSeconds(30), DeploymentPollRules.Decide(T0, T0.AddMinutes(5), DeploymentStatus.Building).NextPollAt);
        Assert.Equal(DeploymentPollDecisionKind.Finished, DeploymentPollRules.Decide(T0, T0.AddMinutes(1), DeploymentStatus.Ready).Kind);
        Assert.Equal(DeploymentPollDecisionKind.Finished, DeploymentPollRules.Decide(T0, T0.AddMinutes(1), DeploymentStatus.Error).Kind);

        // A terminal state at the ceiling still finishes; still building at the ceiling times out.
        Assert.Equal(DeploymentPollDecisionKind.Finished, DeploymentPollRules.Decide(T0, T0.AddMinutes(30), DeploymentStatus.Ready).Kind);
        Assert.Equal(DeploymentPollDecisionKind.TimedOut, DeploymentPollRules.Decide(T0, T0.AddMinutes(30), DeploymentStatus.Building).Kind);
        Assert.Equal(DeploymentPollDecisionKind.TimedOut, DeploymentPollRules.Decide(T0, T0.AddMinutes(45), DeploymentStatus.Queued).Kind);
        Assert.Equal("DEPLOYMENT_TIMEOUT", ErrorCodes.DeploymentTimeout);
    }

    // ------------------------------------------------------------------ FR-90 review waiting

    [Fact]
    public void FR90_Review_doesnt_wait_without_a_preview()
    {
        var decision = PreviewReviewRules.Decide(null, null, T0);

        Assert.False(decision.Wait);
        Assert.Null(decision.WaitedSeconds);
        Assert.False(decision.TimedOut);
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Building)]
    public void FR90_Review_waits_for_an_unfinished_preview_rechecking_every_10_seconds(DeploymentStatus status)
    {
        var first = PreviewReviewRules.Decide(status, null, T0);
        Assert.True(first.Wait);
        Assert.Equal(T0, first.WaitingSince);
        Assert.Equal(T0.AddSeconds(10), first.RecheckAt);

        var later = PreviewReviewRules.Decide(status, T0, T0.AddMinutes(19));
        Assert.True(later.Wait);
        Assert.Equal(T0, later.WaitingSince);
    }

    [Fact]
    public void FR90_Review_proceeds_when_the_preview_finishes_or_after_20_minutes()
    {
        var ready = PreviewReviewRules.Decide(DeploymentStatus.Ready, T0, T0.AddMinutes(3));
        Assert.False(ready.Wait);
        Assert.Equal(180, ready.WaitedSeconds);
        Assert.False(ready.TimedOut);

        var immediately = PreviewReviewRules.Decide(DeploymentStatus.Error, null, T0);
        Assert.False(immediately.Wait);
        Assert.Null(immediately.WaitedSeconds);

        var gaveUp = PreviewReviewRules.Decide(DeploymentStatus.Building, T0, T0.AddMinutes(20));
        Assert.False(gaveUp.Wait);
        Assert.True(gaveUp.TimedOut);
        Assert.Equal(1200, gaveUp.WaitedSeconds);
    }

    [Fact]
    public void FR90_A_failed_preview_is_a_critical_architecture_deterministic_finding_with_the_log_tail()
    {
        var finding = Assert.Single(PreviewReviewRules.Findings(DeploymentStatus.Error, "Type error: x is not a string", "https://p.vercel.app"));

        Assert.Equal(ReviewCategory.Architecture, finding.Category);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal(FindingSource.Deterministic, finding.Source);
        Assert.Equal("Vercel preview build failed", finding.Message);
        Assert.Equal("Type error: x is not a string", finding.Evidence);
        Assert.Empty(PreviewReviewRules.Findings(DeploymentStatus.Ready, "log", null));
        Assert.Empty(PreviewReviewRules.Findings(DeploymentStatus.Building, null, null));
        Assert.Empty(PreviewReviewRules.Findings(null, null, null));
    }

    [Fact]
    public void FR90_The_finding_evidence_is_the_end_of_a_long_log()
    {
        var log = new string('a', 10_000) + "THE END";

        var evidence = Assert.Single(PreviewReviewRules.Findings(DeploymentStatus.Error, log, null)).Evidence!;

        Assert.Equal(PreviewReviewRules.EvidenceMaxLength, evidence.Length);
        Assert.EndsWith("THE END", evidence, StringComparison.Ordinal);
        Assert.Equal("https://p.vercel.app", Assert.Single(PreviewReviewRules.Findings(DeploymentStatus.Error, null, "https://p.vercel.app")).Evidence);
    }

    [Fact]
    public void FR90_The_build_log_tail_keeps_the_end()
    {
        var log = new string('x', Deployment.BuildLogTailMaxLength + 5) + "tail";

        Assert.Equal(Deployment.BuildLogTailMaxLength, Deployment.TailOf(log).Length);
        Assert.EndsWith("tail", Deployment.TailOf(log), StringComparison.Ordinal);
        Assert.Equal("short", Deployment.TailOf("short"));
    }

    // ------------------------------------------------------------------ FR-91 production

    [Fact]
    public void FR91_The_automatic_production_deployment_looks_for_Vercels_own_for_30_seconds()
    {
        Assert.Equal(T0.AddSeconds(10), ProductionDeploymentRules.LookAgainAt(T0, T0));
        Assert.Equal(T0.AddSeconds(30), ProductionDeploymentRules.LookAgainAt(T0, T0.AddSeconds(25)));
        Assert.Null(ProductionDeploymentRules.LookAgainAt(T0, T0.AddSeconds(30)));
        Assert.True(ProductionDeploymentRules.Adopts(DeploymentTarget.Production, DeploymentTrigger.Auto, DeploymentProvider.Vercel));
        Assert.False(ProductionDeploymentRules.Adopts(DeploymentTarget.Production, DeploymentTrigger.Auto, DeploymentProvider.Fake));
        Assert.False(ProductionDeploymentRules.Adopts(DeploymentTarget.Production, DeploymentTrigger.Redeploy, DeploymentProvider.Vercel));
        Assert.False(ProductionDeploymentRules.Adopts(DeploymentTarget.Preview, DeploymentTrigger.Auto, DeploymentProvider.Vercel));
    }

    // ------------------------------------------------------------------ FR-87, FR-88 Vercel errors

    [Theory]
    [InlineData(401, null, "Not authorized", false, VercelFailure.CredentialRejected)]
    [InlineData(403, "forbidden", "Not authorized", true, VercelFailure.CredentialRejected)]
    [InlineData(403, "forbidden", "You don't have permission to create the project.", false, VercelFailure.Forbidden)]
    [InlineData(429, "rate_limited", "Too many requests", false, VercelFailure.RateLimited)]
    [InlineData(500, null, null, false, VercelFailure.Upstream)]
    [InlineData(null, null, null, false, VercelFailure.Upstream)]
    [InlineData(404, "not_found", "Project not found", false, VercelFailure.NotFound)]
    [InlineData(400, "bad_request", "To link a GitHub repository, you need to install the GitHub integration first.", false, VercelFailure.GitNotConnected)]
    [InlineData(404, "repo_not_found", "Repository not found", false, VercelFailure.GitNotConnected)]
    [InlineData(400, "bad_request", "Hobby teams can't deploy repositories owned by an organization; upgrade to Pro.", false, VercelFailure.PlanLimit)]
    [InlineData(400, "bad_request", "Invalid name", false, VercelFailure.Rejected)]
    public void FR88_Vercel_errors_are_classified(int? status, string? code, string? message, bool invalidToken, VercelFailure expected) =>
        Assert.Equal(expected, VercelErrorRules.Classify(status, code, message, invalidToken));

    [Fact]
    public void FR88_Vercel_failures_map_to_the_spec_error_codes()
    {
        Assert.Equal(ErrorCodes.VercelGitNotConnected, VercelErrorRules.ErrorCodeFor(VercelFailure.GitNotConnected));
        Assert.Equal(ErrorCodes.VercelPlanLimit, VercelErrorRules.ErrorCodeFor(VercelFailure.PlanLimit));
        Assert.Equal(ErrorCodes.CredentialInvalid, VercelErrorRules.ErrorCodeFor(VercelFailure.CredentialRejected));
        Assert.Equal(ErrorCodes.NotFound, VercelErrorRules.ErrorCodeFor(VercelFailure.NotFound));
        Assert.Equal(ErrorCodes.UpstreamError, VercelErrorRules.ErrorCodeFor(VercelFailure.Upstream));
        Assert.Equal(ErrorCodes.UpstreamError, VercelErrorRules.ErrorCodeFor(VercelFailure.RateLimited));
        Assert.StartsWith("https://github.com/apps/vercel", VercelErrorRules.DefaultGitHubInstallUrl, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ FR-88 settings

    [Fact]
    public void FR88_Deployment_is_supported_by_a_vercel_preset_or_an_Owner_opting_in()
    {
        Assert.True(DeploymentSettingsRules.Supported(["vercel"], providerSet: false));
        Assert.True(DeploymentSettingsRules.Supported([], providerSet: true));
        Assert.True(DeploymentSettingsRules.Supported(null, providerSet: true));
        Assert.False(DeploymentSettingsRules.Supported(null, providerSet: false));
        Assert.False(DeploymentSettingsRules.Supported(["netlify"], providerSet: false));
    }

    [Fact]
    public void FR88_nextjs_supabase_required_env_is_flagged_when_missing()
    {
        IReadOnlyList<DeploymentTarget> both = [DeploymentTarget.Preview, DeploymentTarget.Production];
        var project = new Dictionary<string, IReadOnlyList<DeploymentTarget>>
        {
            ["NEXT_PUBLIC_SUPABASE_URL"] = both,
            ["NEXT_PUBLIC_SUPABASE_ANON_KEY"] = [DeploymentTarget.Preview],
        };

        Assert.True(DeploymentEnvRules.IsPresent("NEXT_PUBLIC_SUPABASE_URL", both, project));
        Assert.False(DeploymentEnvRules.IsPresent("NEXT_PUBLIC_SUPABASE_ANON_KEY", both, project));
        Assert.True(DeploymentEnvRules.IsPresent("NEXT_PUBLIC_SUPABASE_ANON_KEY", [DeploymentTarget.Preview], project));
        Assert.False(DeploymentEnvRules.IsPresent("SUPABASE_SERVICE_ROLE_KEY", both, project));
    }

    [Fact]
    public void FR88_Local_Git_projects_deploy_only_with_the_Fake_provider_switched_on()
    {
        Assert.True(DeploymentSettingsRules.Validate(DeploymentProvider.Vercel, Ana, "prj_a", false, null, null, GitProvider.Local, allowFake: true, false).IsSuccess);
        Assert.True(DeploymentSettingsRules.Validate(DeploymentProvider.Vercel, Ana, "prj_a", false, null, null, GitProvider.Local, allowFake: false, false).IsFailure);
    }

    [Theory]
    [InlineData(null, "acme/todo", true)]
    [InlineData("acme/todo", "acme/todo", true)]
    [InlineData("Acme/Todo", "acme/todo", true)]
    [InlineData("acme/other", "acme/todo", false)]
    public void FR88_A_linked_Vercel_project_must_be_unlinked_or_linked_to_the_same_repo(string? linked, string project, bool matches) =>
        Assert.Equal(matches, DeploymentSettingsRules.LinkMatches(linked, project));

    [Fact]
    public void FR88_The_binding_goes_with_the_Owner_who_bound_it()
    {
        Assert.True(DeploymentSettingsRules.RoleChangeClearsBinding(Ana, Ana, ProjectRole.Contributor));
        Assert.False(DeploymentSettingsRules.RoleChangeClearsBinding(Ana, Ana, ProjectRole.Owner));
        Assert.False(DeploymentSettingsRules.RoleChangeClearsBinding(Ana, Ben, ProjectRole.Viewer));
        Assert.True(DeploymentSettingsRules.RemovalClearsBinding(Ana, Ana));
        Assert.False(DeploymentSettingsRules.RemovalClearsBinding(Ana, Ben));
        Assert.False(DeploymentSettingsRules.RemovalClearsBinding(null, Ben));
    }

    // ------------------------------------------------------------------ §4.15 env sync

    [Fact]
    public void S4_15_Env_sync_upserts_by_key_and_target_and_deletes_dropped_targets()
    {
        var first = EnvSyncRules.Plan([DeploymentTarget.Production, DeploymentTarget.Preview], null);
        Assert.Equal([new EnvTargetWrite(DeploymentTarget.Preview, null), new EnvTargetWrite(DeploymentTarget.Production, null)], first.Writes);
        Assert.Empty(first.Deletes);

        var ids = new Dictionary<string, string> { ["preview"] = "env_p", ["production"] = "env_q" };
        var narrowed = EnvSyncRules.Plan([DeploymentTarget.Preview], ids);
        Assert.Equal([new EnvTargetWrite(DeploymentTarget.Preview, "env_p")], narrowed.Writes);
        Assert.Equal([new EnvTargetDelete(DeploymentTarget.Production, "env_q")], narrowed.Deletes);

        Assert.Equal(["env_p", "env_q"], EnvSyncRules.AllIds(ids));
        Assert.Empty(EnvSyncRules.AllIds(null));
        Assert.Equal(("sensitive", "encrypted"), (EnvSyncRules.VercelType(true), EnvSyncRules.VercelType(false)));
    }

    [Fact]
    public void S4_15_Env_writes_stay_within_120_a_minute()
    {
        var recent = Enumerable.Range(0, 119).Select(i => T0.AddMilliseconds(i)).ToList();

        Assert.True(EnvSyncRules.TryWrite(recent, 1, T0.AddSeconds(1)).Allowed);
        var (allowed, retryAt) = EnvSyncRules.TryWrite(recent, 2, T0.AddSeconds(1));
        Assert.False(allowed);
        Assert.Equal(T0.AddMinutes(1), retryAt);

        // Writes older than a minute no longer count.
        Assert.True(EnvSyncRules.TryWrite(recent, 120, T0.AddMinutes(1).AddSeconds(1)).Allowed);
    }

    [Fact]
    public void S4_15_Env_creates_are_batched()
    {
        Assert.Empty(EnvSyncRules.Batches(0));
        Assert.Equal([3], EnvSyncRules.Batches(3));
        Assert.Equal([50, 50, 20], EnvSyncRules.Batches(120));
    }

    // ------------------------------------------------------------------ §4.15, §9 Fake provider

    [Fact]
    public void S4_15_The_Fake_provider_is_ready_after_2_polls_at_a_fake_url()
    {
        Assert.Equal("https://fake-0123abcd.vercel.test", FakeDeploymentRules.Url("0123ABCDEF4567"));
        Assert.Equal(DeploymentStatus.Building, FakeDeploymentRules.StateAfter(1, fails: false));
        Assert.Equal(DeploymentStatus.Ready, FakeDeploymentRules.StateAfter(2, fails: false));
        Assert.Equal(DeploymentStatus.Error, FakeDeploymentRules.StateAfter(2, fails: true));
    }

    [Fact]
    public void S4_15_The_Fake_build_fails_when_the_commit_has_aide_fake_fail_build()
    {
        Assert.True(FakeDeploymentRules.FailsBuild(["README.md", ".aide-fake/fail-build"]));
        Assert.False(FakeDeploymentRules.FailsBuild(["README.md", "src/.aide-fake/fail-build"]));
        Assert.False(FakeDeploymentRules.FailsBuild(null));
    }

    // ------------------------------------------------------------------ notifications (FR-90, FR-91, FR-92)

    [Fact]
    public void FR91_A_failed_deployment_notifies_Owners_and_a_ready_preview_every_member()
    {
        Assert.Equal(NotificationType.DeploymentFailed, NotificationRules.ForEvent(EventTypes.DeploymentFailed));
        Assert.Equal(RecipientScope.Owners, NotificationRules.Recipients(NotificationType.DeploymentFailed));
        Assert.Equal(NotificationType.PreviewReady, NotificationRules.ForEvent(EventTypes.DeploymentPreviewReady));
        Assert.Equal(RecipientScope.AllMembers, NotificationRules.Recipients(NotificationType.PreviewReady));
        Assert.Null(NotificationRules.ForEvent(EventTypes.DeploymentUpdated));
        Assert.Null(NotificationRules.ForEvent(EventTypes.DeploymentCreated));
    }

    [Fact]
    public void FR91_FR90_Deployment_notification_wording()
    {
        var failed = NotificationMessages.Compose(new NotificationFacts(NotificationType.DeploymentFailed, "Todo")
        {
            DeploymentTarget = "production",
            ReleaseVersion = "1.2.0",
            ErrorCode = "VERCEL_ERROR",
        });
        Assert.Equal("Todo: production deployment 1.2.0 failed", failed.Title);
        Assert.Contains("1.2.0", failed.Body, StringComparison.Ordinal);
        Assert.Contains("The release stands", failed.Body, StringComparison.Ordinal);

        var ready = NotificationMessages.Compose(new NotificationFacts(NotificationType.PreviewReady, "Todo")
        {
            ReleaseVersion = "1.2.0",
            DeploymentUrl = "https://todo-abc.vercel.app",
        });
        Assert.Equal("Todo: preview 1.2.0 is ready", ready.Title);
        Assert.Contains("https://todo-abc.vercel.app", ready.Body, StringComparison.Ordinal);
    }
}
