using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Live;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Pipelines;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Live;

/// <summary>FR-302: the live hub sends <c>gateUpdated</c>, <c>pipelineRunUpdated</c> and <c>stageUpdated</c> to the project group.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelineHubEventsTests(AideApiFactory factory)
{
    private static readonly TimeSpan LiveBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task FR302_approving_a_gate_sends_gateUpdated_pipelineRunUpdated_and_stageUpdated_to_members_only()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        var runId = await PipelineApiGateTests.RunIdAsync(factory, id);

        await using var viewer = await LiveClient.ConnectAsync(factory, fixture.Viewer);
        var member = Listen(viewer.Connection);
        await viewer.JoinAsync(id);

        await using var outsider = await LiveClient.ConnectAsync(factory, fixture.Outsider);
        var stranger = Listen(outsider.Connection);
        await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(id));

        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve")).StatusCode);

        var gateUpdated = await member.NextAsync(ProjectsHub.GateUpdatedMethod, m => m.GetProperty("id").GetString() == gate.Id.ToString());
        Assert.Equal("approved", gateUpdated.GetProperty("status").GetString());
        var runUpdated = await member.NextAsync(ProjectsHub.PipelineRunUpdatedMethod, m => m.GetProperty("id").GetString() == runId.ToString());
        Assert.Equal(id.ToString(), runUpdated.GetProperty("projectId").GetString());
        var stage = await member.NextAsync(ProjectsHub.StageUpdatedMethod, m => m.GetProperty("id").GetString() == "planning");
        Assert.Equal(runId.ToString(), stage.GetProperty("runId").GetString());

        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.Equal(0, stranger.Count);
    }

    private static Inbox Listen(HubConnection connection)
    {
        var inbox = new Inbox();
        foreach (var method in new[] { ProjectsHub.GateUpdatedMethod, ProjectsHub.PipelineRunUpdatedMethod, ProjectsHub.StageUpdatedMethod })
        {
            connection.On<JsonElement>(method, message => inbox.Add(method, message));
        }

        return inbox;
    }

    /// <summary>Engine live messages received by one connection, by hub method.</summary>
    private sealed class Inbox
    {
        private readonly Channel<(string Method, JsonElement Message)> _messages = Channel.CreateUnbounded<(string, JsonElement)>();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add(string method, JsonElement message)
        {
            Interlocked.Increment(ref _count);
            _messages.Writer.TryWrite((method, message));
        }

        /// <summary>The next <paramref name="method"/> message matching <paramref name="match"/>, skipping others; fails after the budget.</summary>
        public async Task<JsonElement> NextAsync(string method, Func<JsonElement, bool> match)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(LiveBudget);
            while (true)
            {
                var (received, message) = await _messages.Reader.ReadAsync(cts.Token);
                if (received == method && match(message))
                {
                    return message;
                }
            }
        }
    }
}
