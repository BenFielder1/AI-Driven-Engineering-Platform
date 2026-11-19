namespace Tevrin.Engine.Domain.Entities;

/// <summary>Status of an agent run (FR-23, FR-25).</summary>
public enum AgentRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>LLM provider (FR-22). Wire values: <c>anthropic</c>, <c>openai</c>, <c>ollama</c>, <c>fake</c>.</summary>
public enum LlmProvider
{
    Anthropic = 0,
    Openai = 1,
    Ollama = 2,
    Fake = 3,
}

/// <summary>Per-channel delivery state of a notification (FR-49).</summary>
public enum NotificationDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Dead = 3,

    /// <summary>Not attempted: the channel can't send in this deployment (e.g. email without SMTP settings). Never retried.</summary>
    Skipped = 4,
}

/// <summary>Project notification channel types (FR-47).</summary>
public enum NotificationChannelType
{
    Email = 0,
    Slack = 1,
    Teams = 2,
    Webhook = 3,
}

/// <summary>The decision a one-click approval link carries (FR-48).</summary>
public enum ApprovalLinkDecision
{
    Approve = 0,
    Reject = 1,
}
