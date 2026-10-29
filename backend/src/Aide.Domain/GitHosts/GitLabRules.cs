using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.GitHosts;

/// <summary>Where a GitLab instance's settings come from (FR-130). Wire values: <c>database</c>, <c>configuration</c>.</summary>
public enum GitHostInstanceSource
{
    /// <summary>Registered by an Admin in Admin → Integrations → GitLab.</summary>
    Database = 0,

    /// <summary>From <c>GitLab__Instances__&lt;n&gt;__*</c> (secrets.env or Railway variables), which take precedence.</summary>
    Configuration = 1,
}

/// <summary>A place a GitLab project can be created (FR-132). Wire values: <c>user</c>, <c>group</c>.</summary>
public enum GitLabOwnerKind
{
    /// <summary>The user's personal namespace.</summary>
    User = 0,

    /// <summary>A group (or subgroup) the user is at least a Developer of.</summary>
    Group = 1,
}

/// <summary>The outcome of a GitLab <c>GET /api/v4/version</c> reachability check (FR-130).</summary>
public enum GitLabVersionVerdict
{
    /// <summary>The version is 17.11 or later.</summary>
    Supported = 0,

    /// <summary>Older than 17.11: accepted with a warning.</summary>
    Old = 1,

    /// <summary>The instance answered 401 unauthenticated (reachable, version unknown), or the version didn't parse.</summary>
    Unknown = 2,
}

/// <summary>
/// FR-130–FR-132, §4.19: the pure GitLab rules: gitlab.com, the minimum version, OAuth scopes, PKCE S256, access levels
/// and project-creation levels, the create-project error mapping and request validation.
/// </summary>
public static partial class GitLabRules
{
    /// <summary>gitlab.com, the instance offered by default (its row stores a NULL base URL).</summary>
    public const string GitLabComBaseUrl = "https://gitlab.com";

    public const string GitLabComName = "GitLab.com";

    /// <summary>The OAuth callback path registered on every GitLab OAuth application (§9).</summary>
    public const string CallbackPath = "/api/auth/gitlab/callback";

    /// <summary>§4.19: the minimum supported GitLab version; older instances show a warning.</summary>
    public static readonly Version MinimumVersion = new(17, 11);

    /// <summary>GitLab access levels (Developer 30, Maintainer 40, Owner 50).</summary>
    public const int DeveloperAccess = 30;

    public const int MaintainerAccess = 40;

    public const int OwnerAccess = 50;

    public const int MaxNameLength = 200;

    public const int MaxTokenLength = 1000;

    public const int MaxRepoNameLength = 255;

    public const int MaxDescriptionLength = 2000;

    public const int MaxSearchLength = 100;

    public const int MaxCursorLength = 40;

    /// <summary>FR-131: the space-separated OAuth <c>scope</c> parameter.</summary>
    public static string OauthScopeParameter => string.Join(' ', GitTokenRules.GitLabOauthScopes);

    /// <summary>
    /// FR-130: the verdict for a reported version (<c>17.11.2-ee</c>, <c>18.0.0</c>): 17.11 or later is supported; a
    /// missing or unparseable version is unknown.
    /// </summary>
    public static GitLabVersionVerdict CheckVersion(string? version)
    {
        if (ParseVersion(version) is not { } parsed)
        {
            return GitLabVersionVerdict.Unknown;
        }

        return parsed >= MinimumVersion ? GitLabVersionVerdict.Supported : GitLabVersionVerdict.Old;
    }

    /// <summary>The warning shown for <see cref="GitLabVersionVerdict.Old"/>, else <c>null</c>.</summary>
    public static string? VersionWarning(string? version) => CheckVersion(version) == GitLabVersionVerdict.Old
        ? $"GitLab {version} is older than {MinimumVersion.Major}.{MinimumVersion.Minor}, the minimum AIDE supports. Some features may not work."
        : null;

