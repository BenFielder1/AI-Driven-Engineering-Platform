using Aide.Api.Features.Approvals;
using Aide.Api.Jobs;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Notifications;

namespace Aide.Api.Features.Notifications;

/// <summary>Notification delivery settings (section <c>Notifications</c>, FR-47, FR-49, FR-15).</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Lets Slack, Teams and webhook channels reach private, loopback and link-local addresses. Off by default and in every
    /// deployment (NFR-SEC, SSRF); only tests that stand up a local receiver turn it on.
    /// </summary>
    public bool AllowPrivateTargets { get; set; }

    /// <summary>Run the background sweep (delivery retries and gate reminders). Tests turn it off and drive it directly.</summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>How often due delivery retries are attempted (FR-49).</summary>
    public TimeSpan DeliverySweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often pending gates are checked for reminders and overdue flags (FR-15: hourly).</summary>
    public TimeSpan GateSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Timeout of one Slack, Teams or webhook request (at most <see cref="DeliveryPolicy.MaxSendTimeout"/>).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Deliveries retried per sweep.</summary>
    public int SweepBatchSize { get; set; } = 50;
}

/// <summary>App-wide settings (section <c>App</c>).</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>The browser-facing base URL, used for absolute links in channel messages (env <c>App__PublicBaseUrl</c>).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary><see cref="PublicBaseUrl"/> + <paramref name="path"/> (a path starting with <c>/</c>).</summary>
    public string Absolute(string path) => PublicBaseUrl.TrimEnd('/') + path;
}

/// <summary>
/// One-click approval links (section <c>Approvals</c>, FR-48). <c>SigningKey</c> (env <c>Approvals__SigningKey</c>, at least
/// 32 characters) signs the link tokens; without it no links are created (fail closed) and gate notifications link to the
/// project page only.
/// </summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Approvals";

    public string? SigningKey { get; set; }
}

public static class NotificationServiceCollectionExtensions
{
    /// <summary>FR-47–FR-49, FR-15: notification fan-out job, channels, delivery retries, gate timers and approval links.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddOptions<NotificationOptions>()
            .BindConfiguration(NotificationOptions.SectionName)
            .Validate(o => o.DeliverySweepInterval > TimeSpan.Zero && o.GateSweepInterval > TimeSpan.Zero, "Notifications sweep intervals must be positive.")
            .Validate(o => DeliveryPolicy.IsValidSendTimeout(o.RequestTimeout), "Notifications:RequestTimeout must be positive and at most 60 seconds (well inside the delivery claim lease).")
            .Validate(o => o.SweepBatchSize >= 1, "Notifications:SweepBatchSize must be at least 1.")
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

        services.AddScoped<ApprovalLinkService>();
        services.AddScoped<NotificationComposer>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<GateTimerService>();
        services.AddJobHandler<NotificationJobHandler>(JobTypes.Notification);
        services.AddSingleton<NotificationSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationSweeper>());
        return services;
    }
}
