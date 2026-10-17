using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Notifications;

namespace Aide.UnitTests.Notifications;

/// <summary>FR-47: which events notify whom, through which channel subscription, under which key.</summary>
public class NotificationRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR47_The_first_budget_refusal_notifies() =>
        Assert.True(NotificationRules.NotifiesBudgetExceeded(null, []));

    [Fact]
    public void FR47_Repeated_budget_refusals_with_no_budget_change_notify_once() =>
        Assert.False(NotificationRules.NotifiesBudgetExceeded(T0, [T0.AddMinutes(-5)]));

    [Fact]
    public void FR47_A_budget_refusal_after_the_budget_changed_notifies_again()
    {
        Assert.True(NotificationRules.NotifiesBudgetExceeded(T0, [T0.AddMinutes(-5), T0.AddMinutes(3)]));
        Assert.True(NotificationRules.NotifiesBudgetExceeded(T0, [T0])); // changed in the same instant: notify rather than miss it
    }

    [Theory]
    [InlineData(EventTypes.GateCreated, NotificationType.GateRequired)]
    [InlineData(EventTypes.GateOverdue, NotificationType.GateOverdue)]
    [InlineData(EventTypes.ArtifactCreated, NotificationType.ArtifactReady)]
    [InlineData(EventTypes.StepBlocked, NotificationType.StepBlocked)]
    [InlineData(EventTypes.RunFailed, NotificationType.RunFailed)]
    [InlineData(EventTypes.BudgetExceeded, NotificationType.BudgetExceeded)]
    [InlineData(EventTypes.ReviewCompleted, NotificationType.ReviewCompleted)]
    [InlineData(EventTypes.ReleaseCompleted, NotificationType.ReleaseCompleted)]
    [InlineData(EventTypes.ReleaseFailed, NotificationType.ReleaseFailed)]
    [InlineData(EventTypes.ReleaseAwaitingMerge, NotificationType.ReleaseFailed)]
    [InlineData(EventTypes.RollbackCompleted, NotificationType.Rollback)]
    public void FR47_Each_listed_event_raises_its_notification(string eventType, NotificationType expected) =>
        Assert.Equal(expected, NotificationRules.ForEvent(eventType));

    [Theory]
    [InlineData(EventTypes.PhaseChanged)]
    [InlineData(EventTypes.GateApproved)]
    [InlineData(EventTypes.RunStarted)]
    [InlineData(EventTypes.ReviewFailed)]
    [InlineData(EventTypes.SettingsUpdated)]
    public void FR47_Other_events_raise_none(string eventType) => Assert.Null(NotificationRules.ForEvent(eventType));

    [Theory]
    [InlineData(NotificationType.GateRequired, RecipientScope.Owners)]
    [InlineData(NotificationType.GateReminder, RecipientScope.Owners)]
    [InlineData(NotificationType.GateOverdue, RecipientScope.Owners)]
    [InlineData(NotificationType.ArtifactReady, RecipientScope.Owners)]
    [InlineData(NotificationType.StepBlocked, RecipientScope.Owners)]
    [InlineData(NotificationType.RunFailed, RecipientScope.Owners)]
    [InlineData(NotificationType.BudgetExceeded, RecipientScope.Owners)]
    [InlineData(NotificationType.ReleaseFailed, RecipientScope.Owners)]
    [InlineData(NotificationType.ReviewCompleted, RecipientScope.AllMembers)]
    [InlineData(NotificationType.ReleaseCompleted, RecipientScope.AllMembers)]
    [InlineData(NotificationType.Rollback, RecipientScope.AllMembers)]
    public void FR47_Recipients_are_owners_for_actions_and_all_members_for_outcomes(NotificationType type, RecipientScope expected) =>
        Assert.Equal(expected, NotificationRules.Recipients(type));

    [Fact]
    public void FR47_Channel_events_use_the_settings_wire_values_and_reminders_follow_gate_required()
    {
        Assert.Equal("gate_required", NotificationRules.ChannelEvent(NotificationType.GateReminder));
        Assert.Equal("gate_required", NotificationRules.ChannelEvent(NotificationType.GateRequired));
        Assert.Equal("artifact_ready", NotificationRules.ChannelEvent(NotificationType.ArtifactReady));
        Assert.Equal("rollback", NotificationRules.ChannelEvent(NotificationType.Rollback));
    }

    [Fact]
    public void FR47_Keys_round_trip_and_reject_garbage()
    {
        var source = Guid.CreateVersion7();
        var key = NotificationRules.Key(NotificationType.ReleaseFailed, source);
        Assert.Equal($"release_failed:{source:D}", key);
        Assert.True(NotificationRules.TryParseKey(key, out var type, out var parsed));
        Assert.Equal((NotificationType.ReleaseFailed, source), (type, parsed));
        Assert.False(NotificationRules.TryParseKey("nope", out _, out _));
        Assert.False(NotificationRules.TryParseKey("unknown_type:" + source, out _, out _));
        Assert.False(NotificationRules.TryParseKey(null, out _, out _));
    }

    [Fact]
    public void S2_Muted_types_are_skipped_in_the_notification_centre()
    {
        Assert.True(NotificationRules.Wants(NotificationType.GateRequired, null));
        Assert.True(NotificationRules.Wants(NotificationType.GateRequired, [NotificationType.Rollback]));
        Assert.False(NotificationRules.Wants(NotificationType.GateRequired, [NotificationType.GateRequired]));
    }

    [Theory]
    [InlineData(GateType.SpecApproval, true)]
    [InlineData(GateType.PlanApproval, true)]
    [InlineData(GateType.PreRelease, true)]
    [InlineData(GateType.Escalation, false)]
    public void FR48_One_click_links_exist_only_for_spec_plan_and_pre_release_gates(GateType type, bool expected) =>
        Assert.Equal(expected, NotificationRules.OffersApprovalLinks(type));

    [Fact]
    public void FR47_Links_open_app_pages_never_approval_tokens()
    {
        var project = Guid.CreateVersion7();
        Assert.Equal($"/projects/{project}", NotificationRules.LinkPath(NotificationType.GateRequired, project));
        Assert.Equal($"/projects/{project}/artifacts/technical_specification", NotificationRules.LinkPath(NotificationType.ArtifactReady, project, "technical_specification"));
        Assert.Equal($"/projects/{project}/execution", NotificationRules.LinkPath(NotificationType.StepBlocked, project));
        Assert.Equal($"/projects/{project}/runs", NotificationRules.LinkPath(NotificationType.RunFailed, project));
        Assert.Equal($"/projects/{project}/review", NotificationRules.LinkPath(NotificationType.ReviewCompleted, project));
        Assert.All(NotificationRules.All, t => Assert.DoesNotContain("approvals", NotificationRules.LinkPath(t, project), StringComparison.Ordinal));
    }

    [Fact]
    public void FR47_Messages_keep_names_on_one_line_and_bound_their_length()
    {
        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.GateRequired, "Shop\nInjected: line") { GateType = GateType.PlanApproval });
        Assert.Equal("Shop Injected: line: The plan approval needs a decision", text.Title);
        Assert.DoesNotContain('\n', text.Body);

        var huge = NotificationMessages.Compose(new NotificationFacts(NotificationType.Rollback, new string('x', 5000)) { Reason = new string('y', 5000) });
        Assert.True(huge.Title.Length <= NotificationMessages.TitleMaxLength);
        Assert.True(huge.Body.Length <= NotificationMessages.BodyMaxLength);
    }

    [Fact]
    public void FR15_An_overdue_gate_gets_no_late_reminder()
    {
        var created = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(GateTimerActions.Nothing, GateTimers.Evaluate(GateStatus.Pending, created, created.AddHours(80), remindedAt: null, overdue: true));
        Assert.Equal(new GateTimerActions(false, true), GateTimers.Evaluate(GateStatus.Pending, created, created.AddHours(73), remindedAt: null, overdue: false));
    }
}