    /// <summary>The major.minor of a GitLab version string, else <c>null</c>.</summary>
    public static Version? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = VersionPattern().Match(version.Trim());
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                ? new Version(major, minor)
                : null;
    }

    /// <summary>FR-131: the PKCE S256 code challenge of <paramref name="verifier"/> (base64url of its SHA-256, unpadded).</summary>
    public static string PkceChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>RFC 7636: a verifier is 43–128 characters of <c>[A-Za-z0-9-._~]</c>.</summary>
    public static bool IsValidPkceVerifier(string? verifier) => verifier is { Length: >= 43 and <= 128 } && PkceVerifierPattern().IsMatch(verifier);

    /// <summary>Unpadded base64url.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Granted OAuth scopes from a token response's space-separated <c>scope</c>.</summary>
    public static IReadOnlyList<string> ParseScopes(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? [] : [.. scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// FR-132: whether a user with <paramref name="accessLevel"/> in a group may create projects there, from the group's
    /// <c>project_creation_level</c>: <c>noone</c> and <c>administrator</c> never (instance admins aside), <c>owner</c> Owners,
    /// <c>maintainer</c> Maintainers and up, <c>developer</c> Developers and up. GitLab's default (missing) is
    /// <c>maintainer</c>.
    /// </summary>
    public static bool CanCreateProjectIn(string? projectCreationLevel, int accessLevel) =>
        (projectCreationLevel?.Trim().ToLowerInvariant() ?? "maintainer") switch
        {
            "noone" or "administrator" => false,
            "owner" => accessLevel >= OwnerAccess,
            "developer" => accessLevel >= DeveloperAccess,
            _ => accessLevel >= MaintainerAccess,
        };

    /// <summary>Why <see cref="CanCreateProjectIn"/> is false, for the owner picker.</summary>
    public static string? CannotCreateReason(string? projectCreationLevel, int accessLevel) =>
        CanCreateProjectIn(projectCreationLevel, accessLevel)
            ? null
            : (projectCreationLevel?.Trim().ToLowerInvariant() ?? "maintainer") switch
            {
                "noone" or "administrator" => "Project creation is turned off in this group.",
                "owner" => "Only group Owners can create projects in this group.",
                "developer" => "Only Developers and above can create projects in this group.",
                _ => "Only Maintainers and Owners can create projects in this group.",
            };

    /// <summary>
    /// FR-132: the AIDE error for a failed <c>POST /projects</c>: a name or path that "has already been taken" is
    /// <c>REPO_NAME_TAKEN</c>; a 429 or a project-creation limit is <c>UPSTREAM_RATE_LIMITED</c>; a 403 is
    /// <c>FORBIDDEN</c>; another 400/422 is <c>VALIDATION</c>; anything else is <c>UPSTREAM_ERROR</c>.
    /// <paramref name="message"/> is GitLab's error text (its <c>message</c>, flattened).
    /// </summary>
    public static string CreateProjectErrorCode(int status, string? message)
    {
        var text = message ?? string.Empty;
        if (status == 429 || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("creation limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCodes.UpstreamRateLimited;
        }

        if (status is 400 or 409 or 422 && text.Contains("has already been taken", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCodes.RepoNameTaken;
        }

        return status switch
        {
            403 => ErrorCodes.Forbidden,
            400 or 422 => ErrorCodes.Validation,
            _ => ErrorCodes.UpstreamError,
        };
    }

    /// <summary>
    /// FR-132: a new project's name (letters, digits, emoji-free punctuation <c>_ . - +</c> and spaces, starting with a letter,
    /// digit or underscore), optional path (letters, digits, <c>_ . -</c>; not starting or ending with <c>-</c> or <c>.</c>,
    /// not ending in <c>.git</c>/<c>.atom</c>) and description. Failures are <c>VALIDATION</c> with <c>field: message</c> details.
    /// </summary>
    public static Result ValidateNewProject(string? name, string? path, string? description)
    {
        var problems = new List<string>();
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            problems.Add("name: Enter a project name.");
        }
        else if (trimmed.Length > MaxRepoNameLength)
        {
            problems.Add($"name: The name can't be longer than {MaxRepoNameLength} characters.");
        }
        else if (!ProjectNamePattern().IsMatch(trimmed))
        {
            problems.Add("name: Use letters, digits, spaces, underscores, dots, dashes or pluses, starting with a letter, digit or underscore.");
        }

        if (path is not null)
        {
            var p = path.Trim();
            if (p.Length == 0 || p.Length > MaxRepoNameLength || !ProjectPathPattern().IsMatch(p)
                || p.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("path: Use letters, digits, underscores, dots or dashes, not starting or ending with a dash or dot, and not ending in .git or .atom.");
            }
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            problems.Add($"description: The description can't be longer than {MaxDescriptionLength} characters.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The repository is invalid.") { Details = problems });
    }

    /// <summary>FR-131 token fallback: a non-empty token without whitespace, at most 1000 characters.</summary>
    public static Result ValidateToken(string? token)
    {
        var problem = string.IsNullOrWhiteSpace(token)
            ? "token: Paste a GitLab access token."
            : token.Length > MaxTokenLength
                ? $"token: The token can't be longer than {MaxTokenLength} characters."
                : token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)
                    ? "token: The token can't contain spaces or control characters."
                    : null;
        return problem is null
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The token is invalid.") { Details = [problem] });
    }

    /// <summary>
    /// FR-131: the token fallback's scope check: missing <c>api</c> or <c>write_repository</c> is
    /// <c>CONNECTION_SCOPE_INSUFFICIENT</c>, with the missing scopes as details (and in the title).
    /// </summary>
    public static Result CheckTokenScopes(IEnumerable<string> granted)
    {
        var missing = GitTokenRules.MissingGitLabScopes(granted);
        return missing.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.ConnectionScopeInsufficient, $"This token is missing permissions: {string.Join(", ", missing)}.")
            {
                Details = [.. missing],
            });
    }

    /// <summary>FR-130: an instance name: 1–200 characters after trimming.</summary>
    public static Result ValidateInstanceName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= MaxNameLength
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The GitLab instance is invalid.")
            {
                Details = [$"name: Enter a name of 1 to {MaxNameLength} characters."],
            });
    }

    [GeneratedRegex(@"^v?(\d{1,4})\.(\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-._~]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PkceVerifierPattern();

    [GeneratedRegex(@"^[\p{L}\p{N}_][\p{L}\p{N}_.\-+ ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]([A-Za-z0-9_.\-]*[A-Za-z0-9_])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPathPattern();
}
