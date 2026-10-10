using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Notifications;
using Aide.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Notifications;

/// <summary>
/// A notification as a channel sends it: absolute links, and the gate's one-click approve/reject URLs when it has them
/// (FR-48). <see cref="DeliveryId"/> is the delivery row's id: stable across retries, so receivers can de-duplicate.
/// </summary>
public sealed record ChannelMessage(
    Guid DeliveryId,
    string Key,
    NotificationType Type,
    Guid ProjectId,
    string ProjectName,
    string Title,
    string Body,
    string Link,
    string? ApproveUrl,
    string? RejectUrl,
    DateTimeOffset Timestamp);

/// <summary>The outcome of one send; <see cref="Error"/> is short, safe to store and log (never a URL or secret).</summary>
public sealed record ChannelSendResult(DeliveryAttemptResult Result, string? Error)
{
    public static ChannelSendResult Sent { get; } = new(DeliveryAttemptResult.Sent, null);
}

/// <summary>Sends to one kind of project channel (FR-47).</summary>
public interface INotificationChannelSender
{
    NotificationChannelType Type { get; }

    Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken);
}

/// <summary>The SSRF-guarded HttpClient of the Slack, Teams and webhook channels (NFR-SEC).</summary>
public static class NotificationHttp
{
    public const string ClientName = "notifications";

    /// <summary>Marks requests so tracing leaves them out: Slack and Teams URLs are credentials.</summary>
    public static readonly HttpRequestOptionsKey<bool> NotTraced = new("aide.notification");

    /// <summary>Response bodies are never read; this caps anything buffered by accident.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    public static bool IsNotificationRequest(HttpRequestMessage request) =>
        request is not null && request.Options.TryGetValue(NotTraced, out var marked) && marked;

    internal static void AddClient(IServiceCollection services)
    {
        services.AddHttpClient(ClientName, (sp, client) =>
            {
                client.Timeout = sp.GetRequiredService<IOptions<NotificationOptions>>().Value.RequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("aide-platform");
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<NotificationOptions>>().Value;
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    MaxResponseHeadersLength = 64,
                    // New connections are vetted again, so a DNS change is re-checked within a minute.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                };
                if (!options.AllowPrivateTargets)
                {
                    handler.ConnectCallback = new SafeTargetConnector().ConnectAsync;
                }

                return handler;
            })
            // The default handler logs request URIs, and Slack/Teams URLs are credentials.
            .RemoveAllLoggers();
    }
}

/// <summary>FR-47 email channel: one plain-text email to the channel's address. Without SMTP settings it is skipped.</summary>
public sealed partial class EmailChannelSender(IEmailTransport transport, ILogger<EmailChannelSender> logger) : INotificationChannelSender
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (!transport.IsConfigured)
        {
            LogSmtpMissing(logger, message.Key, channel.Id);
            return new ChannelSendResult(DeliveryAttemptResult.Skipped, "SMTP is not configured.");
        }

        var text = new StringBuilder()
            .Append(message.Body).Append("\n\n")
            .Append("Open in AIDE: ").Append(message.Link).Append('\n');
        if (message.ApproveUrl is { } approve && message.RejectUrl is { } reject)
        {
            text.Append("\nApprove: ").Append(approve).Append('\n')
                .Append("Reject: ").Append(reject).Append('\n')
                .Append("(Each link opens a confirmation page, works once and expires after 72 hours. Sign in as an Owner of the project.)\n");
        }

        var (result, error) = await transport.SendAsync(new EmailMessage(channel.Target, $"[AIDE] {message.Title}", text.ToString()), cancellationToken);
        return result switch
        {
            EmailSendResult.Sent => ChannelSendResult.Sent,
            EmailSendResult.NotConfigured => new ChannelSendResult(DeliveryAttemptResult.Skipped, error),
            EmailSendResult.PermanentFailure => new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, error),
            _ => new ChannelSendResult(DeliveryAttemptResult.TransientFailure, error),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email notification {NotificationKey} for channel {ChannelId} skipped: SMTP is not configured (Smtp:Host). Other channels are unaffected.")]
    private static partial void LogSmtpMissing(ILogger logger, string notificationKey, Guid channelId);
}

