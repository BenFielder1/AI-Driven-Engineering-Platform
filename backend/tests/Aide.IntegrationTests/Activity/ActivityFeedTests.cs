using System.Net;
using Aide.Api.Features.Activity;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Activity;

/// <summary>FR-50: <c>GET /api/projects/:id/activity</c>.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ActivityFeedTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR50_200_events_load_the_latest_50_then_more_with_the_cursor()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var expected = await SeedEventsAsync(fixture, 200);

        var first = await GetAsync(fixture.Viewer.Client, fixture.Path + "/activity");

        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextBefore);
        Assert.Equal(expected.Take(50), first.Items.Select(i => i.Id));

        // Following the cursor walks every event exactly once, newest first (ties on time broken by id).
        var all = new List<Guid>(first.Items.Select(i => i.Id));
        var cursor = first.NextBefore;
        while (cursor is not null)
        {
            var page = await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/activity?before={Uri.EscapeDataString(cursor)}");
            all.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextBefore;
        }

        Assert.Equal(expected, all);
    }

    [Fact]
    public async Task FR50_amended_by_S4_10_The_feed_filters_by_category_and_resolves_actors()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await SeedEventsAsync(fixture, 30);

        var transitions = await GetAsync(fixture.Viewer.Client, fixture.Path + "/activity?type=transition&limit=100");

        Assert.Equal(10, transitions.Items.Count);
        Assert.Null(transitions.NextBefore);
        Assert.All(transitions.Items, i => Assert.Equal((EventTypes.PhaseChanged, (EventCategory?)EventCategory.Transition), (i.Type, i.Category)));
        Assert.Equal("Phase changed from refinement to specification (checkpoint 2)", transitions.Items[0].Summary);
        Assert.Equal("refinement", transitions.Items[0].Payload["from"]!.GetValue<string>());

        var gates = await GetAsync(fixture.Viewer.Client, fixture.Path + "/activity?type=gate");
        Assert.All(gates.Items, i => Assert.Equal(EventCategory.Gate, i.Category));
        Assert.All(gates.Items, i => Assert.Equal(new ActorDto(null, ActorResolver.SystemDisplayName, true), i.Actor));

        var owner = fixture.Owner.User!;
        Assert.All(transitions.Items, i => Assert.Equal(new ActorDto(owner.Id, owner.Name, false), i.Actor));
    }

    [Fact]
    public async Task FR50_Bad_parameters_are_400_and_non_members_get_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path + "/activity?before=not-a-cursor", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path + "/activity?type=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path + "/activity?limit=101", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri(fixture.Path + "/activity", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    private static async Task<ActivityPageDto> GetAsync(HttpClient client, string path) =>
        await ProjectApi.ReadAsync<ActivityPageDto>(await client.GetAsync(new Uri(path, UriKind.Relative), Ct));

    /// <summary>
    /// <paramref name="count"/> events cycling through phase changes (by the Owner), gate events (by the system) and step
    /// events, two per timestamp so ties are broken by id. Returns every project event id, newest first.
    /// </summary>
    private async Task<List<Guid>> SeedEventsAsync(ProjectFixture fixture, int count) =>
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var id = fixture.Project.Id;
            var start = DateTimeOffset.UtcNow.AddDays(1);
            var owner = fixture.Owner.User!.Id.ToString();
            for (var i = 0; i < count; i++)
            {
                var at = start.AddSeconds(i / 2);
                _ = (i % 3) switch
                {
                    0 => db.AddEvent(EventTypes.PhaseChanged, id, new { from = "refinement", to = "specification", projectVersion = 2 }, owner, at),
                    1 => db.AddEvent(EventTypes.GateCreated, id, new { type = "spec_approval" }, SystemActor.Name, at),
                    _ => db.AddEvent(EventTypes.StepCompleted, id, new { stepNumber = i }, SystemActor.Name, at),
                };
            }

            await db.SaveChangesAsync(Ct);
            return (await db.Events.Where(e => e.ProjectId == id).Select(e => new { e.Id, e.CreatedAt }).ToListAsync(Ct))
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Id, Comparer<Guid>.Create(CompareAsPostgres))
                .Select(e => e.Id)
                .ToList();
        });

    /// <summary>PostgreSQL orders uuids by their bytes in string (big-endian) order.</summary>
    private static int CompareAsPostgres(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("N"), b.ToString("N"));
}
