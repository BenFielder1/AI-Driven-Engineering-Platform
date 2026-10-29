using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// FR-132, FR-141: reads shared by the request paths (the caller's connection) and <see cref="GitLabGitProvider"/>: a
/// repository's release refs for the import, and the owners a project can be created in.
/// </summary>
public static class GitLabReaders
{
    /// <summary>Release tags missing from the tag list that are looked up one by one (as on GitHub, FR-73).</summary>
    public const int MaxReleaseTagLookups = 20;

    /// <summary>
    /// FR-141: the repository's default branch and head, whether it is empty (no commits, or only starter files: FR-86),
    /// its Releases (upcoming ones left out; the latest is the newest <c>released_at</c>) and tags (with semantic versions),
    /// and the suggested released version and version to develop. <c>null</c> when the repository isn't visible.
    /// </summary>
    public static async Task<GitHostReleaseRefs?> ReleaseRefsAsync(GitLabClient client, string token, string repoId, string tagPrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var project = await client.GetProjectAsync(token, repoId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var id = project.ExternalId;
        var defaultBranch = project.DefaultBranch ?? Domain.Projects.ProjectSettingsRules.DefaultBranch;
        var head = project.EmptyRepo ? null : await client.GetBranchHeadAsync(token, id, defaultBranch, cancellationToken);
        var root = head is null ? null : await client.ListRootEntriesAsync(token, id, defaultBranch, cancellationToken);
        if (head is null)
        {
            return new GitHostReleaseRefs(project.PathWithNamespace, defaultBranch, null, true, [], [], GitLabProjectRules.SuggestRelease([], tagPrefix));
        }

        var released = (await client.ListReleasesAsync(token, id, cancellationToken)).Where(r => !r.Upcoming).ToList();
        var latest = released.Where(r => r.ReleasedAt is not null).OrderByDescending(r => r.ReleasedAt).Select(r => r.TagName).FirstOrDefault();
        var tags = (await client.ListTagsAsync(token, id, cancellationToken)).ToList();
        var listed = tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in released.Select(r => r.TagName).Where(t => !listed.Contains(t)).Distinct(StringComparer.Ordinal).Take(MaxReleaseTagLookups))
        {
            if (await client.FindTagCommitAsync(token, id, missing, cancellationToken) is { } sha)
            {
                tags.Add(new GitLabTag(missing, sha));
            }
        }

        var releases = released.Select(r => new GitHostRelease(r.TagName, r.Name, r.ReleasedAt, r.TagName == latest)).ToList();
        var candidates = releases.Select(r => new ReleaseRefCandidate(r.TagName, true, r.IsLatest))
            .Concat(tags.Select(t => new ReleaseRefCandidate(t.Name, false, false)));
        return new GitHostReleaseRefs(
            project.PathWithNamespace,
            defaultBranch,
            head,
            Domain.GitHub.GitHubRules.IsEmptyRepository(root),
            releases,
            [.. tags.Select(t => new GitHostTag(t.Name, t.CommitSha, Domain.GitHub.GitHubRules.TagSemver(t.Name, tagPrefix)))],
            GitLabProjectRules.SuggestRelease(candidates, tagPrefix));
    }

    /// <summary>
    /// FR-132: the personal namespace, then the groups where the user is at least Developer
    /// (<c>GET /groups?min_access_level=30</c>), each with whether its <c>project_creation_level</c> lets them create a
    /// project. The user's level per group comes from the Maintainer (40) and Owner (50) group lists.
    /// </summary>
    public static async Task<IReadOnlyList<GitHostOwner>> OwnersAsync(GitLabClient client, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var user = await client.GetUserAsync(token, cancellationToken);
        var personal = await client.GetNamespaceAsync(token, user.Username, cancellationToken);
        var developer = await client.ListGroupsAsync(token, GitLabRules.DeveloperAccess, cancellationToken);
        var maintainer = (await client.ListGroupsAsync(token, GitLabRules.MaintainerAccess, cancellationToken)).Select(g => g.Id).ToHashSet();
        var owner = developer.Any(g => string.Equals(g.ProjectCreationLevel, "owner", StringComparison.OrdinalIgnoreCase))
            ? (await client.ListGroupsAsync(token, GitLabRules.OwnerAccess, cancellationToken)).Select(g => g.Id).ToHashSet()
            : [];
        var list = new List<GitHostOwner>
        {
            new(personal?.Id ?? 0, GitLabOwnerKind.User.ToWireName(), personal?.FullPath ?? user.Username, user.Name ?? user.Username, true, null),
        };
        foreach (var group in developer)
        {
            var level = owner.Contains(group.Id) ? GitLabRules.OwnerAccess
                : maintainer.Contains(group.Id) ? GitLabRules.MaintainerAccess
                : GitLabRules.DeveloperAccess;
            list.Add(new GitHostOwner(
                group.Id,
                GitLabOwnerKind.Group.ToWireName(),
                group.FullPath,
                group.FullName ?? group.Name,
                GitLabRules.CanCreateProjectIn(group.ProjectCreationLevel, level),
                GitLabRules.CannotCreateReason(group.ProjectCreationLevel, level)));
        }

        return list;
    }

    private static string ToWireName(this GitLabOwnerKind kind) => Domain.Common.EnumStrings.ToWire(kind);
}