/// <summary>FR-49: per-channel retry with backoff.</summary>
public class DeliveryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    public void FR49_Transient_failures_are_retried_after_1_5_30_and_120_minutes(int attempts, int minutes) =>
        Assert.Equal(
            new DeliveryNextState(NotificationDeliveryStatus.Pending, Now.AddMinutes(minutes)),
            DeliveryPolicy.After(DeliveryAttemptResult.TransientFailure, attempts, Now));

    [Fact]
    public void FR49_The_fifth_failed_attempt_fails_the_delivery() =>
        Assert.Equal(new DeliveryNextState(NotificationDeliveryStatus.Failed, null), DeliveryPolicy.After(DeliveryAttemptResult.TransientFailure, DeliveryPolicy.MaxAttempts, Now));

    [Fact]
    public void FR49_Sent_permanent_and_skipped_outcomes_are_final()
    {
        Assert.Equal(new DeliveryNextState(NotificationDeliveryStatus.Sent, null), DeliveryPolicy.After(DeliveryAttemptResult.Sent, 1, Now));
        Assert.Equal(new DeliveryNextState(NotificationDeliveryStatus.Failed, null), DeliveryPolicy.After(DeliveryAttemptResult.PermanentFailure, 1, Now));
        Assert.Equal(new DeliveryNextState(NotificationDeliveryStatus.Skipped, null), DeliveryPolicy.After(DeliveryAttemptResult.Skipped, 1, Now));
    }

    [Theory]
    [InlineData(200, DeliveryAttemptResult.Sent)]
    [InlineData(204, DeliveryAttemptResult.Sent)]
    [InlineData(301, DeliveryAttemptResult.PermanentFailure)]
    [InlineData(400, DeliveryAttemptResult.PermanentFailure)]
    [InlineData(404, DeliveryAttemptResult.PermanentFailure)]
    [InlineData(408, DeliveryAttemptResult.TransientFailure)]
    [InlineData(429, DeliveryAttemptResult.TransientFailure)]
    [InlineData(500, DeliveryAttemptResult.TransientFailure)]
    [InlineData(503, DeliveryAttemptResult.TransientFailure)]
    public void FR49_HTTP_statuses_are_classified(int status, DeliveryAttemptResult expected) =>
        Assert.Equal(expected, DeliveryPolicy.ForHttpStatus(status));
}

