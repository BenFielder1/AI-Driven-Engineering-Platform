using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;

namespace Aide.Api.Features.Admin;

/// <summary>
/// The AIDE GitHub App's status for Admin → Integrations → GitHub (FR-80). Secrets (client secret, private key) are never
/// returned. <c>source</c> is <c>configuration</c> when <c>GitHub__App__*</c> is set (it takes precedence), <c>database</c>
/// for an App registered through the manifest flow, and <c>null</c> when <c>status</c> is <c>not_configured</c> (then every
/// App field is <c>null</c> and <c>permissions</c> is empty). <c>installUrl</c> is
/// <c>https://github.com/apps/{slug}/installations/new</c>; <c>permissions</c> are the App's granted permissions
/// (e.g. <c>contents: write</c>); <c>canCreateRepositories</c> is true when <c>administration: write</c> is granted (§4.14).
/// </summary>
public sealed record AdminGitHubAppDto(
    GitHubAppStatus Status,
    GitHubAppSource? Source,
    long? AppId,
    string? Slug,
    string? OwnerLogin,
    GitHubOwnerType? OwnerType,
    string? InstallUrl,
    string? HtmlUrl,
    IReadOnlyDictionary<string, string> Permissions,
    bool CanCreateRepositories);

/// <summary>
/// Start the GitHub App manifest flow (FR-80): the App is owned by the Admin's personal account (<c>user</c>) or by the
/// organisation <c>org</c> (required for <c>organization</c>, ignored for <c>user</c>).
/// </summary>
public sealed record CreateGitHubAppManifestRequest
{
    public required GitHubOwnerType OwnerType { get; init; }

    /// <summary>The organisation login, for <c>ownerType: organization</c>.</summary>
    [MaxLength(GitHubRules.MaxLoginLength)]
    [RegularExpression(GitHubRules.LoginPattern)]
    public string? Org { get; init; }
}

/// <summary>The manifest's webhook settings: AIDE runs on localhost, so the webhook is inactive (§4.14).</summary>
public sealed record GitHubAppManifestHookDto([property: JsonPropertyName("active")] bool Active);

/// <summary>
/// A GitHub App manifest (FR-80, §4.14), in GitHub's own field names so the page can post it unchanged (as the
/// <c>manifest</c> form field, JSON-encoded): name <c>AIDE ({host})</c> (the host of <c>App:PublicBaseUrl</c>); <c>url</c> and <c>setup_url</c> under
/// <c>App:PublicBaseUrl</c>; <c>redirect_url</c> the manifest callback (<c>/api/admin/integrations/github/callback</c>);
/// <c>callback_urls</c> the sign-in callback (<c>/api/auth/github/callback</c>); webhook inactive; private; and
/// <c>default_permissions</c> contents, pull_requests, workflows and administration <c>write</c>, metadata, checks and
/// statuses <c>read</c> (the Admin may drop <c>administration</c>, which disables "Create repository"); no events.
/// </summary>
public sealed record GitHubAppManifestDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("hook_attributes")] GitHubAppManifestHookDto HookAttributes,
    [property: JsonPropertyName("redirect_url")] string RedirectUrl,
    [property: JsonPropertyName("callback_urls")] IReadOnlyList<string> CallbackUrls,
    [property: JsonPropertyName("setup_url")] string SetupUrl,
    [property: JsonPropertyName("public")] bool Public,
    [property: JsonPropertyName("default_permissions")] IReadOnlyDictionary<string, string> DefaultPermissions,
    [property: JsonPropertyName("default_events")] IReadOnlyList<string> DefaultEvents,
    [property: JsonPropertyName("request_oauth_on_install")] bool RequestOauthOnInstall);

/// <summary>
/// What the page posts to GitHub (FR-80): an HTML form <c>POST</c> to <c>postUrl</c>
/// (<c>https://github.com/settings/apps/new?state=…</c>, or
/// <c>https://github.com/organizations/{org}/settings/apps/new?state=…</c>) with the field <c>manifest</c> set to the
/// JSON of <c>manifest</c>. <c>state</c> (also in <c>postUrl</c>) is single-use, bound to the Admin's session and checked
/// by the callback.
/// </summary>
public sealed record GitHubAppManifestResponseDto(string PostUrl, GitHubAppManifestDto Manifest, string State);

/// <summary>Request limits of the GitHub App manifest callback.</summary>
public static class AdminGitHubLimits
{
    public const int CodeMaxLength = 200;
    public const int StateMaxLength = 200;
}
