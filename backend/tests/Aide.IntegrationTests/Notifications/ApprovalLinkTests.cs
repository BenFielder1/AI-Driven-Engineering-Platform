using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Approvals;
using Aide.Api.Features.Gates;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Notifications;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Notifications;

/// <summary>FR-48: signed single-use one-click approval links (<c>/api/approvals/{token}</c>).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ApprovalLinkTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> App => NotificationTestApp.For(factory).Factory;

    [Fact]
    public async Task FR48_A_valid_link_shows_the_gate_and_confirming_it_decides_the_gate_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, reject) = await GateWithLinksAsync(fixture);

        var view = await ProjectApi.ReadAsync<ApprovalLinkViewDto>(await fixture.Owner.Client.GetAsync(Path(approve), Ct));
        Assert.Equal(
            (gate.Id, fixture.Project.Id, GateType.SpecApproval, ApprovalLinkDecision.Approve, ApprovalLinkStatus.Valid),
            (view.GateId, view.ProjectId, view.GateType, view.Decision, view.Status));
        Assert.Equal(ArtifactType.TechnicalSpecification, view.ArtifactSummary!.Type);
        Assert.Equal("TechnicalSpecification v1", view.ArtifactSummary.Title);

        // Reading never uses the link.
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.Null(l.UsedAt));

        var decided = await ProjectApi.ReadAsync<GateDto>(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { comment = "LGTM" }, Ct));
        Assert.Equal((GateStatus.Approved, "LGTM"), (decided.Status, decided.Comment));
        Assert.Equal(Phase.Planning, (await LifecycleSeed.ProjectAsync(App, fixture.Project.Id)).CurrentPhase);
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.NotNull(l.UsedAt));
    }

    [Fact]
    public async Task FR48_A_used_link_answers_LINK_USED_and_nothing_changes()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var id = fixture.Project.Id;
        var (gate, approve, reject) = await GateWithLinksAsync(fixture);
        await ProjectApi.ReadAsync<GateDto>(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { }, Ct));
        var project = await LifecycleSeed.ProjectAsync(App, id);
        var events = await ProjectApi.WithDbAsync(App, db => db.Events.CountAsync(e => e.ProjectId == id, Ct));

        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(Path(approve), Ct), HttpStatusCode.Conflict, ErrorCodes.LinkUsed);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.LinkUsed);
        // The sibling reject link was voided with it.
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(reject), new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.LinkUsed);

        var after = await LifecycleSeed.ProjectAsync(App, id);
        Assert.Equal((project.CurrentPhase, project.ProjectVersion), (after.CurrentPhase, after.ProjectVersion));
        Assert.Equal(GateStatus.Approved, (await NotificationScenario.GateAsync(App, gate.Id)).Status);
        Assert.Equal(events, await ProjectApi.WithDbAsync(App, db => db.Events.CountAsync(e => e.ProjectId == id, Ct)));
    }

    [Fact]
    public async Task FR48_The_reject_link_rejects_the_gate_with_the_comment_as_feedback()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, _, reject) = await GateWithLinksAsync(fixture);

        var decided = await ProjectApi.ReadAsync<GateDto>(await fixture.Owner.Client.PostAsJsonAsync(Path(reject), new { comment = "Add auth" }, Ct));

        Assert.Equal((GateStatus.Rejected, "Add auth"), (decided.Status, decided.Comment));
        Assert.Equal(Phase.Specification, (await LifecycleSeed.ProjectAsync(App, fixture.Project.Id)).CurrentPhase);
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.NotNull(l.UsedAt));
    }

    [Fact]
    public async Task FR48_An_expired_link_answers_LINK_EXPIRED_and_the_gate_stays_pending()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, _) = await GateWithLinksAsync(fixture);
        await ProjectApi.WithDbAsync(App, db => db.ApprovalLinks.Where(l => l.GateId == gate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(Path(approve), Ct), HttpStatusCode.Gone, ErrorCodes.LinkExpired);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { }, Ct), HttpStatusCode.Gone, ErrorCodes.LinkExpired);

        Assert.Equal(GateStatus.Pending, (await NotificationScenario.GateAsync(App, gate.Id)).Status);
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.Null(l.UsedAt));
    }

    [Fact]
    public async Task FR48_Links_expire_72_hours_after_the_gate_notification()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var before = DateTimeOffset.UtcNow;
        var (gate, _, _) = await GateWithLinksAsync(fixture);

        var links = await NotificationScenario.LinksAsync(App, gate.Id);
        Assert.Equal([ApprovalLinkDecision.Approve, ApprovalLinkDecision.Reject], links.Select(l => l.Decision));
        Assert.All(links, l => Assert.InRange(l.ExpiresAt - before, TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(72) + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task FR48_Only_Owners_of_the_project_can_open_or_confirm_a_link()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, _) = await GateWithLinksAsync(fixture);
        using var admin = await ApiSession.AdminAsync(App);

        foreach (var session in new[] { fixture.Contributor, fixture.Viewer, fixture.Outsider, admin })
        {
            await Problems.AssertAsync(await session.Client.GetAsync(Path(approve), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            await Problems.AssertAsync(await session.Client.PostAsJsonAsync(Path(approve), new { }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        Assert.Equal(GateStatus.Pending, (await NotificationScenario.GateAsync(App, gate.Id)).Status);
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.Null(l.UsedAt));
    }

    [Fact]
    public async Task FR48_An_admin_who_is_also_an_Owner_can_use_the_link()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (_, approve, _) = await GateWithLinksAsync(fixture);
        using var admin = await ApiSession.AdminAsync(App);
        await ProjectApi.AddMemberInDbAsync(App, fixture.Project.Id, admin.User!.Id, ProjectRole.Owner);

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync(Path(approve), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR48_Unknown_forged_or_malformed_tokens_are_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, _) = await GateWithLinksAsync(fixture);
        var linkId = approve[..32];
        var forgedNonce = linkId + "." + System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);
        var otherKey = ApprovalTokens.Create("another-signing-key-0123456789abcdefgh", Guid.ParseExact(linkId, "N"));

        foreach (var token in new[] { forgedNonce, otherKey, ApprovalTokens.Create(AideApiFactory.TestSigningKey, Guid.CreateVersion7()), "not-a-token", approve + "x" })
        {
            await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(Path(token), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(forgedNonce), new { }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(GateStatus.Pending, (await NotificationScenario.GateAsync(App, gate.Id)).Status);
    }

    [Fact]
    public async Task FR48_A_gate_decided_elsewhere_shows_gate_closed_and_confirming_changes_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, _) = await GateWithLinksAsync(fixture);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "reject", new { comment = "No" }));

        var view = await ProjectApi.ReadAsync<ApprovalLinkViewDto>(await fixture.Owner.Client.GetAsync(Path(approve), Ct));
        Assert.Equal(ApprovalLinkStatus.GateClosed, view.Status);

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
        Assert.Equal(GateStatus.Rejected, (await NotificationScenario.GateAsync(App, gate.Id)).Status);
        Assert.All(await NotificationScenario.LinksAsync(App, gate.Id), l => Assert.Null(l.UsedAt));
    }

    [Fact]
    public async Task FR48_Tokens_are_never_stored_and_in_app_notifications_carry_no_token()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (gate, approve, reject) = await GateWithLinksAsync(fixture);

        var links = await NotificationScenario.LinksAsync(App, gate.Id);
        foreach (var (link, token) in links.Zip([approve, reject]))
        {
            var nonce = token[33..];
            Assert.Equal(64, link.NonceHash.Length);
            Assert.DoesNotContain(nonce, link.NonceHash, StringComparison.Ordinal);
            Assert.NotEqual(Convert.ToHexStringLower(System.Buffers.Text.Base64Url.DecodeFromChars(nonce)), link.NonceHash);
        }

        var inApp = Assert.Single(await NotificationJobs.InAppAsync(App, fixture.Project.Id));
        Assert.Equal($"/projects/{fixture.Project.Id}", inApp.Link);
        Assert.DoesNotContain("/approvals/", inApp.Link + inApp.Body + inApp.Title, StringComparison.Ordinal);
        var nonceText = approve[33..];
        var stored = await ProjectApi.WithDbAsync(App, db => db.Notifications.AnyAsync(n => n.Link.Contains(nonceText) || n.Body.Contains(nonceText), Ct));
        Assert.False(stored);
        Assert.DoesNotContain(factory.Logs.Entries, l => l.AllText.Contains(approve[33..], StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR48_Escalation_gates_get_no_one_click_links()
    {
        var test = NotificationTestApp.For(factory);
        using var fixture = await ProjectApi.FixtureAsync(test.Factory);
        var id = fixture.Project.Id;
        var slack = $"https://hooks.slack.com/services/T0/B0/{Guid.NewGuid():N}";
        await NotificationScenario.ConfigureChannelsAsync(fixture.Owner.Client, id, new { type = "slack", secret = slack, events = NotificationScenario.Ev("gate_required") });
        var gate = await LifecycleSeed.AddGateAsync(test.Factory, id, GateType.Escalation);
        await ProjectApi.WithDbAsync(test.Factory, async db =>
        {
            Aide.Infrastructure.Persistence.EventLog.AddEvent(db, EventTypes.GateCreated, id, new { gateId = gate.Id, type = GateType.Escalation }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        await NotificationJobs.RunQueuedAsync(test.Factory, id);

        Assert.Empty(await NotificationScenario.LinksAsync(test.Factory, gate.Id));
        var message = Assert.Single(test.Http.To(slack));
        Assert.DoesNotContain("/approvals/", message.Text, StringComparison.Ordinal);
        Assert.Contains($"/projects/{id}", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NFR_SEC_Approval_tokens_never_reach_the_logs()
    {
        using var fixture = await ProjectApi.FixtureAsync(App);
        var (_, approve, _) = await GateWithLinksAsync(fixture);
        var nonce = approve[33..];

        // Reading the link, and a confirmation refused by antiforgery (which logs a warning with the path).
        Assert.Equal(HttpStatusCode.OK, (await fixture.Owner.Client.GetAsync(Path(approve), Ct)).StatusCode);
        fixture.Owner.SendXsrfHeader = false;
        try
        {
            await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Path(approve), new { }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Antiforgery);
        }
        finally
        {
            fixture.Owner.SendXsrfHeader = true;
        }

        var rejected = Assert.Single(factory.Logs.Entries, l => l.Message.StartsWith("Antiforgery validation failed for POST /api/approvals/", StringComparison.Ordinal)
            && l.Scopes.Any(s => s.Key == "RequestPath" && Equals(s.Value, "/api/approvals/[redacted]")));
        Assert.Contains("/api/approvals/[redacted]", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, l => l.AllText.Contains(nonce, StringComparison.Ordinal));
    }

    private static string Path(string token) => $"/api/approvals/{token}";

    /// <summary>A pending spec gate whose notification job ran: its link pair exists; returns the approve and reject tokens.</summary>
    private async Task<(GateDto Gate, string Approve, string Reject)> GateWithLinksAsync(ProjectFixture fixture)
    {
        var gate = await NotificationScenario.PendingSpecGateAsync(App, fixture);
        await NotificationJobs.RunQueuedAsync(App, fixture.Project.Id);
        var links = await NotificationScenario.LinksAsync(App, gate.Id);
        Assert.Equal(2, links.Count);
        return (
            gate,
            ApprovalTokens.Create(AideApiFactory.TestSigningKey, links.Single(l => l.Decision == ApprovalLinkDecision.Approve).Id),
            ApprovalTokens.Create(AideApiFactory.TestSigningKey, links.Single(l => l.Decision == ApprovalLinkDecision.Reject).Id));
    }
}
