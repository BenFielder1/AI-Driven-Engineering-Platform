using System.Net;
using System.Net.Http.Json;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-362, FR-366 (M72): the next 3 fire times of schedules, a preview before saving, and what Start run reads.</summary>
public sealed partial class TriggerTests
{
    private static readonly DateTimeOffset[] NextThreeMondays =
    [
        new(2030, 12, 2, 6, 0, 0, TimeSpan.Zero),
        new(2030, 12, 9, 6, 0, 0, TimeSpan.Zero),
        new(2030, 12, 16, 6, 0, 0, TimeSpan.Zero),
    ];

    [Fact]
    public async Task FR366_a_schedule_shows_its_next_3_fire_times_in_its_timezone_and_a_paused_one_counts_from_now()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var path = $"{fixture.Path}/pipeline-schedules";

        var created = await ProjectApi.ReadAsync<PipelineScheduleDto>(await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(), Ct), HttpStatusCode.Created);
        Assert.Equal(NextThreeMondays, created.NextFireTimes);
        var listed = Assert.Single(await ProjectApi.ReadAsync<List<PipelineScheduleDto>>(await fixture.Viewer.Client.GetAsync(path, Ct)));
        Assert.Equal(NextThreeMondays, listed.NextFireTimes);

        // Paused on Tuesday: a resume would start from now, so the preview skips the Monday that passed.
        clock.Set(new DateTimeOffset(2030, 12, 3, 12, 0, 0, TimeSpan.Zero));
        var paused = await ProjectApi.ReadAsync<PipelineScheduleDto>(await fixture.Owner.Client.PatchAsJsonAsync($"{path}/{created.Id}", new { paused = true }, Ct));
        Assert.Equal([NextThreeMondays[1], NextThreeMondays[2], new(2030, 12, 23, 6, 0, 0, TimeSpan.Zero)], paused.NextFireTimes);
    }

    [Fact]
    public async Task FR366_preview_gives_the_next_3_fire_times_of_an_unsaved_cron_to_owners_only()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var preview = $"{fixture.Path}/pipeline-schedules/preview";

        var shown = await ProjectApi.ReadAsync<PipelineSchedulePreviewDto>(
            await fixture.Owner.Client.GetAsync($"{preview}?cron={Uri.EscapeDataString("0 6 * * 1")}&timezone={Uri.EscapeDataString("Europe/London")}", Ct));
        Assert.Equal(("0 6 * * 1", "Europe/London"), (shown.Cron, shown.Timezone));
        Assert.Equal(NextThreeMondays, shown.NextFireTimes);

        await Problems.AssertAsync(
            await fixture.Owner.Client.GetAsync($"{preview}?cron={Uri.EscapeDataString("0 6 * *")}&timezone=UTC", Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(
            await fixture.Owner.Client.GetAsync($"{preview}?cron={Uri.EscapeDataString("0 6 * * 1")}&timezone=Mars%2FOlympus", Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(preview, Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(
            await fixture.Owner.Client.GetAsync($"{preview}?cron={new string('1', 101)}&timezone=UTC", Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(
            await fixture.Owner.Client.GetAsync($"{preview}?cron=*+*+*+*+*&timezone={new string('a', 101)}", Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.GetAsync($"{preview}?cron=*+*+*+*+*&timezone=UTC", Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await fixture.Outsider.Client.GetAsync($"{preview}?cron=*+*+*+*+*&timezone=UTC", Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR362_pipelines_and_definitions_show_run_control_triggers_approval_modes_and_input_schemas()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);

        var pipelines = await ProjectApi.ReadAsync<List<PipelineSummaryDto>>(await fixture.Viewer.Client.GetAsync("/api/pipelines", Ct));
        Assert.Equal(RunControl.Host, pipelines.Single(p => p.Id == SdlcPack.PipelineId).RunControl);
        var scheduled = pipelines.Single(p => p.Id == FixtureTriggersPack.ScheduledPipeline);
        Assert.Equal(RunControl.Engine, scheduled.RunControl);

        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{scheduled.Id}/definitions/{scheduled.Hash}", Ct));
        Assert.Equal(RunControl.Engine, definition.RunControl);
        Assert.Equal([ApprovalMode.Strict, ApprovalMode.Advisory, ApprovalMode.Autonomous], definition.ApprovalModes);
        Assert.Equal(ApprovalMode.Advisory, definition.DefaultApprovalMode);
        Assert.Equal([new PipelineTriggerDefinitionDto(TriggerKind.Schedule, null)], definition.Triggers);
        var brief = definition.Artifacts.Single(a => a.Key == "brief");
        Assert.True(brief.Input);
        Assert.NotNull(brief.Schema);
        Assert.Equal("topic", brief.Schema.Value.GetProperty("required")[0].GetString());
        Assert.Equal("integer", brief.Schema.Value.GetProperty("properties").GetProperty("words").GetProperty("type").GetString());

        var webhook = pipelines.Single(p => p.Id == FixtureTriggersPack.WebhookPipeline);
        var hooked = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{webhook.Id}/definitions/{webhook.Hash}", Ct));
        var trigger = Assert.Single(hooked.Triggers);
        Assert.Equal(TriggerKind.Webhook, trigger.Kind);
        Assert.NotNull(trigger.Input);

        var sdlc = pipelines.Single(p => p.Id == SdlcPack.PipelineId);
        var sdlcDefinition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{sdlc.Id}/definitions/{sdlc.Hash}", Ct));
        Assert.Equal(RunControl.Host, sdlcDefinition.RunControl);
    }
}