/// <summary>Shared POST logic of the HTTP channels: SSRF-guarded client, no redirects, status classification.</summary>
public abstract class HttpChannelSender(IHttpClientFactory clients, ISecretProtector secrets) : INotificationChannelSender
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public abstract NotificationChannelType Type { get; }

    public abstract Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken);

    /// <summary>The channel's decrypted secret (Slack/Teams URL, webhook HMAC key), or <c>null</c>.</summary>
    protected string? Secret(NotificationChannelConfig channel) =>
        channel?.SecretEncrypted is { } encrypted && secrets.TryUnprotect(encrypted, out var plain) && !string.IsNullOrWhiteSpace(plain)
            ? plain
            : null;

    protected static Uri? HttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    protected async Task<ChannelSendResult> PostAsync(Uri url, byte[] body, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Options.Set(NotificationHttp.NotTraced, true);
        headers?.Invoke(request.Headers);
        var client = clients.CreateClient(NotificationHttp.ClientName);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            var result = DeliveryPolicy.ForHttpStatus(status);
            return result == DeliveryAttemptResult.Sent
                ? ChannelSendResult.Sent
                : new ChannelSendResult(result, $"{url.Host} answered HTTP {status.ToString(CultureInfo.InvariantCulture)}.");
        }
        catch (HttpRequestException ex) when (Blocked(ex))
        {
            return new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, $"{url.Host} resolves to an address notifications may not reach.");
        }
        catch (HttpRequestException ex)
        {
            return new ChannelSendResult(DeliveryAttemptResult.TransientFailure, $"{url.Host}: {ex.HttpRequestError}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChannelSendResult(DeliveryAttemptResult.TransientFailure, $"{url.Host} timed out.");
        }
    }

    private static bool Blocked(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is BlockedTargetException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>FR-47 Slack incoming webhook: a mrkdwn message with the title, body and links.</summary>
public sealed class SlackChannelSender(IHttpClientFactory clients, ISecretProtector secrets) : HttpChannelSender(clients, secrets)
{
    public override NotificationChannelType Type => NotificationChannelType.Slack;

    public override Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (HttpsUrl(Secret(channel)) is not { } url)
        {
            return Task.FromResult(new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "The Slack channel has no usable webhook URL."));
        }

        var text = new StringBuilder()
            .Append('*').Append(Mrkdwn(message.Title)).Append("*\n")
            .Append(Mrkdwn(message.Body)).Append('\n')
            .Append('<').Append(message.Link).Append("|Open in AIDE>");
        if (message.ApproveUrl is { } approve && message.RejectUrl is { } reject)
        {
            text.Append(" · <").Append(approve).Append("|Approve> · <").Append(reject).Append("|Reject>");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new { text = text.ToString() }, Json);
        return PostAsync(url, body, null, cancellationToken);
    }

    /// <summary>Slack mrkdwn control characters (&amp;, &lt;, &gt;) escaped, so names can't inject links or mentions.</summary>
    public static string Mrkdwn(string value) =>
        (value ?? string.Empty).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>FR-47 Teams incoming webhook (Workflows or legacy connector): an Adaptive Card with the title, body and links.</summary>
public sealed class TeamsChannelSender(IHttpClientFactory clients, ISecretProtector secrets) : HttpChannelSender(clients, secrets)
{
    public override NotificationChannelType Type => NotificationChannelType.Teams;

    public override Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (HttpsUrl(Secret(channel)) is not { } url)
        {
            return Task.FromResult(new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "The Teams channel has no usable webhook URL."));
        }

        var actions = new List<object> { new { type = "Action.OpenUrl", title = "Open in AIDE", url = message.Link } };
        if (message.ApproveUrl is { } approve && message.RejectUrl is { } reject)
        {
            actions.Add(new { type = "Action.OpenUrl", title = "Approve", url = approve });
            actions.Add(new { type = "Action.OpenUrl", title = "Reject", url = reject });
        }

        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = new object[]
            {
                new { type = "TextBlock", text = message.Title, weight = "Bolder", size = "Medium", wrap = true },
                new { type = "TextBlock", text = message.Body, wrap = true },
            },
            ["actions"] = actions,
        };
        var payload = new
        {
            type = "message",
            summary = message.Title,
            attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } },
        };
        return PostAsync(url, JsonSerializer.SerializeToUtf8Bytes(payload, Json), null, cancellationToken);
    }
}

/// <summary>
/// FR-47 generic webhook: a JSON body signed with the channel secret (<see cref="WebhookSignature"/>), with
/// <c>X-AIDE-Timestamp</c>, <c>X-AIDE-Delivery</c> / <c>Idempotency-Key</c> (stable across retries) and <c>X-AIDE-Event</c>.
/// </summary>
public sealed class WebhookChannelSender(IHttpClientFactory clients, ISecretProtector secrets) : HttpChannelSender(clients, secrets)
{
    public override NotificationChannelType Type => NotificationChannelType.Webhook;

    public override Task<ChannelSendResult> SendAsync(NotificationChannelConfig channel, ChannelMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (HttpsUrl(channel.Target) is not { } url)
        {
            return Task.FromResult(new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "The webhook URL is not an https URL."));
        }

        // NFR-SEC: webhook bodies are always signed. Settings refuse a webhook without a key, so this only happens for a
        // key that can no longer be decrypted (rotated data protection keys): fail rather than send unsigned.
        if (Secret(channel) is not { } secret)
        {
            return Task.FromResult(new ChannelSendResult(DeliveryAttemptResult.PermanentFailure, "The webhook has no usable signing secret."));
        }

        var timestamp = message.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                id = message.DeliveryId,
                key = message.Key,
                type = message.Type.ToWire(),
                timestamp = message.Timestamp,
                project = new { id = message.ProjectId, name = message.ProjectName },
                title = message.Title,
                body = message.Body,
                link = message.Link,
                approveUrl = message.ApproveUrl,
                rejectUrl = message.RejectUrl,
            },
            Json);
        return PostAsync(
            url,
            body,
            headers =>
            {
                headers.Add(WebhookSignature.TimestampHeaderName, timestamp);
                headers.Add(WebhookSignature.DeliveryHeaderName, message.DeliveryId.ToString("D"));
                headers.Add(WebhookSignature.IdempotencyHeaderName, message.DeliveryId.ToString("D"));
                headers.Add(WebhookSignature.EventHeaderName, message.Type.ToWire());
                headers.Add(WebhookSignature.HeaderName, WebhookSignature.Compute(secret, body));
            },
            cancellationToken);
    }
}
