using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Tevrin.Api.Features.Lifecycle;
using Tevrin.Api.Features.Live;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Pipelines;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Live;

/// <summary>
/// FR-363 live updates (§6 hub events): <c>stageBranchUpdated</c> for branch stage executions (<c>fixture-parallel</c>) and
/// throttled <c>mapProgress</c> for map items (<c>fixture-qa-handlers</c>), to the project group's members only; the SDLC
/// sends neither.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConstructHubEventsTests(TevrinApiFactory factory)
{
    private static readonly TimeSpan LiveBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId, object inputs)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId, approvalMode = "advisory", inputs }, Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static object Transcripts(int count, params int[] failing) => new
    {
        transcripts = new
        {
            items = Enumerable.Range(0, count)
                .Select(i => new Dictionary<string, object> { ["id"] = i, ["text"] = $"call transcript number {i}", ["fail"] = failing.Contains(i) })
                .ToArray(),
        },
    };

    /// <summary>Runs the project's queued <c>stage.run</c> jobs one at a time until none is left (a map queues more as items end).</summary>
    private static async Task DriveAllAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        for (var step = 0; step < 2000; step++)
        {
            var queued = (await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId)).OrderBy(j => j.CreatedAt).ToList();
            if (queued.Count == 0)
            {
                return;
            }

            await AgentJobs.RunAsync(app, queued[0]);
        }

        Assert.Fail("The map did not settle.");
    }

    [Fact]
    public async Task FR363_branch_stage_executions_send_stageBranchUpdated_with_ids_and_state_to_members_only()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await using var viewer = await LiveClient.ConnectAsync(app, fixture.Viewer);
        var member = Inbox.Listen(viewer.Connection);
        await viewer.JoinAsync(id);
        await using var outsider = await LiveClient.ConnectAsync(app, fixture.Outsider);
        var stranger = Inbox.Listen(outsider.Connection);
        await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(id));

        var runId = await StartAsync(fixture, FlowFixturePack.Parallel, new { ticker = "ACME" });
        await FixtureLinearApp.DriveAsync(app, id);

        bool Is(JsonElement m, string stage, string branch, bool exited) =>
            m.GetProperty("stageId").GetString() == stage && m.GetProperty("branch").GetString() == branch
            && (m.GetProperty("exitedAt").ValueKind != JsonValueKind.Null) == exited;

        var bullEntered = await member.NextAsync(ProjectsHub.StageBranchUpdatedMethod, m => Is(m, "bull", "0", exited: false));
        Assert.Equal(id.ToString(), bullEntered.GetProperty("projectId").GetString());
        Assert.Equal(runId.ToString(), bullEntered.GetProperty("runId").GetString());
        Assert.True(Guid.TryParse(bullEntered.GetProperty("executionId").GetString(), out _));
        Assert.Equal(JsonValueKind.Null, bullEntered.GetProperty("outcome").ValueKind);
        var bear = await member.NextAsync(ProjectsHub.StageBranchUpdatedMethod, m => Is(m, "bear", "1", exited: true));
        Assert.Equal(BranchOutcomes.Completed, bear.GetProperty("outcome").GetString());
        var review = await member.NextAsync(ProjectsHub.StageBranchUpdatedMethod, m => Is(m, "bull_review", "0", exited: true));
        Assert.Equal(BranchOutcomes.Completed, review.GetProperty("outcome").GetString());

        // Ids and state only: no content fields.
        Assert.Equal(
            ["branch", "executionId", "exitedAt", "outcome", "projectId", "runId", "stageId"],
            bear.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Empty(member.All(ProjectsHub.MapProgressMethod));

        await Task.Delay(Quiet, Ct);
        Assert.Equal(0, stranger.Count);
    }

    [Fact]
    public async Task FR363_map_items_send_throttled_mapProgress_ending_with_the_final_counts()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), new QaLog());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await using var viewer = await LiveClient.ConnectAsync(app, fixture.Viewer);
        var member = Inbox.Listen(viewer.Connection);
        await viewer.JoinAsync(id);

        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(200, 5, 50, 150));
        var clock = Stopwatch.StartNew();
        await DriveAllAsync(app, id);

        static bool Settled(JsonElement m) =>
            m.GetProperty("progress").GetProperty("completed").GetInt32() + m.GetProperty("progress").GetProperty("failed").GetInt32() == 200;
        var final = await member.NextAsync(ProjectsHub.MapProgressMethod, Settled);
        Assert.Equal(id.ToString(), final.GetProperty("projectId").GetString());
        Assert.Equal(runId.ToString(), final.GetProperty("runId").GetString());
        Assert.Equal("score", final.GetProperty("stageId").GetString());
        var progress = final.GetProperty("progress");
        Assert.Equal((200, 197, 3), (progress.GetProperty("total").GetInt32(), progress.GetProperty("completed").GetInt32(), progress.GetProperty("failed").GetInt32()));
        Assert.Equal(["progress", "projectId", "runId", "stageId"], final.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // At most one a second per map (plus the first, sent at once), never one per item; the final counts come last.
        await Task.Delay(Quiet, Ct);
        var sent = member.All(ProjectsHub.MapProgressMethod);
        Assert.InRange(sent.Count, 1, (int)Math.Ceiling(clock.Elapsed.TotalSeconds) + 1);
        Assert.True(sent.Count < 200);
        Assert.True(Settled(sent[^1]));
        var executionIds = sent.Select(m => m.GetProperty("progress").GetProperty("executionId").GetString()).Distinct().ToList();
        Assert.Single(executionIds);
        Assert.Empty(member.All(ProjectsHub.StageBranchUpdatedMethod));
    }

    [Fact]
    public async Task FR363_the_SDLC_sends_neither_stageBranchUpdated_nor_mapProgress()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;

        await using var viewer = await LiveClient.ConnectAsync(factory, fixture.Viewer);
        var member = Inbox.Listen(viewer.Connection);
        await viewer.JoinAsync(id);
        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve")).StatusCode);

        await member.NextAsync(ProjectsHub.StageUpdatedMethod, m => m.GetProperty("id").GetString() == "planning");
        await Task.Delay(Quiet, Ct);
        Assert.Empty(member.All(ProjectsHub.StageBranchUpdatedMethod));
        Assert.Empty(member.All(ProjectsHub.MapProgressMethod));
    }

    /// <summary>Engine live messages received by one connection, by hub method.</summary>
    private sealed class Inbox
    {
        private static readonly string[] Methods =
            [ProjectsHub.StageBranchUpdatedMethod, ProjectsHub.MapProgressMethod, ProjectsHub.StageUpdatedMethod];

        private readonly ConcurrentQueue<(string Method, JsonElement Message)> _received = new();
        private readonly Channel<bool> _signal = Channel.CreateUnbounded<bool>();

        public int Count => _received.Count;

        public static Inbox Listen(HubConnection connection)
        {
            var inbox = new Inbox();
            foreach (var method in Methods)
            {
                connection.On<JsonElement>(method, message =>
                {
                    inbox._received.Enqueue((method, message.Clone()));
                    inbox._signal.Writer.TryWrite(true);
                });
            }

            return inbox;
        }

        /// <summary>Every <paramref name="method"/> message received so far, in order.</summary>
        public List<JsonElement> All(string method) => [.. _received.Where(r => r.Method == method).Select(r => r.Message)];

        /// <summary>The first <paramref name="method"/> message matching <paramref name="match"/>; fails after the budget.</summary>
        public async Task<JsonElement> NextAsync(string method, Func<JsonElement, bool> match)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(LiveBudget);
            while (true)
            {
                foreach (var (received, message) in _received)
                {
                    if (received == method && match(message))
                    {
                        return message;
                    }
                }

                await _signal.Reader.ReadAsync(cts.Token);
            }
        }
    }
}
