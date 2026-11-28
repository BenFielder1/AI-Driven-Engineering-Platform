using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Api;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-364 (M72): a pending <c>external_action</c> gate shows the exact payload approval sends.</summary>
public sealed partial class ExternalActionTests
{
    [Fact]
    public async Task FR364_an_external_action_gate_shows_its_exact_stored_payload_without_internal_fields()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, gate) = await PendingAsync(app, fixture);
        var prepared = Assert.Single(log.Prepared);

        foreach (var session in new[] { fixture.Owner, fixture.Contributor, fixture.Viewer })
        {
            var payload = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await session.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct))).Payload;
            Assert.NotNull(payload);
            Assert.Equal(
                (PipelineGatePayloadDto.ExternalHandlerKind, SubmitApplicationHandler.Name, Submit, prepared),
                (payload.Kind, payload.Handler, payload.Stage, payload.Payload));
            Assert.Null(payload.Tool);
        }

        var item = Assert.Single(
            (await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(
                await fixture.Contributor.Client.GetAsync(new Uri("/api/pipeline-gates?gateType=external_action&limit=200", UriKind.Relative), Ct))).Items,
            i => i.Gate.Id == gate.Id);
        Assert.False(item.CanDecide);
        Assert.Equal(prepared, item.Gate.Payload!.Payload);

        // The wire shape has no claim, send time, job or stage execution id.
        var raw = await (await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("jobId", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stageExecutionId", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ranAt", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sentAt", raw, StringComparison.Ordinal);
    }
}