/// <summary>FR-48, NFR-SEC: approval link tokens.</summary>
public class ApprovalTokenTests
{
    private const string Key = "unit-test-signing-key-0123456789abcdef";

    [Fact]
    public void FR48_A_token_parses_and_verifies_against_its_stored_hash_only()
    {
        var linkId = Guid.CreateVersion7();
        var token = ApprovalTokens.Create(Key, linkId);
        var hash = ApprovalTokens.NonceHash(ApprovalTokens.Nonce(Key, linkId));

        Assert.True(ApprovalTokens.TryParse(token, out var parsedId, out var nonce));
        Assert.Equal(linkId, parsedId);
        Assert.Equal(32, nonce.Length);
        Assert.True(ApprovalTokens.Verify(Key, linkId, nonce, hash));

        Assert.False(ApprovalTokens.Verify("another-signing-key-0123456789abcdefgh", linkId, nonce, hash));
        Assert.False(ApprovalTokens.Verify(Key, Guid.CreateVersion7(), nonce, hash));
        Assert.False(ApprovalTokens.Verify(Key, linkId, new byte[32], hash));
        Assert.False(ApprovalTokens.Verify(Key, linkId, nonce, new string('0', 64)));
    }

    [Fact]
    public void FR48_The_stored_hash_is_SHA256_of_the_nonce_not_the_token()
    {
        var linkId = Guid.CreateVersion7();
        var nonce = ApprovalTokens.Nonce(Key, linkId);
        var hash = ApprovalTokens.NonceHash(nonce);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(nonce)), hash);
        Assert.DoesNotContain(ApprovalTokens.Create(Key, linkId).Split('.')[1], hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef.")]
    [InlineData("0123456789abcdef0123456789abcdef.AAAA")]
    [InlineData("not-a-guid-not-a-guid-not-a-guid.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void FR48_Malformed_tokens_do_not_parse(string token) => Assert.False(ApprovalTokens.TryParse(token, out _, out _));

    [Fact]
    public void FR48_Links_are_used_before_expired_and_expire_after_72_hours()
    {
        var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = created + ApprovalTokens.Lifetime;
        Assert.Equal(TimeSpan.FromHours(72), ApprovalTokens.Lifetime);
        Assert.Equal(ApprovalLinkState.Valid, ApprovalTokens.State(expires, null, expires.AddTicks(-1)));
        Assert.Equal(ApprovalLinkState.Expired, ApprovalTokens.State(expires, null, expires));
        Assert.Equal(ApprovalLinkState.Used, ApprovalTokens.State(expires, created.AddHours(1), expires.AddHours(1)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("change-me", false)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    public void FR48_A_short_or_missing_signing_key_disables_links(string? key, bool usable) =>
        Assert.Equal(usable, ApprovalTokens.IsUsableKey(key));
}

/// <summary>FR-47, NFR-SEC: webhook signatures.</summary>
public class WebhookSignatureTests
{
    [Fact]
    public void FR47_The_signature_is_sha256_hex_HMAC_of_the_raw_body_and_verifies()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"gate_required\"}");
        var signature = WebhookSignature.Compute("s3cret", body);
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), body)), signature);
        Assert.True(WebhookSignature.Verify("s3cret", body, signature));
        Assert.False(WebhookSignature.Verify("s3cret", Encoding.UTF8.GetBytes("{\"type\":\"rollback\"}"), signature));
        Assert.False(WebhookSignature.Verify("other", body, signature));
        Assert.False(WebhookSignature.Verify("s3cret", body, null));
    }
}

/// <summary>NFR-SEC (SSRF): notification targets.</summary>
public class NetworkRulesTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.5")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void NFR_SEC_Private_loopback_link_local_and_reserved_addresses_are_refused(string address) =>
        Assert.False(NetworkRules.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    public void NFR_SEC_Public_addresses_are_allowed(string address) =>
        Assert.True(NetworkRules.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task NFR_SEC_Literal_private_hosts_are_blocked_before_connecting(string host)
    {
        var connector = new SafeTargetConnector((_, _) => throw new InvalidOperationException("no DNS for literals"));
        await Assert.ThrowsAsync<BlockedTargetException>(() => connector.ResolveAllowedAsync(host, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NFR_SEC_A_name_resolving_to_a_private_address_is_blocked_even_with_public_answers()
    {
        var onlyPrivate = new SafeTargetConnector((_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }));
        var mixed = new SafeTargetConnector((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.0.2") }));
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<BlockedTargetException>(() => onlyPrivate.ResolveAllowedAsync("internal.example.com", ct));
        await Assert.ThrowsAsync<BlockedTargetException>(() => mixed.ResolveAllowedAsync("rebind.example.com", ct));
    }

    [Fact]
    public async Task NFR_SEC_A_name_resolving_only_to_public_addresses_is_allowed()
    {
        var connector = new SafeTargetConnector((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var allowed = await connector.ResolveAllowedAsync("hooks.example.com", TestContext.Current.CancellationToken);
        Assert.Equal([IPAddress.Parse("93.184.216.34")], allowed);
    }
}
