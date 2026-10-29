using Aide.Domain.GitHosts;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// GitLab settings (section <c>GitLab</c>, FR-130, §8, §9):
/// <list type="bullet">
/// <item><c>GitLab:AllowPrivateNetworks</c> (default <c>true</c>; <c>false</c> on Railway): whether instances may resolve to
/// private ranges (10/8, 172.16/12, 192.168/16, fc00::/7, <c>*.railway.internal</c>). Loopback, link-local, metadata and
/// <c>0.0.0.0</c> are always refused.</item>
/// <item><c>GitLab:Instances:&lt;n&gt;:{Name, BaseUrl, ClientId, ClientSecret, AllowInsecureHttp}</c>: instances from
/// <c>secrets.env</c> or Railway variables. An empty <c>BaseUrl</c> is gitlab.com. They take precedence over the Admin's
/// row with the same base URL (a row is created for them so connections can reference it).</item>
/// <item><c>GitLab:RequestTimeout</c> (default 20 s): per GitLab API request.</item>
/// </list>
/// </summary>
public sealed class GitLabOptions
{
    public const string SectionName = "GitLab";

    public bool AllowPrivateNetworks { get; set; } = true;

    public List<GitLabInstanceSettings> Instances { get; set; } = [];

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Tests only: clone and push go to <c>{GitBaseUrlOverride}{group/sub/project}.git</c> instead of the instance's base
    /// URL (a <c>file://</c> folder of bare repositories). Needs <see cref="AllowInsecureGitBaseUrl"/>. Never set in a deployment.
    /// </summary>
    public string? GitBaseUrlOverride { get; set; }

    /// <summary>Tests only: allows a <c>file://</c> or <c>http://</c> <see cref="GitBaseUrlOverride"/>.</summary>
    public bool AllowInsecureGitBaseUrl { get; set; }
}

/// <summary>One configured GitLab instance (<c>GitLab__Instances__&lt;n&gt;__*</c>). Secrets never reach the database.</summary>
public sealed class GitLabInstanceSettings
{
    public string? Name { get; set; }

    /// <summary>Empty for gitlab.com.</summary>
    public string? BaseUrl { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool AllowInsecureHttp { get; set; }

    /// <summary>The normalised base URL (<c>null</c> for gitlab.com), or a failure for an invalid one.</summary>
    public Domain.Common.Result<string?> NormalizedBaseUrl(bool allowPrivateNetworks)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return Domain.Common.Result.Success<string?>(null);
        }

        var normalized = GitHostAddressRules.NormalizeBaseUrl(BaseUrl, AllowInsecureHttp, allowPrivateNetworks);
        if (normalized.IsFailure)
        {
            return Domain.Common.Result.Failure<string?>(normalized.Error!);
        }

        var text = normalized.Value.AbsoluteUri.TrimEnd('/');
        return Domain.Common.Result.Success<string?>(string.Equals(text, GitLabRules.GitLabComBaseUrl, StringComparison.Ordinal) ? null : text);
    }
}

/// <summary>Configured instances must have a valid base URL that the SSRF guard accepts (§8); otherwise startup fails.</summary>
public sealed class GitLabOptionsValidator : IValidateOptions<GitLabOptions>
{
    public ValidateOptionsResult Validate(string? name, GitLabOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("GitLab:RequestTimeout must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(options.GitBaseUrlOverride)
            && (!Uri.TryCreate(options.GitBaseUrlOverride, UriKind.Absolute, out var gitBase)
                || (gitBase.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureGitBaseUrl)
                || !string.IsNullOrEmpty(gitBase.UserInfo) || !string.IsNullOrEmpty(gitBase.Query)))
        {
            failures.Add("GitLab:GitBaseUrlOverride must be an absolute https URL without credentials or a query (file:// or http:// only with GitLab:AllowInsecureGitBaseUrl).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Instances.Count; i++)
        {
            var normalized = options.Instances[i].NormalizedBaseUrl(options.AllowPrivateNetworks);
            if (normalized.IsFailure)
            {
                failures.Add($"GitLab:Instances:{i}:BaseUrl is invalid: {string.Join(" ", normalized.Error!.Details)}");
                continue;
            }

            if (!seen.Add(normalized.Value ?? string.Empty))
            {
                failures.Add($"GitLab:Instances:{i}:BaseUrl repeats another configured instance.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
