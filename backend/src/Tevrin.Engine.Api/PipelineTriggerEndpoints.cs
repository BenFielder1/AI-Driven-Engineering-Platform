using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Triggers;
using Tevrin.Engine.Runtime.Notifications;
using Tevrin.Engine.Runtime.Triggers;

namespace Tevrin.Engine.Api;

/// <summary>FR-336: a project schedule (cron evaluated in <see cref="Timezone"/>, §4.71).</summary>
public sealed record PipelineScheduleDto(
    Guid Id,
    string PipelineId,
    string Cron,
    string Timezone,
    JsonElement Inputs,
    bool Paused,
    DateTimeOffset NextFireAt,
    DateTimeOffset? LastFireAt);

/// <summary>FR-336: schedule a pipeline that declares <c>trigger: { kind: schedule }</c>.</summary>
public sealed record CreatePipelineScheduleRequest
{
    public string PipelineId { get; init; } = string.Empty;

    /// <summary>Five fields: minute hour day-of-month month day-of-week.</summary>
    public string Cron { get; init; } = string.Empty;

    /// <summary>An IANA timezone, e.g. <c>Europe/London</c>.</summary>
    public string Timezone { get; init; } = string.Empty;

    /// <summary>Input artifacts each fire starts its run with, by key.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Inputs { get; init; }
}

/// <summary>FR-336: pause or resume a schedule, or change its cron, timezone or inputs (omitted fields keep their value).</summary>
public sealed record UpdatePipelineScheduleRequest
{
    public bool? Paused { get; init; }

    public string? Cron { get; init; }

    public string? Timezone { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Inputs { get; init; }
}

/// <summary>FR-337: a project webhook (never its secret).</summary>
public sealed record PipelineWebhookDto(Guid Id, string PipelineId, string Url, DateTimeOffset? PreviousSecretExpiresAt);

/// <summary>FR-337: a new webhook with its secret, shown only now.</summary>
public sealed record CreatedPipelineWebhookDto(Guid Id, string Url, string Secret);

/// <summary>FR-337: create a webhook for a pipeline that declares <c>trigger: { kind: webhook, input }</c>.</summary>
public sealed record CreatePipelineWebhookRequest
{
    public string PipelineId { get; init; } = string.Empty;
}

/// <summary>FR-337: a rotated webhook's new secret, shown only now (the old one stays valid for 24 hours).</summary>
public sealed record PipelineWebhookSecretDto(string Secret);

/// <summary>FR-337: the run a webhook call started or submitted to.</summary>
public sealed record PipelineWebhookAcceptedDto(Guid RunId);

/// <summary>FR-336, FR-337: schedule and webhook triggers (SPEC §6).</summary>
public static partial class PipelineRunEndpoints
{
    private const string TriggerTag = "Pipeline triggers";
    private const int MaxPipelineIdLength = 100;

