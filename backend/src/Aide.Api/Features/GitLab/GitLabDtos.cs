using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// Admin → Integrations → GitLab (FR-130): the registration help and the instances. <c>callbackUrl</c> is the exact redirect
/// URI to register on every GitLab OAuth application (<c>App:PublicBaseUrl</c> + <c>/api/auth/gitlab/callback</c>);
/// <c>scopes</c> are the scopes to tick (<c>api write_repository openid profile email</c>); <c>minimumVersion</c> is
/// <c>17.11</c>; <c>allowPrivateNetworks</c> is <c>GitLab:AllowPrivateNetworks</c> (read-only configuration). gitlab.com is
/// always listed first.
/// </summary>
public sealed record AdminGitLabIntegrationDto(
    string CallbackUrl,
    IReadOnlyList<string> Scopes,
    string MinimumVersion,
    bool AllowPrivateNetworks,
    IReadOnlyList<AdminGitLabInstanceDto> Instances);

/// <summary>
/// A GitLab instance for Admins (FR-130). Secrets are flags only (<c>hasClientSecret</c>). <c>baseUrl</c> is the effective
/// base URL (<c>https://gitlab.com</c> for gitlab.com). <c>source</c> is <c>configuration</c> when
/// <c>GitLab__Instances__&lt;n&gt;__*</c> sets it (then only <c>enabled</c> can be changed here, and it can't be removed).
/// <c>oauthConfigured</c>: client id and secret are set, so "Continue with GitLab" and OAuth connections work (token connections
/// work without). <c>version</c> is from the last reachability check (<c>null</c> when GitLab answered 401 unauthenticated);
/// <c>versionStatus</c> is <c>supported</c> (17.11+), <c>old</c> (with <c>versionWarning</c>) or <c>unknown</c>.
/// <c>connectionCount</c>/<c>projectCount</c> are the users' connections and the projects using it (removal is refused
/// while <c>projectCount</c> &gt; 0).
/// </summary>
public sealed record AdminGitLabInstanceDto(
    Guid Id,
    string Name,
    string BaseUrl,
    bool IsGitLabCom,
    GitHostInstanceSource Source,
    string? ClientId,
    bool HasClientSecret,
    bool OauthConfigured,
    bool AllowInsecureHttp,
    bool Enabled,
    string? Version,
    GitLabVersionVerdict VersionStatus,
    string? VersionWarning,
    int ConnectionCount,
    int ProjectCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Add a self-managed GitLab instance (FR-130). <c>baseUrl</c> must be <c>https://</c> (or <c>http://</c> with
/// <c>allowInsecureHttp</c>); it is normalised, checked by the SSRF guard (§8) and probed with <c>GET /api/v4/version</c>.
/// gitlab.com is always listed already (edit it to add its OAuth application). <c>clientId</c>/<c>clientSecret</c> are the
/// GitLab OAuth application's id and secret (optional: token connections work without).
/// </summary>
public sealed record CreateGitLabInstanceRequest
{
    [Required]
    [MaxLength(GitLabRules.MaxNameLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [MaxLength(GitHostAddressRules.MaxBaseUrlLength)]
    public string BaseUrl { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? ClientId { get; init; }

    [MaxLength(500)]
    public string? ClientSecret { get; init; }

    public bool AllowInsecureHttp { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Change a GitLab instance (FR-130); omitted fields are unchanged. An empty <c>clientId</c> or <c>clientSecret</c> clears it.
/// Changing <c>baseUrl</c> or <c>allowInsecureHttp</c> runs the SSRF guard and the reachability check again. gitlab.com's
/// base URL can't change. <c>enabled: false</c> disables the instance: its connections stop working and sign-in hides it.
/// </summary>
public sealed record UpdateGitLabInstanceRequest
{
    [MaxLength(GitLabRules.MaxNameLength)]
    public string? Name { get; init; }

    [MaxLength(GitHostAddressRules.MaxBaseUrlLength)]
    public string? BaseUrl { get; init; }

    [MaxLength(500)]
    public string? ClientId { get; init; }

    [MaxLength(500)]
    public string? ClientSecret { get; init; }

    public bool? AllowInsecureHttp { get; init; }

    public bool? Enabled { get; init; }
}

/// <summary>An enabled GitLab instance for users (sign-in, connections). <c>oauthConfigured</c>: "Continue with GitLab" and
/// "Connect with GitLab" are offered; otherwise only the token fallback.</summary>
public sealed record GitLabInstanceSummaryDto(Guid Id, string Name, string BaseUrl, bool IsGitLabCom, bool OauthConfigured);

/// <summary>
/// FR-131 sign-in: GitLab's verified email belongs to an existing AIDE account that isn't linked to this GitLab identity.
/// AIDE never links silently: the user signs in to that account (any method) and confirms with
/// <c>POST /api/auth/gitlab/link</c>, or cancels with <c>DELETE /api/auth/gitlab/link</c>. Expires after 10 minutes.
/// </summary>
public sealed record GitLabPendingLinkDto(Guid InstanceId, string InstanceName, string Login, string Email, DateTimeOffset ExpiresAt);

/// <summary>
/// A GitLab connection of the caller (FR-131); never a token. <c>authKind</c> is <c>oauth</c> or <c>token</c> (access token
/// fallback); <c>scopes</c> as granted or validated; <c>expiresAt</c> is an access token's own expiry, and
/// <c>expiryWarning</c> is true from 14 days before it. <c>status</c>: <c>valid</c>, <c>invalid</c> (GitLab rejected it; reconnect)
/// or <c>insufficient_scope</c>. <c>instanceEnabled: false</c> means an Admin disabled the instance ("GitLab instance disabled").
/// </summary>
public sealed record GitLabConnectionDto(
    Guid Id,
    Guid InstanceId,
    string InstanceName,
    string InstanceBaseUrl,
    bool InstanceEnabled,
    string Login,
    string ExternalUserId,
    GitConnectionAuthKind AuthKind,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    bool ExpiryWarning,
    GitConnectionStatus Status,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// FR-131 token fallback: a personal, project or group access token with <c>api</c> and <c>write_repository</c> for the
/// instance (<c>instanceId</c> omitted = gitlab.com). Validated with <c>GET /api/v4/user</c> and
/// <c>GET /api/v4/personal_access_tokens/self</c>; nothing is saved unless it passes.
/// </summary>
public sealed record CreateGitLabTokenConnectionRequest
{
    public Guid? InstanceId { get; init; }

    [Required]
    [MaxLength(GitLabRules.MaxTokenLength)]
    public string Token { get; init; } = string.Empty;
}

/// <summary>A GitLab project for the repo picker (FR-132). <c>id</c> is GitLab's numeric project id as text; <c>fullPath</c>
/// is <c>group/sub/project</c>.</summary>
public sealed record GitLabRepoDto(
    string Id,
    string FullPath,
    string Name,
    string Path,
    string? DefaultBranch,
    string? WebUrl,
    string? NamespaceFullPath,
    string? Visibility,
    DateTimeOffset? LastActivityAt);

/// <summary>One page of projects (30, newest first); pass <c>nextCursor</c> as <c>cursor</c> for the next (<c>null</c> = last page).</summary>
public sealed record GitLabRepoPageDto(IReadOnlyList<GitLabRepoDto> Items, string? NextCursor);

/// <summary>
/// Where the caller can create a GitLab project (FR-132): their personal namespace (<c>kind: user</c>) and the groups where
/// they are at least Developer (<c>kind: group</c>). <c>canCreateProjects</c> follows the group's
/// <c>project_creation_level</c>; <c>reason</c> says why not. Pass <c>fullPath</c> as <c>owner</c> when creating.
/// </summary>
public sealed record GitLabOwnerDto(long Id, GitLabOwnerKind Kind, string FullPath, string Name, bool CanCreateProjects, string? Reason);

/// <summary>
/// Create a GitLab project (FR-132) with the caller's connection <c>connectionId</c>, in namespace <c>owner</c> (a
/// <c>fullPath</c> from the owners list), initialised with a README so <c>main</c> exists and is the default branch.
/// <c>path</c> defaults to GitLab's slug of <c>name</c>. <c>private</c> (default true) sets visibility private, else public.
/// </summary>
public sealed record CreateGitLabRepoRequest
{
    [Required]
    public Guid ConnectionId { get; init; }

    [Required]
    [MaxLength(500)]
    public string Owner { get; init; } = string.Empty;

    [Required]
    [MaxLength(GitLabRules.MaxRepoNameLength)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(GitLabRules.MaxRepoNameLength)]
    public string? Path { get; init; }

    [MaxLength(GitLabRules.MaxDescriptionLength)]
    public string? Description { get; init; }

    public bool Private { get; init; } = true;
}
