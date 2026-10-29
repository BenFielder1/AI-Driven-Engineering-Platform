using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;

namespace Aide.Api.Features.GitHub;

/// <summary>A GitHub user or organisation (FR-81).</summary>
public sealed record GitHubAccountDto(string Login, GitHubOwnerType Type);

/// <summary>
/// An installation of the AIDE GitHub App that the caller can access (FR-81, <c>GET /user/installations</c>):
/// <c>repositorySelection</c> is <c>all</c> or <c>selected</c>; <c>manageUrl</c> is where the user adds repositories to it.
/// <c>pagesPermission</c> (FR-143) is whether the installation grants the App's Pages write permission (<c>granted</c>,
/// <c>missing</c>: the Owner has to accept the updated permissions on the installation, or <c>unknown</c>).
/// </summary>
public sealed record GitHubInstallationDto(
    long Id,
    GitHubAccountDto Account,
    GitHubRepositorySelection RepositorySelection,
    string ManageUrl,
    GitHubPagesAccess PagesPermission);

/// <summary>
/// The caller's GitHub connection (FR-81). Tokens are never returned. When <c>connected</c> is false, <c>login</c>,
/// <c>githubUserId</c>, <c>status</c> and <c>accessExpiresAt</c> are <c>null</c> and <c>installations</c> is empty.
/// <c>status</c> is <c>invalid</c> after a failed token refresh (§4.14); <c>accessExpiresAt</c> is <c>null</c> when the
/// App's tokens don't expire. <c>installUrl</c> is the App's "Install AIDE on more repositories" link (<c>null</c> when no
/// App is registered).
/// </summary>
public sealed record GitHubConnectionDto(
    bool Connected,
    string? Login,
    long? GithubUserId,
    GitHubConnectionStatus? Status,
    DateTimeOffset? AccessExpiresAt,
    IReadOnlyList<GitHubInstallationDto> Installations,
    string? InstallUrl);

/// <summary>
/// A repository the AIDE GitHub App can access for the caller (FR-81, FR-86). <c>installationId</c> is the installation
/// that grants it (send it with <c>repoFullName</c> in <c>POST /api/projects</c>). <c>empty</c> is whether the repo has
/// no commits (<c>null</c>: not checked in the list; <c>GET …/refs</c> always says). <c>canPush</c>: the caller may push to it
/// (push, maintain or admin); only such repositories can back a project (FR-82, FR-83).
/// </summary>
public sealed record GitHubRepoDto(
    long Id,
    string FullName,
    string Owner,
    string Name,
    bool Private,
    string DefaultBranch,
    long InstallationId,
    bool? Empty,
    string HtmlUrl,
    bool CanPush);

/// <summary>A page of the repo picker (FR-81): <c>hasMore</c> is true when <c>page + 1</c> has more repositories.</summary>
public sealed record GitHubRepoPageDto(IReadOnlyList<GitHubRepoDto> Items, int Page, bool HasMore);

/// <summary>
/// A possible owner for "Create repository" (FR-81): the caller (<c>user</c>) or an organisation they belong to.
/// <c>appInstalled</c> / <c>installationId</c>: whether (and where) the AIDE GitHub App is installed on it;
/// <c>canCreate</c>: the App is installed, has Administration write, and (for an organisation) the caller may create
/// repositories there.
/// </summary>
public sealed record GitHubOwnerDto(string Login, GitHubOwnerType Type, long? InstallationId, bool AppInstalled, bool CanCreate);

/// <summary>A GitHub release (FR-73 import, FR-86): <c>isLatest</c> marks GitHub's latest release.</summary>
public sealed record GitHubReleaseDto(string TagName, string? Name, DateTimeOffset? PublishedAt, bool IsLatest);

/// <summary>A tag; <c>semver</c> is its version (a leading <c>v</c> or the tag prefix stripped), or <c>null</c> when the
/// tag isn't a semantic version.</summary>
public sealed record GitHubTagDto(string Name, string Sha, string? Semver);

/// <summary>
/// What the wizard needs to know about a picked repository (FR-73, FR-86): its default branch, whether it is
/// <c>empty</c> (no commits: "start from scratch"; otherwise it is treated as an import), its releases and tags.
/// </summary>
public sealed record GitHubRepoRefsDto(
    string DefaultBranch,
    bool Empty,
    IReadOnlyList<GitHubReleaseDto> Releases,
    IReadOnlyList<GitHubTagDto> Tags);

/// <summary>
/// Create a repository (FR-81, §4.14) for <c>owner</c> (the caller's login or an organisation where the App is installed),
/// initialised with a README so the default branch exists.
/// </summary>
public sealed record CreateGitHubRepoRequest
{
    [Required]
    [MaxLength(GitHubRules.MaxLoginLength)]
    [RegularExpression(GitHubRules.LoginPattern)]
    public required string Owner { get; init; }

    [Required]
    [MaxLength(GitHubRules.MaxRepoNameLength)]
    [RegularExpression(GitHubRules.RepoNamePattern)]
    public required string Name { get; init; }

    [MaxLength(GitHubRules.MaxRepoDescriptionLength)]
    public string? Description { get; init; }

    public required bool Private { get; init; }
}
