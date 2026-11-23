using Tevrin.Engine.Domain.Notifications;
using Tevrin.Engine.Runtime.Notifications;

namespace Tevrin.Api.Features.Notifications;

/// <summary>Notification sweep settings (section <c>Notifications</c>, FR-49, FR-15, FR-155, FR-165). The channel settings
/// of the same section (<c>AllowPrivateTargets</c>, <c>RequestTimeout</c>) are the engine's
/// <see cref="NotificationChannelOptions"/>.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Run the background sweep (delivery retries and gate reminders). Tests turn it off and drive it directly.</summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>How often due delivery retries are attempted (FR-49).</summary>
    public TimeSpan DeliverySweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often pending gates are checked for reminders and overdue flags (FR-15: hourly).</summary>
    public TimeSpan GateSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the database expiry sweep job is queued (FR-155: hourly; each run reads each Supabase project once).</summary>
    public TimeSpan DatabaseExpirySweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often Jira and Linear connections are checked for a user-entered expiry within 14 days (FR-165: hourly; no provider calls).</summary>
    public TimeSpan ConnectionExpirySweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Deliveries retried per sweep.</summary>
    public int SweepBatchSize { get; set; } = 50;
}

public static class NotificationServiceCollectionExtensions
{
    /// <summary>FR-47–FR-49, FR-15: notification fan-out job, channels, delivery retries, gate timers and approval links.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddOptions<NotificationOptions>()
            .BindConfiguration(NotificationOptions.SectionName)
            .Validate(
                o => o.DeliverySweepInterval > TimeSpan.Zero && o.GateSweepInterval > TimeSpan.Zero && o.DatabaseExpirySweepInterval > TimeSpan.Zero && o.ConnectionExpirySweepInterval > TimeSpan.Zero,
                "Notifications sweep intervals must be positive.")
            .Validate(o => o.SweepBatchSize >= 1, "Notifications:SweepBatchSize must be at least 1.")
            .ValidateOnStart();
        services.AddOptions<NotificationChannelOptions>()
            .BindConfiguration(NotificationChannelOptions.SectionName)
            .Validate(o => DeliveryPolicy.IsValidSendTimeout(o.RequestTimeout), "Notifications:RequestTimeout must be positive and at most 60 seconds (well inside the delivery claim lease).")
            .ValidateOnStart();
        services.AddOptions<AppOptions>()
            .BindConfiguration(AppOptions.SectionName)
            .Validate(o => Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "App:PublicBaseUrl must be an absolute http(s) URL.")
            .ValidateOnStart();
        services.AddOptions<ApprovalOptions>().BindConfiguration(ApprovalOptions.SectionName);
        services.AddOptions<SmtpOptions>()
            .BindConfiguration(SmtpOptions.SectionName)
            .Validate(o => DeliveryPolicy.IsValidSendTimeout(o.Timeout), "Smtp:Timeout must be positive and at most 60 seconds (well inside the delivery claim lease).")
            .ValidateOnStart();

        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        NotificationHttp.AddClient(services);
        services.AddScoped<INotificationChannelSender, EmailChannelSender>();
        services.AddScoped<INotificationChannelSender, SlackChannelSender>();
        services.AddScoped<INotificationChannelSender, TeamsChannelSender>();
        services.AddScoped<INotificationChannelSender, WebhookChannelSender>();

        services.AddSingleton<IApprovalLinkPolicy, SdlcApprovalLinkPolicy>();
        services.AddScoped<ApprovalLinkService>();
        services.AddScoped<NotificationComposer>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<GateTimerService>();
        services.AddScoped<Trackers.TrackerConnectionExpirySweepService>();
        services.AddSingleton<NotificationSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationSweeper>());
        return services;
    }
}
