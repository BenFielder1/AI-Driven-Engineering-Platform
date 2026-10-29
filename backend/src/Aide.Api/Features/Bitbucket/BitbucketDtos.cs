using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// Admin → Integrations → Bitbucket (FR-133): the OAuth consumer and its registration help. <c>callbackUrl</c> is the exact
/// callback to enter on the consumer (<c>App:PublicBaseUrl</c> + <c>/api/auth/bitbucket/callback</c>); <c>permissions</c> are
/// the consumer permissions to tick (<c>account email repository:write repository:admin pullrequest:write project
/// webhook</c>; Bitbucket fixes scopes on the consumer). <c>source</c> is <c>configuration</c> when
/// <c>Bitbucket__OAuth__*</c> sets the consumer (then it can't be changed or removed here). The secret is a flag only
/// (<c>hasClientSecret</c>). <c>oauthConfigured</c>: key and secret are set, so "Continue with Bitbucket" and OAuth
/// connections work (API-token connections work without). <c>connectionCount</c> counts users' Bitbucket connections.
/// </summary>
public sealed record AdminBitbucketIntegrationDto(
    string CallbackUrl,
    IReadOnlyList<string> Permissions,
    GitHostInstanceSource Source,
    string? ClientId,
    bool HasClientSecret,
    bool OauthConfigured,
    int ConnectionCount,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Save the Bitbucket OAuth consumer (FR-133): its key (<c>clientId</c>) and secret. An omitted <c>clientSecret</c> keeps the
/// stored one (so the key can be corrected without re-entering the secret).
/// </summary>
public sealed record UpdateBitbucketConsumerRequest
{
    [Required]
    [MaxLength(500)]
    public string ClientId { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? ClientSecret { get; init; }
}

/// <summary>
/// FR-134 sign-in: Bitbucket's verified email belongs to an existing AIDE account that isn't linked to this Bitbucket
/// identity. AIDE never links silently (D-V4-18): the user signs in to that account (any method) and confirms with
/// <c>POST /api/auth/bitbucket/link</c>, or cancels with <c>DELETE /api/auth/bitbucket/link</c>. Expires after 10 minutes.
/// </summary>
public sealed record BitbucketPendingLinkDto(string Login, string Email, DateTimeOffset ExpiresAt);

/// <summary>
/// A Bitbucket connection of the caller (FR-134); never a token or the API token's email. <c>externalUserId</c> is the
/// Bitbucket account UUID. <c>authKind</c> is <c>oauth</c> or <c>token</c> (Atlassian API token); <c>scopes</c> as granted
/// (OAuth) or as checked (API token). <c>status</c>: <c>valid</c>, <c>invalid</c> (Bitbucket rejected it; reconnect) or
/// <c>insufficient_scope</c>.
/// </summary>
public sealed record BitbucketConnectionDto(
    Guid Id,
    string Login,
    string ExternalUserId,
    GitConnectionAuthKind AuthKind,
    IReadOnlyList<string> Scopes,
    GitConnectionStatus Status,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// FR-134 token fallback: an Atlassian API token with the scopes <c>read:user</c>, <c>read:workspace</c>,
/// <c>read:project</c>, <c>read:repository</c>, <c>write:repository</c>, <c>admin:repository</c>, <c>read:pullrequest</c>,
/// <c>write:pullrequest</c> and <c>read:pipeline</c> (all <c>:bitbucket</c>), and the Atlassian account <c>email</c> it
/// belongs to. App passwords aren't supported. Nothing is saved unless the token passes the checks.
/// </summary>
public sealed record CreateBitbucketTokenConnectionRequest
{
    [Required]
    [MaxLength(BitbucketRules.MaxTokenLength)]
    public string Token { get; init; } = string.Empty;

    [Required]
    [MaxLength(BitbucketRules.MaxEmailLength)]
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// A Bitbucket repository for the repo picker (FR-135). <c>id</c> is Bitbucket's repository UUID; <c>fullName</c> is
/// <c>workspace/slug</c>; <c>mainBranch</c> is <c>null</c> for an empty repository.
/// </summary>
public sealed record BitbucketRepoDto(
    string Id,
    string FullName,
    string Name,
    string Slug,
    string? Workspace,
    string? ProjectKey,
    string? MainBranch,
    bool IsPrivate,
    string? WebUrl,
    DateTimeOffset? UpdatedOn);

/// <summary>One page of repositories (one workspace at a time, most recently updated first); pass <c>nextCursor</c> as
/// <c>cursor</c> for the next (<c>null</c> = last page).</summary>
public sealed record BitbucketRepoPageDto(IReadOnlyList<BitbucketRepoDto> Items, string? NextCursor);

/// <summary>A project in a workspace (FR-135); pass <c>key</c> as <c>project</c> when creating a repository.</summary>
public sealed record BitbucketProjectDto(string Key, string Name, bool? IsPrivate);

/// <summary>
/// A workspace the caller can access (FR-135) with the first 100 of its projects (<c>projectsTruncated</c>: it has more).
/// Pass <c>slug</c> as <c>owner</c> when creating. <c>isAdmin</c> is whether the caller administers the workspace, when
/// Bitbucket says.
/// </summary>
public sealed record BitbucketWorkspaceDto(string Slug, string Name, bool? IsAdmin, IReadOnlyList<BitbucketProjectDto> Projects, bool ProjectsTruncated);

/// <summary>
/// Where the caller can create a Bitbucket repository (FR-135): their first 50 workspaces with their projects;
/// <c>truncated</c> says the caller has more workspaces than are listed.
/// </summary>
public sealed record BitbucketOwnersDto(IReadOnlyList<BitbucketWorkspaceDto> Workspaces, bool Truncated);

/// <summary>
/// Create a Bitbucket repository (FR-135) with the caller's connection <c>connectionId</c> in workspace <c>owner</c> and
/// project <c>project</c> (its key; Bitbucket requires one). The slug is Bitbucket's slug of <c>name</c>. AIDE then pushes an
/// initial commit with a README to <c>main</c>, so the default branch exists. <c>private</c> defaults to true.
/// </summary>
public sealed record CreateBitbucketRepoRequest
{
    [Required]
    public Guid ConnectionId { get; init; }

    [Required]
    [MaxLength(BitbucketRules.MaxWorkspaceLength)]
    public string Owner { get; init; } = string.Empty;

    [Required]
    [MaxLength(BitbucketRules.MaxProjectKeyLength)]
    public string Project { get; init; } = string.Empty;

    [Required]
    [MaxLength(BitbucketRules.MaxRepoNameLength)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(BitbucketRules.MaxDescriptionLength)]
    public string? Description { get; init; }

    public bool Private { get; init; } = true;
}