    private static void MapTriggers(IEndpointRouteBuilder app)
    {
        var schedules = app.MapGroup("/api/projects/{id:guid}/pipeline-schedules")
            .WithTags(TriggerTag)
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        schedules.MapGet("/", ListSchedules).WithName("ListPipelineSchedules")
            .WithSummary("FR-336: the project's pipeline schedules (Viewer).")
            .Produces<IReadOnlyList<PipelineScheduleDto>>();
        schedules.MapPost("/", CreateSchedule).WithName("CreatePipelineSchedule")
            .WithSummary("FR-336: schedule a pipeline that declares trigger kind schedule (Owner). 400 SCHEDULE_INVALID (cron or timezone), VALIDATION or INPUT_INVALID; 403 FORBIDDEN; 404 NOT_FOUND (pipeline); 409 TRIGGER_NOT_SUPPORTED or RUN_CONTROLLED_BY_HOST.")
            .Produces<PipelineScheduleDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        schedules.MapPatch("/{scheduleId:guid}", UpdateSchedule).WithName("UpdatePipelineSchedule")
            .WithSummary("FR-336: pause, resume or change a schedule (Owner). 400 SCHEDULE_INVALID, VALIDATION or INPUT_INVALID; 403 FORBIDDEN; 404 NOT_FOUND.")
            .Produces<PipelineScheduleDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        schedules.MapDelete("/{scheduleId:guid}", DeleteSchedule).WithName("DeletePipelineSchedule")
            .WithSummary("FR-336: delete a schedule (Owner). 403 FORBIDDEN; 404 NOT_FOUND.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var webhooks = app.MapGroup("/api/projects/{id:guid}/pipeline-webhooks")
            .WithTags(TriggerTag)
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        webhooks.MapGet("/", ListWebhooks).WithName("ListPipelineWebhooks")
            .WithSummary("FR-337: the project's pipeline webhooks, without their secrets (Viewer).")
            .Produces<IReadOnlyList<PipelineWebhookDto>>();
        webhooks.MapPost("/", CreateWebhook).WithName("CreatePipelineWebhook")
            .WithSummary("FR-337: create a webhook for a pipeline that declares trigger kind webhook (Owner); the secret is shown only in this response. 400 VALIDATION; 403 FORBIDDEN; 404 NOT_FOUND (pipeline); 409 TRIGGER_NOT_SUPPORTED or RUN_CONTROLLED_BY_HOST.")
            .Produces<CreatedPipelineWebhookDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        webhooks.MapPost("/{webhookId:guid}/rotate", RotateWebhook).WithName("RotatePipelineWebhookSecret")
            .WithSummary("FR-337: a new secret, shown only in this response; the old one stays valid for 24 hours (Owner). 403 FORBIDDEN; 404 NOT_FOUND; 409 CONFLICT.")
            .Produces<PipelineWebhookSecretDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        webhooks.MapDelete("/{webhookId:guid}", DeleteWebhook).WithName("DeletePipelineWebhook")
            .WithSummary("FR-337: delete a webhook (Owner). 403 FORBIDDEN; 404 NOT_FOUND.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// FR-337, §4.72: maps <c>POST /api/hooks/pipelines/{webhookId}</c>, authenticated only by its signature (anonymous, no
    /// session). The host applies its own conventions to the returned builder (e.g. its antiforgery exemption: the route
    /// takes no cookie session's authority).
    /// </summary>
    public static RouteHandlerBuilder MapPipelineHookEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.MapPost("/api/hooks/pipelines/{webhookId}", ReceiveHook)
            .WithTags(TriggerTag)
            .AllowAnonymous()
            .WithName("ReceivePipelineWebhook")
            .WithSummary("FR-337, §4.72: a signed webhook call (X-Tevrin-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, \"<t>.<raw body>\")>, X-Tevrin-Delivery: <uuid>). Starts a run with the JSON body as the trigger's input, or submits it to the active run (on_active: submit). 401 WEBHOOK_SIGNATURE_INVALID (also for an unknown webhook); 409 WEBHOOK_REPLAY or RUN_ALREADY_ACTIVE; 400 INPUT_INVALID or VALIDATION (delivery id); 413 VALIDATION (body over 256 KB).")
            .Accepts<JsonElement>("application/json")
            .Produces<PipelineWebhookAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineScheduleDto>>, ProblemHttpResult>> ListSchedules(
        Guid id, IRunControlAccess access, PipelineSchedules schedules, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ViewTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var rows = await schedules.ListAsync(id, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PipelineScheduleDto>>([.. rows.Select(ToDto)]);
    }

    private static async Task<Results<Created<PipelineScheduleDto>, ProblemHttpResult>> CreateSchedule(
        Guid id, CreatePipelineScheduleRequest? request, IRunControlAccess access, PipelineSchedules schedules, IEngineProblems problems,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PipelineId) || request.PipelineId.Length > MaxPipelineIdLength)
        {
            return problems.FromError(PipelineRequests.Invalid("pipelineId", "pipelineId is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var created = await schedules.CreateAsync(
            new CreateSchedule(id, request.PipelineId, request.Cron ?? string.Empty, (request.Timezone ?? string.Empty).Trim(), request.Inputs, access.UserId),
            cancellationToken);
        return created.IsFailure
            ? problems.FromError(created.Error!)
            : TypedResults.Created($"/api/projects/{id}/pipeline-schedules/{created.Value.Id}", ToDto(created.Value));
    }

    private static async Task<Results<Ok<PipelineScheduleDto>, ProblemHttpResult>> UpdateSchedule(
        Guid id, Guid scheduleId, UpdatePipelineScheduleRequest? request, IRunControlAccess access, PipelineSchedules schedules,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return problems.FromError(PipelineRequests.Invalid("body", "A request body is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        if (await schedules.FindAsync(id, scheduleId, cancellationToken) is not { } schedule)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The schedule was not found."));
        }

        var updated = await schedules.UpdateAsync(schedule, new UpdateSchedule(request.Paused, request.Cron, request.Timezone?.Trim(), request.Inputs), cancellationToken);
        return updated.IsFailure ? problems.FromError(updated.Error!) : TypedResults.Ok(ToDto(updated.Value));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSchedule(
        Guid id, Guid scheduleId, IRunControlAccess access, PipelineSchedules schedules, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        if (await schedules.FindAsync(id, scheduleId, cancellationToken) is not { } schedule)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The schedule was not found."));
        }

        await schedules.DeleteAsync(schedule, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<PipelineWebhookDto>>, ProblemHttpResult>> ListWebhooks(
        Guid id, IRunControlAccess access, PipelineWebhooks webhooks, IOptions<AppOptions> app, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ViewTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var rows = await webhooks.ListAsync(id, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PipelineWebhookDto>>(
            [.. rows.Select(w => new PipelineWebhookDto(w.Id, w.PipelineId, HookUrl(app.Value, w.Id), w.PreviousSecretExpiresAt))]);
    }

    private static async Task<Results<Created<CreatedPipelineWebhookDto>, ProblemHttpResult>> CreateWebhook(
        Guid id, CreatePipelineWebhookRequest? request, IRunControlAccess access, PipelineWebhooks webhooks, IOptions<AppOptions> app,
        IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PipelineId) || request.PipelineId.Length > MaxPipelineIdLength)
        {
            return problems.FromError(PipelineRequests.Invalid("pipelineId", "pipelineId is required."));
        }

        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        var created = await webhooks.CreateAsync(id, request.PipelineId, access.UserId, cancellationToken);
        if (created.IsFailure)
        {
            return problems.FromError(created.Error!);
        }

        var webhook = created.Value.Webhook;
        return TypedResults.Created(
            $"/api/projects/{id}/pipeline-webhooks/{webhook.Id}",
            new CreatedPipelineWebhookDto(webhook.Id, HookUrl(app.Value, webhook.Id), created.Value.Secret));
    }

    private static async Task<Results<Ok<PipelineWebhookSecretDto>, ProblemHttpResult>> RotateWebhook(
        Guid id, Guid webhookId, IRunControlAccess access, PipelineWebhooks webhooks, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        if (await webhooks.FindAsync(id, webhookId, cancellationToken) is not { } webhook)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The webhook was not found."));
        }

        return TypedResults.Ok(new PipelineWebhookSecretDto(await webhooks.RotateAsync(webhook, cancellationToken)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteWebhook(
        Guid id, Guid webhookId, IRunControlAccess access, PipelineWebhooks webhooks, IEngineProblems problems, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(id, RunControlAction.ManageTriggers, cancellationToken) is { } denied)
        {
            return problems.FromError(denied);
        }

        if (await webhooks.FindAsync(id, webhookId, cancellationToken) is not { } webhook)
        {
            return problems.FromError(new DomainError(EngineErrorCodes.NotFound, "The webhook was not found."));
        }

        await webhooks.DeleteAsync(webhook, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<PipelineWebhookAcceptedDto>, ProblemHttpResult>> ReceiveHook(
        string webhookId, HttpContext http, PipelineWebhooks webhooks, IEngineProblems problems, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(http, PipelineWebhooks.MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            var tooLarge = problems.FromError(PipelineRequests.Invalid("body", $"The body must be at most {PipelineWebhooks.MaxBodyBytes / 1024} KB.")).ProblemDetails;
            tooLarge.Status = StatusCodes.Status413PayloadTooLarge;
            return TypedResults.Problem(tooLarge);
        }

        if (!Guid.TryParse(webhookId, out var id))
        {
            // As for an unknown id: nothing tells a caller which ids exist.
            return problems.FromError(new DomainError(EngineErrorCodes.WebhookSignatureInvalid, "The webhook signature is not valid."));
        }

        var headers = http.Request.Headers;
        var received = await webhooks.ReceiveAsync(
            new WebhookCall(id, headers[PipelineWebhookSignature.SignatureHeader].ToString(), headers[PipelineWebhookSignature.DeliveryHeader].ToString(), body),
            cancellationToken);
        return received.IsFailure
            ? problems.FromError(received.Error!)
            : TypedResults.Accepted($"/api/pipeline-runs/{received.Value.RunId}", new PipelineWebhookAcceptedDto(received.Value.RunId));
    }

    /// <summary>The raw body, or <c>null</c> when it is over <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext http, int limit, CancellationToken cancellationToken)
    {
        if (http.Request.ContentLength > limit)
        {
            return null;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit + 1L;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string HookUrl(AppOptions app, Guid webhookId) => app.Absolute($"/api/hooks/pipelines/{webhookId}");

    private static PipelineScheduleDto ToDto(PipelineSchedule s)
    {
        using var inputs = JsonDocument.Parse(string.IsNullOrWhiteSpace(s.Inputs) ? "{}" : s.Inputs);
        return new PipelineScheduleDto(s.Id, s.PipelineId, s.Cron, s.Timezone, inputs.RootElement.Clone(), s.Paused, s.NextFireAt, s.LastFireAt);
    }
}
