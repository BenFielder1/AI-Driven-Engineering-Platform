using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Tokens;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Live;

/// <summary>FR-46: live project updates over SignalR (<c>/api/hubs/projects</c>).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectsHubTests(AideApiFactory factory)
{
    private static readonly TimeSpan LiveBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR46_When_a_gate_is_approved_another_viewer_receives_it_within_2_seconds()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        await using var viewer = await LiveClient.ConnectAsync(factory, fixture.Viewer);
        await viewer.JoinAsync(id);

        var clock = Stopwatch.StartNew();
        var approve = await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve");
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        // The transition's PHASE_CHANGED is written (and pushed) before GATE_APPROVED, in the same commit.
        var phaseChanged = await viewer.NextAsync(LiveBudget);
        Assert.Equal(EventTypes.PhaseChanged, phaseChanged?.GetProperty("type").GetString());
        var message = await viewer.NextOfTypeAsync(EventTypes.GateApproved, LiveBudget);

        Assert.NotNull(message);
        Assert.True(clock.Elapsed < LiveBudget, $"took {clock.Elapsed}");
        var received = message.Value;
        Assert.Equal((id.ToString(), "gate"), (received.GetProperty("projectId").GetString(), received.GetProperty("category").GetString()));
        Assert.Equal(gate.Id.ToString(), received.GetProperty("payload").GetProperty("gateId").GetString());
        var stored = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GateApproved));
        Assert.Equal(stored.Id.ToString(), received.GetProperty("id").GetString());
    }

    [Fact]
    public async Task FR46_Artifact_events_are_pushed_to_members_in_order()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await using var owner = await LiveClient.ConnectAsync(factory, fixture.Owner);
        await owner.JoinAsync(id);

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, "project_request", "# Idea"), HttpStatusCode.Created);

        var created = await owner.NextAsync(LiveBudget);
        Assert.Equal((EventTypes.ArtifactCreated, "artifact"), (created?.GetProperty("type").GetString(), created?.GetProperty("category").GetString()));
        Assert.Equal(draft.Id.ToString(), created!.Value.GetProperty("payload").GetProperty("artifactId").GetString());
    }

    [Fact]
    public async Task FR46_Joining_a_project_one_is_not_a_member_of_or_that_does_not_exist_is_FORBIDDEN()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await using var outsider = await LiveClient.ConnectAsync(factory, fixture.Outsider);

        var notMember = await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(fixture.Project.Id));
        var unknown = await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(Guid.NewGuid()));

        Assert.Contains(ErrorCodes.Forbidden, notMember.Message, StringComparison.Ordinal);
        Assert.Contains(ErrorCodes.Forbidden, unknown.Message, StringComparison.Ordinal);

        await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.Null(await outsider.NextAsync(Quiet));
    }

    [Fact]
    public async Task FR46_Admins_can_join_any_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var admin = await ApiSession.AdminAsync(factory);
        await using var live = await LiveClient.ConnectAsync(factory, admin);

        await live.JoinAsync(fixture.Project.Id);

        await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.NotNull(await live.NextOfTypeAsync(EventTypes.ArtifactCreated, LiveBudget));
    }

    [Fact]
    public async Task FR46_Leaving_stops_the_messages_and_other_projects_are_not_received()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var other = await ProjectApi.FixtureAsync(factory);
        await ProjectApi.AddMemberInDbAsync(factory, other.Project.Id, fixture.Owner.User!.Id, ProjectRole.Viewer);
        await using var live = await LiveClient.ConnectAsync(factory, fixture.Owner);
        await live.JoinAsync(fixture.Project.Id);

        await ArtifactApi.CreateDraftAsync(other.Owner.Client, other.Project.Id, "project_request", "# Other");
        Assert.Null(await live.NextAsync(Quiet));

        await live.LeaveAsync(fixture.Project.Id);
        await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.Null(await live.NextAsync(Quiet));
    }

    [Fact]
    public async Task FR46_A_removed_member_stops_receiving_the_projects_events()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await using var viewer = await LiveClient.ConnectAsync(factory, fixture.Viewer);
        await viewer.JoinAsync(id);

        var removed = await fixture.Owner.Client.DeleteAsync(new Uri($"/api/projects/{id}/members/{fixture.Viewer.User!.Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.NotNull(await viewer.NextOfTypeAsync(EventTypes.MemberRemoved, LiveBudget));

        await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, "project_request", "# After removal");
        Assert.Null(await viewer.NextAsync(Quiet));
    }

    [Fact]
    public async Task FR46_Events_are_pushed_only_after_commit_and_never_for_a_rolled_back_transaction()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await using var live = await LiveClient.ConnectAsync(factory, fixture.Owner);
        await live.JoinAsync(id);

        // A background writer (own scope, explicit transaction) that rolls back...
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.AddEvent(EventTypes.ProjectUpdated, id, new { marker = "rolled-back" }, SystemActor.Name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
            Assert.Null(await live.NextAsync(Quiet));
            await transaction.RollbackAsync(Ct);
        }

        // ...and one that commits.
        Guid committedId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            committedId = db.AddEvent(EventTypes.ProjectUpdated, id, new { marker = "committed" }, SystemActor.Name, DateTimeOffset.UtcNow).Id;
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        var message = await live.NextAsync(LiveBudget);
        Assert.Equal(committedId.ToString(), message?.GetProperty("id").GetString());
        Assert.Equal("committed", message!.Value.GetProperty("payload").GetProperty("marker").GetString());
        Assert.Null(await live.NextAsync(Quiet));
    }

    [Fact]
    public async Task FR46_Negotiate_with_a_cookie_session_requires_the_antiforgery_header()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        const string negotiate = "/api/hubs/projects/negotiate?negotiateVersion=1";

        var withHeader = await fixture.Owner.Client.PostAsync(new Uri(negotiate, UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);

        fixture.Owner.SendXsrfHeader = false;
        var withoutHeader = await fixture.Owner.Client.PostAsync(new Uri(negotiate, UriKind.Relative), null, Ct);
        await Problems.AssertAsync(withoutHeader, HttpStatusCode.Forbidden, ErrorCodes.Antiforgery);

        using var anonymous = factory.CreateDefaultClient(AideApiFactory.BaseUri);
        var unauthenticated = await anonymous.PostAsync(new Uri(negotiate, UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    // ---------- NFR-SEC: cross-site WebSocket hijacking ----------

    [Theory]
    [InlineData("http://localhost:3001")] // another local app: same-site, so the Lax cookie is sent
    [InlineData("https://localhost:8443")]
    [InlineData("http://localhost")] // scheme differs
    [InlineData("null")] // sandboxed frame
    public async Task NFR_SEC_FR46_A_cookie_WebSocket_from_another_origin_is_refused(string origin)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Assert.ThrowsAnyAsync<Exception>(() => LiveClient.ConnectAsync(factory, fixture.Owner, origin));

        var upgrade = await RawUpgradeAsync(fixture.Owner, origin);
        await Problems.AssertAsync(upgrade, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task NFR_SEC_FR46_A_cookie_WebSocket_without_an_Origin_is_refused()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Assert.ThrowsAnyAsync<Exception>(() => LiveClient.ConnectAsync(factory, fixture.Owner, origin: null));

        var upgrade = await RawUpgradeAsync(fixture.Owner, origin: null);
        await Problems.AssertAsync(upgrade, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task NFR_SEC_FR46_A_cookie_WebSocket_from_the_apps_own_origin_connects()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await using var live = await LiveClient.ConnectAsync(factory, fixture.Owner, LiveClient.OwnOrigin);

        await live.JoinAsync(fixture.Project.Id);
        await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.NotNull(await live.NextOfTypeAsync(EventTypes.ArtifactCreated, LiveBudget));
    }

    [Fact]
    public async Task NFR_SEC_FR46_Negotiate_from_another_origin_is_FORBIDDEN_even_with_the_antiforgery_header()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var foreign = await NegotiateAsync(fixture.Owner.Client, "http://localhost:3001");
        var own = await NegotiateAsync(fixture.Owner.Client, LiveClient.OwnOrigin);

        await Problems.AssertAsync(foreign, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_FR46_The_origin_the_proxy_forwards_is_the_apps_own()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        // A reverse proxy (the Next.js web server, v2's nginx) may send Host without the port and the browser's host:port as X-Forwarded-Host.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/hubs/projects/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "https://localhost:8080");
        request.Headers.Add("X-Forwarded-Host", "localhost:8080");
        var forwarded = await fixture.Owner.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_FR46_Configured_allowed_origins_may_connect()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Live:AllowedOrigins:0", "http://localhost:3000/"));

        await using var live = await LiveClient.ConnectAsync(configured, fixture.Owner, "http://localhost:3000");
        await live.JoinAsync(fixture.Project.Id);

        await Assert.ThrowsAnyAsync<Exception>(() => LiveClient.ConnectAsync(configured, fixture.Owner, "http://localhost:3001"));
    }

    [Fact]
    public async Task NFR_SEC_FR46_Bearer_token_WebSockets_need_no_Origin()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var created = await fixture.Owner.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = (await ProjectApi.ReadAsync<CreatedApiTokenDto>(created, HttpStatusCode.Created)).Token;

        await using var live = await LiveClient.ConnectWithTokenAsync(factory, token);

        await live.JoinAsync(fixture.Project.Id);
    }

    // ---------- FR-46: join vs. member removal ----------

    [Fact]
    public async Task FR46_A_join_racing_a_member_removal_never_leaves_a_subscription()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var fixture = await ProjectApi.FixtureAsync(factory);
            var id = fixture.Project.Id;
            await using var viewer = await LiveClient.ConnectAsync(factory, fixture.Viewer);

            var join = Task.Run(async () =>
            {
                try
                {
                    await viewer.JoinAsync(id);
                }
                catch (HubException)
                {
                    // Refused because the removal committed first.
                }
            }, Ct);
            var removed = await fixture.Owner.Client.DeleteAsync(new Uri($"/api/projects/{id}/members/{fixture.Viewer.User!.Id}", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            await join;

            // Whatever the interleaving, nothing after the removal reaches the removed member.
            await viewer.NextOfTypeAsync(EventTypes.MemberRemoved, Quiet);
            await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, "project_request", "# After removal");
            Assert.Null(await viewer.NextOfTypeAsync(EventTypes.ArtifactCreated, Quiet));
        }
    }

    [Fact]
    public async Task FR46_A_refused_join_leaves_no_subscription_behind()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await using var outsider = await LiveClient.ConnectAsync(factory, fixture.Outsider);

        await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(fixture.Project.Id));

        // Becoming a member later doesn't revive the refused subscription.
        await ProjectApi.AddMemberInDbAsync(factory, fixture.Project.Id, fixture.Outsider.User!.Id, ProjectRole.Viewer);
        await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.Null(await outsider.NextAsync(Quiet));
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/hubs/projects/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        return client.SendAsync(request, Ct);
    }

    /// <summary>A WebSocket handshake (skip-negotiation) GET with the session's cookie, as a cross-site page would send it.</summary>
    private static Task<HttpResponseMessage> RawUpgradeAsync(ApiSession session, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/hubs/projects");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return session.Client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task FR46_Bearer_token_clients_negotiate_without_antiforgery_and_receive_events()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var created = await fixture.Owner.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = (await ProjectApi.ReadAsync<CreatedApiTokenDto>(created, HttpStatusCode.Created)).Token;
        using var bearer = ApiSession.BearerClient(factory, token);

        var negotiated = await bearer.PostAsync(new Uri("/api/hubs/projects/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);

        await using var live = await LiveClient.ConnectWithTokenAsync(factory, token);
        await live.JoinAsync(fixture.Project.Id);
        await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, fixture.Project.Id, "project_request", "# Idea");
        Assert.NotNull(await live.NextOfTypeAsync(EventTypes.ArtifactCreated, LiveBudget));
    }
}
