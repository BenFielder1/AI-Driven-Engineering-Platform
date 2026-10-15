namespace Aide.Domain.Entities;

/// <summary>Git settings of a project (FR-9, FR-35). The token is encrypted with Data Protection.</summary>
public sealed class ProjectGitSettings
{
    public Guid ProjectId { get; set; }

    public GitProvider Provider { get; set; } = GitProvider.Local;

    public string? RepoUrl { get; set; }

    public string DefaultBranch { get; set; } = "main";

    public string? TokenEncrypted { get; set; }

    // The working branch and pull request belong to the release version (ReleaseVersion.BranchName, PullRequestUrl/Number).
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
