using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Git;

/// <summary>
/// GitHub provider settings (section <c>GitHub</c>, FR-35). Hosts are system configuration only: a project's repository URL
/// supplies just the owner and repository name, so no project can point AIDE at another host (no SSRF).
/// </summary>
public sealed class GitHubGitOptions
{
    public const string SectionName = "GitHub";

    public const string DefaultGitBaseUrl = "https://github.com/";

    /// <summary>
    /// Where repositories are cloned from and pushed to: <c>{GitBaseUrl}{owner}/{repo}.git</c>. Default
    /// https://github.com/; set it (with <c>GitHub:ApiBaseUrl</c>) for GitHub Enterprise.
    /// </summary>
    public string GitBaseUrl { get; set; } = DefaultGitBaseUrl;

    /// <summary>
    /// Tests only: lets <see cref="GitBaseUrl"/> be a <c>file://</c> (or <c>http://</c>) URL, so clone and push run against
    /// a local bare repository. Never set this in a deployment.
    /// </summary>
    public bool AllowInsecureGitBaseUrl { get; set; }

    /// <summary><see cref="GitBaseUrl"/> with a trailing slash.</summary>
    public string NormalizedGitBaseUrl => GitBaseUrl.EndsWith('/') ? GitBaseUrl : GitBaseUrl + "/";
}

/// <summary>Validates <see cref="GitHubGitOptions"/> at start-up: an absolute https base URL unless insecure URLs are allowed (tests).</summary>
internal sealed class GitHubGitOptionsValidator : IValidateOptions<GitHubGitOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubGitOptions options)
    {
        if (!Uri.TryCreate(options.GitBaseUrl, UriKind.Absolute, out var uri))
        {
            return ValidateOptionsResult.Fail("GitHub:GitBaseUrl must be an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureGitBaseUrl)
        {
            return ValidateOptionsResult.Fail("GitHub:GitBaseUrl must use https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return ValidateOptionsResult.Fail("GitHub:GitBaseUrl must not contain credentials, a query or a fragment.");
        }

        return ValidateOptionsResult.Success;
    }
}
