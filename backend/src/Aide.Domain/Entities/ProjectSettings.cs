namespace Aide.Domain.Entities;

/// <summary>Git settings of a project (FR-9, FR-35). The token is encrypted with Data Protection.</summary>
public sealed class ProjectGitSettings
{
    public Guid ProjectId { get; set; }

    public GitProvider Provider { get; set; } = GitProvider.Local;

    public string? RepoUrl { get; set; }

    public string DefaultBranch { get; set; } = "main";

    public string? TokenEncrypted { get; set; }

    /// <summary>The project branch <c>aide/&lt;slug&gt;-&lt;short-id&gt;</c>, set when the working copy is prepared.</summary>
    public string? BranchName { get; set; }

    /// <summary>The project PR opened when execution completes (FR-36); merged by the release job (FR-42).</summary>
    public string? PullRequestUrl { get; set; }

    public int? PullRequestNumber { get; set; }
}

/// <summary>LLM settings of a project (FR-9, FR-22). Model ids per agent are keyed by the agent's wire name.</summary>
public sealed class ProjectLlmSettings
{
    public Guid ProjectId { get; set; }

    public LlmProvider Provider { get; set; }

    public string DefaultModel { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> ModelsByAgent { get; set; } = new Dictionary<string, string>();

    public string? ApiKeyEncrypted { get; set; }
}

/// <summary>Notification channels of a project (FR-9, FR-47).</summary>
public sealed class ProjectNotificationSettings
{
    public Guid ProjectId { get; set; }

    public IReadOnlyList<NotificationChannelConfig> Channels { get; set; } = [];
}
