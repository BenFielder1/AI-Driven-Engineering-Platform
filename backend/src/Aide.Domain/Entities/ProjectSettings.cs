namespace Aide.Domain.Entities;

/// <summary>Git settings of a project (FR-9, FR-35). The token is encrypted with Data Protection.</summary>
public sealed class ProjectGitSettings
{
    public Guid ProjectId { get; set; }

    public GitProvider Provider { get; set; } = GitProvider.Local;

    public string? RepoUrl { get; set; }

    public string DefaultBranch { get; set; } = "main";

    /// <summary>The per-project PAT; only for <see cref="GitProvider.GithubPat"/> (FR-83).</summary>
    public string? TokenEncrypted { get; set; }

    /// <summary>FR-82, <see cref="GitProvider.GithubApp"/>: the App installation that grants access to the repo.</summary>
    public long? InstallationId { get; set; }

    /// <summary>FR-82, <see cref="GitProvider.GithubApp"/>: GitHub's numeric repository id (stable across renames).</summary>
    public long? RepoId { get; set; }

    /// <summary>FR-82, <see cref="GitProvider.GithubApp"/>: <c>owner/name</c>. FR-136: GitLab <c>group/sub/project</c>, or
    /// Bitbucket <c>workspace/slug</c>.</summary>
    public string? RepoFullName { get; set; }

    /// <summary>FR-136, <see cref="GitProvider.Gitlab"/>/<see cref="GitProvider.Bitbucket"/>: the
    /// <see cref="GitHostInstance"/> hosting the repo (ON DELETE RESTRICT: an instance in use can't be removed, FR-130).</summary>
    public Guid? InstanceId { get; set; }

    /// <summary>FR-136: the binding Owner's <see cref="GitConnection"/> that background Git work uses; cleared (ON DELETE
    /// SET NULL) when the connection is deleted.</summary>
    public Guid? GitConnectionId { get; set; }

    /// <summary>FR-136: the Owner who bound <see cref="GitConnectionId"/> (the connection's owner).</summary>
    public Guid? BoundByUserId { get; set; }

    /// <summary>FR-136: the host's stable repo id (GitLab numeric project id, Bitbucket repository UUID), as text.</summary>
    public string? ExternalRepoId { get; set; }

    // The working branch and pull request belong to the release version (ReleaseVersion.BranchName, PullRequestUrl/Number).
}

/// <summary>LLM settings of a project (FR-9, FR-22). Model ids per agent are keyed by the agent's wire name.</summary>
public sealed class ProjectLlmSettings
{
    public Guid ProjectId { get; set; }

    public LlmProvider Provider { get; set; }

    public string DefaultModel { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> ModelsByAgent { get; set; } = new Dictionary<string, string>();

    /// <summary>FR-79: the bound user connection (<see cref="LlmCredential"/>) every run of the project uses; cleared
    /// (ON DELETE SET NULL) when the connection is deleted. Replaces v1's per-project key.</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>The Owner who bound <see cref="CredentialId"/> (the connection's owner).</summary>
    public Guid? BoundByUserId { get; set; }
}

/// <summary>Notification channels of a project (FR-9, FR-47).</summary>
public sealed class ProjectNotificationSettings
{
    public Guid ProjectId { get; set; }

    public IReadOnlyList<NotificationChannelConfig> Channels { get; set; } = [];
}
