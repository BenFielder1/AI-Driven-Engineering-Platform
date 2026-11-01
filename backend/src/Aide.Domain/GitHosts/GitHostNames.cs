namespace Aide.Domain.GitHosts;

/// <summary>
/// FR-140: the Git host a project's provider names in user-facing text (activity lines, notifications), from the provider's
/// wire value as stored in event payloads (<c>provider</c>). Events written before v4 carry none: they were GitHub. Pure.
/// </summary>
public static class GitHostNames
{
    public static string Display(string? providerWire) => providerWire switch
    {
        "gitlab" => "GitLab",
        "bitbucket" => "Bitbucket",
        "local" => "the local repository",
        _ => "GitHub",
    };
}
