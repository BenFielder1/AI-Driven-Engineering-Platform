using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Domain.Triggers;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Engine.Runtime.Security;

namespace Tevrin.Engine.Runtime.Triggers;

/// <summary>A created webhook and its secret (shown once, FR-337).</summary>
public sealed record CreatedWebhook(PipelineWebhook Webhook, string Secret);

/// <summary>A webhook call (FR-337): the raw body and the two headers.</summary>
public sealed record WebhookCall(Guid WebhookId, string? Signature, string? DeliveryId, byte[] Body);

/// <summary>What a webhook call did: the run it started or submitted to.</summary>
public sealed record WebhookReceipt(Guid RunId, string Status);

/// <summary>
/// FR-337, §4.72: project webhooks. Secrets are <c>whsec_</c> + 32 random bytes (base64url), stored encrypted
/// (<see cref="ISecretProtector"/>) and returned only when created or rotated. A rotated secret stays valid for 24 hours.
/// </summary>
public sealed partial class PipelineWebhooks(
    IEngineDbContext db,
    PipelineRegistry registry,
    PipelineRunStarter starter,
    EngineRunControl control,
    IProjectApprovalModes projects,
    ISecretProtector protector,
    TimeProvider time,
    ILogger<PipelineWebhooks> logger)
{
    public const string SecretPrefix = "whsec_";

    /// <summary>The largest body a call may send.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public Task<List<PipelineWebhook>> ListAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.PipelineWebhooks.AsNoTracking().Where(w => w.ProjectId == projectId).OrderBy(w => w.Id).ToListAsync(cancellationToken);

    public Task<PipelineWebhook?> FindAsync(Guid projectId, Guid webhookId, CancellationToken cancellationToken) =>
        db.PipelineWebhooks.FirstOrDefaultAsync(w => w.Id == webhookId && w.ProjectId == projectId, cancellationToken);

    /// <summary>A new, strong secret: <c>whsec_</c> + 32 random bytes, base64url.</summary>
    public static string NewSecret() => SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public async Task<Result<CreatedWebhook>> CreateAsync(Guid projectId, string pipelineId, Guid? userId, CancellationToken cancellationToken)
    {
        var pipeline = TriggerChecks.Pipeline(registry, pipelineId, TriggerKind.Webhook);
        if (pipeline.IsFailure)
        {
            return Result.Failure<CreatedWebhook>(pipeline.Error!);
        }

        var secret = NewSecret();
        var webhook = new PipelineWebhook
        {
            ProjectId = projectId,
            PipelineId = pipelineId,
            Secret = protector.Protect(secret),
            CreatedBy = userId,
        };
        db.PipelineWebhooks.Add(webhook);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(new CreatedWebhook(webhook, secret));
    }

    /// <summary>A new secret for a tracked webhook; the current one becomes the previous, valid for 24 hours
    /// (concurrent rotations: <c>CONFLICT</c> by the xmin token, from the save).</summary>
    public async Task<string> RotateAsync(PipelineWebhook webhook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        var secret = NewSecret();
        webhook.PreviousSecret = webhook.Secret;
        webhook.PreviousSecretExpiresAt = time.GetUtcNow() + PipelineWebhookSignature.RotationOverlap;
        webhook.Secret = protector.Protect(secret);
        await db.SaveChangesAsync(cancellationToken);
        return secret;
    }

    public async Task DeleteAsync(PipelineWebhook webhook, CancellationToken cancellationToken)
    {
        db.PipelineWebhooks.Remove(webhook);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// FR-337, §4.72: one call. In order: the signature (any failure, an unknown webhook included, is
    /// <c>WEBHOOK_SIGNATURE_INVALID</c>, so ids can't be probed); the delivery id (<c>VALIDATION</c> when missing); the replay
    /// claim (<c>WEBHOOK_REPLAY</c>); the body as the trigger's input (<c>INPUT_INVALID</c>); then a new run, or with
    /// <c>on_active: submit</c> a new input version on the active run (else <c>RUN_ALREADY_ACTIVE</c>). Every claimed
    /// delivery is logged with its status and run.
    /// </summary>
    public async Task<Result<WebhookReceipt>> ReceiveAsync(WebhookCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var now = time.GetUtcNow();
        var webhook = await db.PipelineWebhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == call.WebhookId, cancellationToken);
        if (webhook is null || !Verifies(webhook, call, now))
        {
            LogRefused(logger, call.WebhookId);
            return Result.Failure<WebhookReceipt>(new DomainError(EngineErrorCodes.WebhookSignatureInvalid, "The webhook signature is not valid."));
        }

        if (!Guid.TryParse(call.DeliveryId, out var deliveryId) || deliveryId == Guid.Empty)
        {
            return Result.Failure<WebhookReceipt>(new DomainError(EngineErrorCodes.Validation, $"The {PipelineWebhookSignature.DeliveryHeader} header must be a UUID.")
            {
                Field = PipelineWebhookSignature.DeliveryHeader,
            });
        }

        if (!await ClaimDeliveryAsync(webhook.Id, deliveryId, now, cancellationToken))
        {
            return Result.Failure<WebhookReceipt>(new DomainError(EngineErrorCodes.WebhookReplay, "This delivery was already received."));
        }

        Result<WebhookReceipt> handled;
        try
        {
            handled = await HandleAsync(webhook, call.Body, cancellationToken);
        }
        catch (Exception ex)
        {
            // Release the claim so the sender's retry isn't refused as a replay; best effort, then rethrow.
            db.ChangeTracker.Clear();
            await ReleaseDeliveryAsync(webhook.Id, deliveryId, ex);
            throw;
        }

        db.ChangeTracker.Clear();
        var status = handled.IsSuccess ? handled.Value.Status : WebhookDeliveryStatuses.Rejected;
        Guid? runId = handled.IsSuccess ? handled.Value.RunId : null;
        await db.WebhookDeliveries.Where(d => d.WebhookId == webhook.Id && d.DeliveryId == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.RunId, runId), cancellationToken);
        LogDelivered(logger, webhook.Id, deliveryId, status, runId);
        return handled;
    }

    /// <summary>§5 retention: deletes deliveries older than 30 days; returns how many.</summary>
    public Task<int> PruneDeliveriesAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - WebhookDeliveryRules.Retention;
        return db.WebhookDeliveries.Where(d => d.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private bool Verifies(PipelineWebhook webhook, WebhookCall call, DateTimeOffset now)
    {
        if (!protector.TryUnprotect(webhook.Secret, out var current))
        {
            return false;
        }

        string? previous = null;
        if (webhook.PreviousSecret is { } stored && protector.TryUnprotect(stored, out var plain))
        {
            previous = plain;
        }

        return PipelineWebhookSignature.Verify(
            call.Signature, call.Body, PipelineWebhookSignature.AcceptedSecrets(current, previous, webhook.PreviousSecretExpiresAt, now), now);
    }

    /// <summary>
    /// D-0.8.0-18: inserts the delivery, or reuses a row of the same (webhook, delivery id) last received 24 hours or more
    /// ago, in one statement; <c>false</c> (a replay) when the pair was received within the window. Concurrent calls with
    /// the same id: the primary key lets one through.
    /// </summary>
    private async Task<bool> ClaimDeliveryAsync(Guid webhookId, Guid deliveryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reusableBefore = now - WebhookDeliveryRules.ReplayWindow;
        var status = WebhookDeliveryStatuses.Received;
        var rows = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO webhook_deliveries (webhook_id, delivery_id, received_at, status, run_id)
            VALUES ({webhookId}, {deliveryId}, {now}, {status}, NULL)
            ON CONFLICT (webhook_id, delivery_id) DO UPDATE
            SET received_at = EXCLUDED.received_at, status = EXCLUDED.status, run_id = NULL
            WHERE webhook_deliveries.received_at <= {reusableBefore}
            """,
            cancellationToken);
        return rows == 1;
    }

    private async Task ReleaseDeliveryAsync(Guid webhookId, Guid deliveryId, Exception cause)
    {
        try
        {
            // Not the request's token: a cancelled request still releases its claim.
            await db.WebhookDeliveries
                .Where(d => d.WebhookId == webhookId && d.DeliveryId == deliveryId && d.Status == WebhookDeliveryStatuses.Received)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogReleaseFailed(logger, webhookId, deliveryId, ex);
        }

        LogHandlingFailed(logger, webhookId, deliveryId, cause);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {WebhookId} delivery {DeliveryId}: the claim couldn't be released.")]
    private static partial void LogReleaseFailed(ILogger logger, Guid webhookId, Guid deliveryId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook {WebhookId} delivery {DeliveryId} failed; the claim was released so a retry is accepted.")]
    private static partial void LogHandlingFailed(ILogger logger, Guid webhookId, Guid deliveryId, Exception exception);

    private async Task<Result<WebhookReceipt>> HandleAsync(PipelineWebhook webhook, byte[] body, CancellationToken cancellationToken)
    {
        var pipeline = TriggerChecks.Pipeline(registry, webhook.PipelineId, TriggerKind.Webhook);
        if (pipeline.IsFailure)
        {
            return Result.Failure<WebhookReceipt>(pipeline.Error!);
        }

        var trigger = pipeline.Value.Definition!.TriggerOf(TriggerKind.Webhook)!;
        var key = trigger.Input!;
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(body);
            value = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<WebhookReceipt>(new DomainError(EngineErrorCodes.InputInvalid, "The body is not valid JSON.") { Details = ["/: The body is not valid JSON."] });
        }

        if (await projects.FindAsync(webhook.ProjectId, cancellationToken) is not { } mode)
        {
            return Result.Failure<WebhookReceipt>(new DomainError(EngineErrorCodes.NotFound, "The project was not found."));
        }

        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [key] = value };
        var started = await starter.StartAsync(new StartPipelineRun(webhook.ProjectId, webhook.PipelineId, null, mode, inputs, null), cancellationToken);
        if (started.IsSuccess)
        {
            return Result.Success(new WebhookReceipt(started.Value.Id, WebhookDeliveryStatuses.Started));
        }

        if (started.Error!.Code != EngineErrorCodes.RunAlreadyActive || trigger.OnActive != TriggerOnActive.Submit)
        {
            return Result.Failure<WebhookReceipt>(started.Error);
        }

        db.ChangeTracker.Clear();
        if (await TriggerChecks.ActiveRunAsync(db, webhook.ProjectId, webhook.PipelineId, cancellationToken) is not { } run)
        {
            return Result.Failure<WebhookReceipt>(started.Error);
        }

        var submitted = await control.SubmitInputAsync(run, key, value, null, cancellationToken);
        return submitted.IsSuccess
            ? Result.Success(new WebhookReceipt(run.Id, WebhookDeliveryStatuses.Submitted))
            : Result.Failure<WebhookReceipt>(submitted.Error!);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook call to {WebhookId} refused: the signature is not valid.")]
    private static partial void LogRefused(ILogger logger, Guid webhookId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {WebhookId} delivery {DeliveryId}: {Status} (run {RunId}).")]
    private static partial void LogDelivered(ILogger logger, Guid webhookId, Guid deliveryId, string status, Guid? runId);
}
