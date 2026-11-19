namespace Tevrin.Engine.Domain.Entities;

/// <summary>A configured project notification channel (FR-47). The secret is encrypted with Data Protection: the
/// HMAC signing key of a webhook, or the incoming-webhook URL of a Slack or Teams channel (a bearer credential), whose
/// <see cref="Target"/> then only holds the masked display form (<c>hooks.slack.com/…</c>). <see cref="Id"/>
/// identifies the channel across settings updates (a kept channel keeps its secret).</summary>
public sealed record NotificationChannelConfig(
    Guid Id,
    NotificationChannelType Type,
    string Target,
    string? SecretEncrypted,
    IReadOnlyList<string> Events);
