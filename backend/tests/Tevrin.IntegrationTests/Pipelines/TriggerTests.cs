using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Domain.Triggers;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Triggers;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-336, FR-337 (M66 step c): the trigger fixtures, read from the engine tests' fixture files.</summary>
public sealed class FixtureTriggersPack : IPipelinePack
{
    public const string ScheduledPipeline = "fixture-scheduled";
    public const string WebhookPipeline = "fixture-webhook";

    public string Id => "fixture-triggers";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-scheduled.pipeline.yaml", Read("fixture-scheduled.pipeline.yaml"));
        registry.AddDefinition("fixture-webhook.pipeline.yaml", Read("fixture-webhook.pipeline.yaml"));
        registry.AddSchema("brief.schema.json", Read("brief.schema.json"));
        registry.AddSchema("payload.schema.json", Read("payload.schema.json"));
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", name));
}

/// <summary>A clock tests move by hand (the app's <see cref="TimeProvider"/>); its timers are the system's.</summary>
public sealed class TriggerClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public void Set(DateTimeOffset now)
    {
        lock (_gate)
        {
            _now = now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }
}

/// <summary>
/// FR-336, FR-337: schedules fire runs at their due times on a fake clock (skipped while a run is active), and signed
/// webhook calls start runs, with replay, staleness and rotation checks (§4.71, §4.72).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class TriggerTests(TevrinApiFactory factory)
{
    /// <summary>§4.72's worked example (as in the engine's PipelineWebhookSignatureTests).</summary>
    private const string VectorSecret = "whsec_test";
    private const long VectorTimestamp = 1764590400;
    private const string VectorBody = "{\"a\":1}";
    private const string VectorV1 = "54fe8ee64e5499df7c6cee87b1b22c2800b3fbfed80e088b818a25b927713fb0";

    /// <summary>Monday 2 Dec 2030, 05:59 in Europe/London (GMT, so 05:59Z). Fake clocks only move forward from a time after
    /// today: sessions signed in at the fake time stay valid, and a clock set before the real time breaks sign-in.</summary>
    private static readonly DateTimeOffset MondayBeforeSix = new(2030, 12, 2, 5, 59, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Create(TevrinApiFactory api, TriggerClock clock) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddPipelinePack(new FixtureTriggersPack());
            s.AddSingleton<TimeProvider>(clock);
        }));

    private static object Schedule(string cron = "0 6 * * 1", string timezone = "Europe/London", string pipelineId = FixtureTriggersPack.ScheduledPipeline) =>
        new { pipelineId, cron, timezone, inputs = new { brief = new { topic = "Tides" } } };

    [Fact]
    public async Task FR336_a_Monday_0600_London_schedule_starts_exactly_one_run_at_0600_and_skips_while_it_is_active()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var created = await ProjectApi.ReadAsync<PipelineScheduleDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-schedules", Schedule(), Ct), HttpStatusCode.Created);
        Assert.Equal(new DateTimeOffset(2030, 12, 2, 6, 0, 0, TimeSpan.Zero), created.NextFireAt);

        Assert.Equal(0, await QueueDueAsync(app, fixture.Project.Id));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await QueueDueAsync(app, fixture.Project.Id));
        Assert.Equal(0, await QueueDueAsync(app, fixture.Project.Id)); // claimed: never twice
        await RunFiresAsync(app, fixture.Project.Id);

        var runs = await RunsAsync(app, fixture.Project.Id);
        var run = Assert.Single(runs);
        Assert.Equal(GenericRunStatus.Active, run.Status);
        var moved = Assert.Single(await fixture.Viewer.Client.GetFromJsonAsync<List<PipelineScheduleDto>>($"{fixture.Path}/pipeline-schedules", Ct) ?? []);
        Assert.Equal((new DateTimeOffset(2030, 12, 9, 6, 0, 0, TimeSpan.Zero), created.NextFireAt), (moved.NextFireAt, moved.LastFireAt));

        // A week later the run is still active: the fire is skipped and recorded, no second run.
        clock.Set(new DateTimeOffset(2030, 12, 9, 6, 0, 30, TimeSpan.Zero));
        Assert.Equal(1, await QueueDueAsync(app, fixture.Project.Id));
        await RunFiresAsync(app, fixture.Project.Id);
        Assert.Single(await RunsAsync(app, fixture.Project.Id));

        var outcomes = (await FiredEventsAsync(app, fixture.Project.Id)).Select(e => e.GetProperty("outcome").GetString()).ToList();
        Assert.Equal([ScheduleFireOutcomes.Started, ScheduleFireOutcomes.Skipped], outcomes);
    }

    [Fact]
    public async Task FR336_catch_up_after_downtime_fires_once_then_resumes()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        await ProjectApi.ReadAsync<PipelineScheduleDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-schedules", Schedule(), Ct), HttpStatusCode.Created);

        // Down for three Mondays.
        clock.Set(new DateTimeOffset(2030, 12, 18, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, await QueueDueAsync(app, fixture.Project.Id));
        Assert.Equal(0, await QueueDueAsync(app, fixture.Project.Id));
        // 16 days on the sign-in cookie (14 days) has expired: read the schedule from the database.
        var schedule = Assert.Single(await SchedulesAsync(app, fixture.Project.Id));
        Assert.Equal(new DateTimeOffset(2030, 12, 23, 6, 0, 0, TimeSpan.Zero), schedule.NextFireAt);
    }

    [Fact]
    public async Task FR336_schedule_validation_permissions_pause_and_delete()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var path = $"{fixture.Path}/pipeline-schedules";

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(cron: "0 6 * *"), Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(cron: "61 6 * * 1"), Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(timezone: "Mars/Olympus"), Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(pipelineId: FixtureTriggersPack.WebhookPipeline), Ct), HttpStatusCode.Conflict, ErrorCodes.TriggerNotSupported);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(pipelineId: SdlcPack.PipelineId), Ct), HttpStatusCode.Conflict, ErrorCodes.RunControlledByHost);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(pipelineId: "no-such-pipeline"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, new { pipelineId = FixtureTriggersPack.ScheduledPipeline, cron = "0 6 * * 1", timezone = "UTC", inputs = new { brief = new { words = 3 } } }, Ct),
            HttpStatusCode.BadRequest, ErrorCodes.InputInvalid);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync(path, Schedule(), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(path, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var created = await ProjectApi.ReadAsync<PipelineScheduleDto>(await fixture.Owner.Client.PostAsJsonAsync(path, Schedule(), Ct), HttpStatusCode.Created);
        var paused = await ProjectApi.ReadAsync<PipelineScheduleDto>(await fixture.Owner.Client.PatchAsJsonAsync($"{path}/{created.Id}", new { paused = true }, Ct));
        Assert.True(paused.Paused);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await QueueDueAsync(app, fixture.Project.Id));

        await Problems.AssertAsync(await fixture.Owner.Client.PatchAsJsonAsync($"{path}/{created.Id}", new { cron = "nope" }, Ct), HttpStatusCode.BadRequest, ErrorCodes.ScheduleInvalid);
        await Problems.AssertAsync(await fixture.Contributor.Client.PatchAsJsonAsync($"{path}/{created.Id}", new { paused = false }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var resumed = await ProjectApi.ReadAsync<PipelineScheduleDto>(await fixture.Owner.Client.PatchAsJsonAsync($"{path}/{created.Id}", new { paused = false }, Ct));
        Assert.Equal((false, new DateTimeOffset(2030, 12, 9, 6, 0, 0, TimeSpan.Zero)), (resumed.Paused, resumed.NextFireAt)); // no catch-up of the paused fire

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.DeleteAsync($"{path}/{created.Id}", Ct)).StatusCode);
        await Problems.AssertAsync(await fixture.Owner.Client.DeleteAsync($"{path}/{created.Id}", Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR337_the_spec_vector_starts_a_run_a_repeated_delivery_is_WEBHOOK_REPLAY_and_a_301_second_old_timestamp_is_refused()
    {
        // Signed-in setup runs at the real time; only the anonymous hook calls run at the vector's time (a year earlier,
        // when no session is valid).
        var clock = new TriggerClock(DateTimeOffset.UtcNow);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var webhook = await CreateWebhookAsync(fixture);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var protector = app.Services.GetRequiredService<ISecretProtector>();
            await db.PipelineWebhooks.Where(w => w.Id == webhook.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Secret, protector.Protect(VectorSecret)), Ct);
        });
        clock.Set(DateTimeOffset.FromUnixTimeSeconds(VectorTimestamp));
        using var anonymous = app.CreateDefaultClient(TevrinApiFactory.BaseUri);
        var delivery = Guid.NewGuid();

        var accepted = await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(
            await PostHookAsync(anonymous, webhook.Id.ToString(), VectorBody, $"t={VectorTimestamp},v1={VectorV1}", delivery), HttpStatusCode.Accepted);
        var run = Assert.Single(await RunsAsync(app, fixture.Project.Id));
        Assert.Equal((run.Id, FixtureTriggersPack.WebhookPipeline), (accepted.RunId, run.PipelineId));
        var input = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.PipelineRunId == run.Id && a.Type == "payload", Ct));
        Assert.Equal(1, JsonDocument.Parse(input.Content).RootElement.GetProperty("a").GetInt32());

        await Problems.AssertAsync(
            await PostHookAsync(anonymous, webhook.Id.ToString(), VectorBody, $"t={VectorTimestamp},v1={VectorV1}", delivery),
            HttpStatusCode.Conflict, ErrorCodes.WebhookReplay);

        var stale = VectorTimestamp - 301;
        await Problems.AssertAsync(
            await PostHookAsync(anonymous, webhook.Id.ToString(), VectorBody, PipelineWebhookSignature.Header(VectorSecret, stale, Encoding.UTF8.GetBytes(VectorBody)), Guid.NewGuid()),
            HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);

        // Unknown and malformed ids look the same as a bad signature.
        var signed = PipelineWebhookSignature.Header(VectorSecret, VectorTimestamp, Encoding.UTF8.GetBytes(VectorBody));
        await Problems.AssertAsync(await PostHookAsync(anonymous, Guid.NewGuid().ToString(), VectorBody, signed, Guid.NewGuid()), HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);
        await Problems.AssertAsync(await PostHookAsync(anonymous, "not-an-id", VectorBody, signed, Guid.NewGuid()), HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);
        await Problems.AssertAsync(await PostHookAsync(anonymous, webhook.Id.ToString(), VectorBody, null, Guid.NewGuid()), HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);

        // A body that fails the input's schema.
        const string bad = "{\"a\":\"x\"}";
        await Problems.AssertAsync(
            await PostHookAsync(anonymous, webhook.Id.ToString(), bad, PipelineWebhookSignature.Header(VectorSecret, VectorTimestamp, Encoding.UTF8.GetBytes(bad)), Guid.NewGuid()),
            HttpStatusCode.BadRequest, ErrorCodes.InputInvalid);

        var deliveries = await ProjectApi.WithDbAsync(app, db => db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == webhook.Id).ToListAsync(Ct));
        Assert.Equal(2, deliveries.Count);
        var started = Assert.Single(deliveries, d => d.DeliveryId == delivery);
        Assert.Equal((WebhookDeliveryStatuses.Started, (Guid?)run.Id), (started.Status, started.RunId));
        Assert.Single(deliveries, d => d.Status == WebhookDeliveryStatuses.Rejected);
    }

    [Fact]
    public async Task FR337_a_rotated_secret_works_for_24_hours_then_fails_and_secrets_are_never_listed()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var created = await CreateWebhookAsync(fixture);
        Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
        Assert.Equal($"/api/hooks/pipelines/{created.Id}", new Uri(created.Url).AbsolutePath);

        var listed = await fixture.Viewer.Client.GetStringAsync($"{fixture.Path}/pipeline-webhooks", Ct);
        Assert.DoesNotContain(created.Secret, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", listed, StringComparison.OrdinalIgnoreCase);
        using var anonymous = app.CreateDefaultClient(TevrinApiFactory.BaseUri);

        var first = await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(await SignedAsync(anonymous, clock, created.Id, created.Secret, "{\"a\":1}"), HttpStatusCode.Accepted);

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsync($"{fixture.Path}/pipeline-webhooks/{created.Id}/rotate", null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var rotated = await ProjectApi.ReadAsync<PipelineWebhookSecretDto>(
            await fixture.Owner.Client.PostAsync($"{fixture.Path}/pipeline-webhooks/{created.Id}/rotate", null, Ct));
        Assert.NotEqual(created.Secret, rotated.Secret);
        Assert.DoesNotContain(rotated.Secret, await fixture.Owner.Client.GetStringAsync($"{fixture.Path}/pipeline-webhooks", Ct), StringComparison.Ordinal);

        // 23 hours on, the old secret still works; the run is active, so the body is submitted to it (on_active: submit).
        clock.Advance(TimeSpan.FromHours(23));
        var submitted = await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(
            await SignedAsync(anonymous, clock, created.Id, created.Secret, "{\"a\":2}"), HttpStatusCode.Accepted);
        Assert.Equal(first.RunId, submitted.RunId);
        Assert.Equal(2, await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().CountAsync(a => a.PipelineRunId == first.RunId && a.Type == "payload", Ct)));

        // After 24 hours the old secret fails and the new one works.
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await Problems.AssertAsync(
            await SignedAsync(anonymous, clock, created.Id, created.Secret, "{\"a\":3}"), HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);
        await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(await SignedAsync(anonymous, clock, created.Id, rotated.Secret, "{\"a\":3}"), HttpStatusCode.Accepted);

        // Deleted: calls look like a bad signature.
        await Problems.AssertAsync(
            await fixture.Contributor.Client.DeleteAsync($"{fixture.Path}/pipeline-webhooks/{created.Id}", Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.DeleteAsync($"{fixture.Path}/pipeline-webhooks/{created.Id}", Ct)).StatusCode);
        await Problems.AssertAsync(
            await SignedAsync(anonymous, clock, created.Id, rotated.Secret, "{\"a\":4}"), HttpStatusCode.Unauthorized, ErrorCodes.WebhookSignatureInvalid);
    }

    [Fact]
    public async Task FR337_webhook_creation_needs_the_trigger_and_an_Owner_and_old_deliveries_are_pruned()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var path = $"{fixture.Path}/pipeline-webhooks";
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, new { pipelineId = FixtureTriggersPack.ScheduledPipeline }, Ct), HttpStatusCode.Conflict, ErrorCodes.TriggerNotSupported);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, new { pipelineId = FixtureLinearPack.PipelineId }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(path, new { pipelineId = FixtureTriggersPack.WebhookPipeline }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var created = await CreateWebhookAsync(fixture);
        using var anonymous = app.CreateDefaultClient(TevrinApiFactory.BaseUri);
        await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(await SignedAsync(anonymous, clock, created.Id, created.Secret, "{\"a\":1}"), HttpStatusCode.Accepted);

        // A newer delivery 29 days later stays; after 31 days only the first is past the 30-day retention.
        clock.Advance(TimeSpan.FromDays(29));
        await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(await SignedAsync(anonymous, clock, created.Id, created.Secret, "{\"a\":2}"), HttpStatusCode.Accepted);
        clock.Advance(TimeSpan.FromDays(2));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // Other tests' deliveries share the database, so only this webhook's rows are checked.
            await scope.ServiceProvider.GetRequiredService<PipelineWebhooks>().PruneDeliveriesAsync(Ct);
        }

        var left = await ProjectApi.WithDbAsync(app, db => db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == created.Id).ToListAsync(Ct));
        var kept = Assert.Single(left);
        Assert.Equal(clock.GetUtcNow() - TimeSpan.FromDays(2), kept.ReceivedAt);
    }

    [Fact]
    public async Task FR336_a_London_schedule_created_in_summer_stores_and_fires_at_0500Z()
    {
        // BST (UTC+1): next_fire_at must be written as UTC, or PostgreSQL refuses the timestamptz.
        // Mon 5 Jul 2027 (BST); signed in at the fake time, which never goes back before the sign-in.
        var clock = new TriggerClock(new DateTimeOffset(2027, 7, 5, 4, 59, 0, TimeSpan.Zero));
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var created = await ProjectApi.ReadAsync<PipelineScheduleDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-schedules", Schedule(), Ct), HttpStatusCode.Created);
        Assert.Equal(new DateTimeOffset(2027, 7, 5, 5, 0, 0, TimeSpan.Zero), created.NextFireAt);

        var patched = await ProjectApi.ReadAsync<PipelineScheduleDto>(
            await fixture.Owner.Client.PatchAsJsonAsync($"{fixture.Path}/pipeline-schedules/{created.Id}", new { cron = "30 6 * * 1" }, Ct));
        Assert.Equal(new DateTimeOffset(2027, 7, 5, 5, 30, 0, TimeSpan.Zero), patched.NextFireAt);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await QueueDueAsync(app, fixture.Project.Id));
        await RunFiresAsync(app, fixture.Project.Id);
        Assert.Single(await RunsAsync(app, fixture.Project.Id));
        var moved = Assert.Single(await fixture.Owner.Client.GetFromJsonAsync<List<PipelineScheduleDto>>($"{fixture.Path}/pipeline-schedules", Ct) ?? []);
        Assert.Equal(new DateTimeOffset(2027, 7, 12, 5, 30, 0, TimeSpan.Zero), moved.NextFireAt);
    }

    private static async Task<CreatedPipelineWebhookDto> CreateWebhookAsync(ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<CreatedPipelineWebhookDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-webhooks", new { pipelineId = FixtureTriggersPack.WebhookPipeline }, Ct),
            HttpStatusCode.Created);

    private static Task<HttpResponseMessage> SignedAsync(HttpClient client, TimeProvider clock, Guid webhookId, string secret, string body) =>
        PostHookAsync(client, webhookId.ToString(), body,
            PipelineWebhookSignature.Header(secret, clock.GetUtcNow().ToUnixTimeSeconds(), Encoding.UTF8.GetBytes(body)), Guid.NewGuid());

    private static async Task<HttpResponseMessage> PostHookAsync(HttpClient client, string webhookId, string body, string? signature, Guid delivery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/pipelines/{webhookId}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) { Headers = { { "Content-Type", "application/json" } } },
        };
        if (signature is not null)
        {
            request.Headers.Add(PipelineWebhookSignature.SignatureHeader, signature);
        }

        request.Headers.Add(PipelineWebhookSignature.DeliveryHeader, delivery.ToString());
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Runs one schedule sweep; returns how many fires it queued for <paramref name="projectId"/> (other tests'
    /// schedules share the database).</summary>
    private static async Task<int> QueueDueAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var before = await FireJobCountAsync(app, projectId);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PipelineSchedules>().QueueDueFiresAsync(Ct);
        }

        return await FireJobCountAsync(app, projectId) - before;
    }

    private static Task<int> FireJobCountAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().CountAsync(j => j.ProjectId == projectId && j.Type == EngineJobTypes.TriggerFire, Ct));

    private static async Task RunFiresAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var queued = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.TriggerFire && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(Ct));
        foreach (var job in queued)
        {
            await AgentJobs.RunAsync(app, job);
        }
    }

    /// <summary>The project's runs of the trigger fixtures (a new project also has its SDLC run, which isn't counted).</summary>
    private static Task<List<PipelineRun>> RunsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                && (r.PipelineId == FixtureTriggersPack.ScheduledPipeline || r.PipelineId == FixtureTriggersPack.WebhookPipeline))
            .ToListAsync(Ct));

    /// <summary>The project's schedules, read from the database (the clock may have moved past the session's lifetime).</summary>
    private static Task<List<PipelineSchedule>> SchedulesAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineSchedules.AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync(Ct));

    private static async Task<List<JsonElement>> FiredEventsAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == TriggerEventTypes.ScheduleFired)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(Ct));
        return [.. events.Select(e => JsonDocument.Parse(e.Payload).RootElement.Clone())];
    }
}
