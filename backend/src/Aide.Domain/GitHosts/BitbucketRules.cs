using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.GitHosts;

/// <summary>
/// The outcome of one harmless Bitbucket call made to check that an API token has a scope (FR-134). Wire values aren't
/// exposed.
/// </summary>
public enum BitbucketScopeProbe
{
    /// <summary>The call got past Bitbucket's scope check (2xx, or a 400/409/422 for the deliberately invalid request).</summary>
    Granted = 0,

    /// <summary>Bitbucket refused the call for a missing scope (403 naming scopes).</summary>
    Missing = 1,

    /// <summary>The call couldn't tell (nothing to try it on, a 404, or a 403 for repository permissions).</summary>
    Unverified = 2,
}

/// <summary>
/// FR-134: the API token scope check's result: <see cref="Missing"/> scopes Bitbucket refused, and
/// <see cref="Unverifiable"/> ones no harmless call could confirm. A token is saved only when both are empty.
/// </summary>
public sealed record BitbucketScopeVerdict(IReadOnlyList<string> Missing, IReadOnlyList<string> Unverifiable)
{
    public bool AllVerified => Missing.Count == 0 && Unverifiable.Count == 0;
}

/// <summary>A Bitbucket email address from <c>GET /2.0/user/emails</c>.</summary>
public sealed record BitbucketEmail(string Email, bool IsPrimary, bool IsConfirmed);

/// <summary>
/// A position in the Bitbucket repo picker (FR-135): the workspace slug (<c>null</c>: the first workspace) and the page.
/// </summary>
public sealed record BitbucketRepoCursor(string? Workspace, int Page);

/// <summary>
/// FR-133–FR-135, §4.19: the pure Bitbucket Cloud rules: the fixed hosts, the consumer permissions to tick, the API token
/// scope check, the verified sign-in email, the repo picker query and cursor, repository slugs and the create-repository
/// error mapping, and request validation. Failures are <c>VALIDATION</c> with <c>field: message</c> details.
/// </summary>
public static partial class BitbucketRules
{
    /// <summary>§4.19: the REST API (fixed; never configurable, so no SSRF surface).</summary>
    public const string ApiBaseUrl = "https://api.bitbucket.org/2.0";

    /// <summary>§4.19: the web host (OAuth endpoints and Git over HTTPS).</summary>
    public const string WebBaseUrl = "https://bitbucket.org";

    /// <summary>The name of the Bitbucket Cloud row in <c>git_host_instances</c>.</summary>
    public const string CloudName = "Bitbucket Cloud";

    /// <summary>FR-133: the OAuth callback path registered on the consumer.</summary>
    public const string CallbackPath = "/api/auth/bitbucket/callback";

    public const int MaxTokenLength = 1000;

    public const int MaxEmailLength = 320;

    public const int MaxSearchLength = 100;

    /// <summary>FR-135: <c>workspace slug:page</c>.</summary>
    public const int MaxCursorLength = 120;

    /// <summary>Bitbucket repository slugs are at most 62 characters.</summary>
    public const int MaxSlugLength = 62;

    public const int MaxRepoNameLength = 255;

    public const int MaxDescriptionLength = 2000;

    public const int MaxWorkspaceLength = 100;

    public const int MaxProjectKeyLength = 128;

    /// <summary>FR-135: repos per picker page.</summary>
    public const int ReposPerPage = 30;

    /// <summary>
    /// FR-133: the permissions to tick on the Bitbucket OAuth consumer (Bitbucket fixes scopes on the consumer, §4.19).
    /// </summary>
    public static IReadOnlyList<string> ConsumerPermissions { get; } =
        ["account", "email", "repository:write", "repository:admin", "pullrequest:write", "project", "webhook"];

    /// <summary>
    /// FR-134: the repository slug of the <c>write:repository</c> probe, <c>POST /repositories/{ws}/{slug}</c>. It is not a
    /// valid Bitbucket slug (<c>!</c> isn't allowed, <see cref="IsValidSlug"/>), and the probe's body is invalid too
    /// (<see cref="ScopeProbeCreateBody"/>), so Bitbucket rejects the request after its scope check and nothing is created.
    /// </summary>
    public const string ScopeProbeSlug = "aide-scope-check!";

    /// <summary>
    /// FR-134: the body of the <c>write:repository</c> probe: an unknown <c>scm</c> and an impossible project key, each of
    /// which Bitbucket rejects with a 400 on its own.
    /// </summary>
    public const string ScopeProbeCreateBody = """{"scm":"aide-scope-check","project":{"key":"!"}}""";

    /// <summary>A valid Bitbucket repository slug: non-empty and already in the form <see cref="Slug"/> produces.</summary>
    public static bool IsValidSlug(string? slug) => !string.IsNullOrEmpty(slug) && string.Equals(Slug(slug), slug, StringComparison.Ordinal);

    /// <summary>
    /// FR-134: the scope list Bitbucket reported (the <c>X-OAuth-Scopes</c> header, or the <c>granted</c> list of a scope
    /// error), usable only when every entry is an API-token scope (<c>…:bitbucket</c>). An OAuth-style list
    /// (<c>repository:write</c>, <c>pullrequest:write</c>…) doesn't map one to one onto API-token scopes, so it is ignored
    /// (<c>null</c>) and the scopes are probed one by one instead: a valid token is never refused wholesale.
    /// </summary>
    public static IReadOnlyList<string>? ApiTokenScopes(IEnumerable<string>? reported)
    {
        var list = reported?.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return list is { Count: > 0 } && list.All(s => s.EndsWith(":bitbucket", StringComparison.Ordinal)) ? list : null;
    }

    /// <summary>
    /// FR-134: the verdict on an API token's scopes. With a usable scope list from Bitbucket (<paramref name="granted"/>, see
    /// <see cref="ApiTokenScopes"/>) that list decides every scope. Otherwise each scope's harmless call decides: refused for
    /// scopes is missing; not run, a 404 or a permission 403 is unverifiable. Both lists keep the required order.
    /// </summary>
    public static BitbucketScopeVerdict EvaluateTokenScopes(IReadOnlyCollection<string>? granted, IReadOnlyDictionary<string, BitbucketScopeProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (ApiTokenScopes(granted) is { } usable)
        {
            return new BitbucketScopeVerdict(GitTokenRules.MissingBitbucketScopes(usable), []);
        }

        var missing = new List<string>();
        var unverifiable = new List<string>();
        foreach (var scope in GitTokenRules.BitbucketTokenScopes)
        {
            var probe = probes.TryGetValue(scope, out var p) ? p : BitbucketScopeProbe.Unverified;
            if (probe == BitbucketScopeProbe.Missing)
            {
                missing.Add(scope);
            }
            else if (probe == BitbucketScopeProbe.Unverified)
            {
                unverifiable.Add(scope);
            }
        }

        return new BitbucketScopeVerdict(missing, unverifiable);
    }

    /// <summary>
    /// FR-134: the token fallback's scope check. A token is saved only when every scope is verified: missing scopes are
    /// <c>CONNECTION_SCOPE_INSUFFICIENT</c> with the scopes as details (and in the title), and unverifiable ones are refused
    /// the same way, listed separately (<see cref="DomainError.UnverifiableScopes"/>) with the way out: connect with OAuth,
    /// or create a repository in the workspace first.
    /// </summary>
    public static Result CheckTokenScopes(BitbucketScopeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.AllVerified)
        {
            return Result.Success();
        }

        var parts = new List<string>();
        if (verdict.Missing.Count > 0)
        {
            parts.Add($"This token is missing permissions: {string.Join(", ", verdict.Missing)}.");
        }

        if (verdict.Unverifiable.Count > 0)
        {
            parts.Add($"AIDE couldn't confirm these permissions: {string.Join(", ", verdict.Unverifiable)}. Use \"Connect with Bitbucket\" (OAuth) instead, or create a repository in the workspace first and try again.");
        }

        return Result.Failure(new DomainError(ErrorCodes.ConnectionScopeInsufficient, string.Join(" ", parts))
        {
            Details = [.. verdict.Missing],
            UnverifiableScopes = [.. verdict.Unverifiable],
        });
    }

    /// <summary>
    /// FR-134: what a probe's answer says about its scope. Anything past the scope check counts as granted: 2xx, and the
    /// validation errors the invalid write probes are built to get (400, 409, 422). A 403 is a missing scope only when
    /// Bitbucket says it is about scopes (<paramref name="scopeError"/>); another 403 (repository permissions) and a 404 (the
    /// lookup may come before the scope check) prove nothing. 401, 429 and 5xx aren't classified here (the caller treats
    /// them as errors).
    /// </summary>
    public static BitbucketScopeProbe ClassifyProbe(int status, bool scopeError) => status switch
    {
        >= 200 and < 300 => BitbucketScopeProbe.Granted,
        400 or 409 or 422 => BitbucketScopeProbe.Granted,
        403 => scopeError ? BitbucketScopeProbe.Missing : BitbucketScopeProbe.Unverified,
        _ => BitbucketScopeProbe.Unverified,
    };

    /// <summary>
    /// FR-134 sign-in: the email AIDE may trust: the primary address if Bitbucket confirmed it, else the first confirmed one;
    /// <c>null</c> when none is confirmed.
    /// </summary>
    public static string? VerifiedEmail(IEnumerable<BitbucketEmail> emails)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var confirmed = emails.Where(e => e.IsConfirmed && !string.IsNullOrWhiteSpace(e.Email)).ToList();
        var chosen = confirmed.FirstOrDefault(e => e.IsPrimary) ?? confirmed.FirstOrDefault();
        return chosen?.Email.Trim();
    }

    /// <summary>
    /// FR-135: the repo picker's BBQL filter <c>name~"…"</c> (case-insensitive contains), with <c>\</c> and <c>"</c> escaped;
    /// <c>null</c> without a search.
    /// </summary>
    public static string? RepoQuery(string? search)
    {
        var term = search?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        var escaped = term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"name~\"{escaped}\"";
    }

    /// <summary>
    /// FR-135: the picker cursor (<c>workspace slug:page</c>); an empty one is the first page of the first workspace.
    /// </summary>
    public static Result<BitbucketRepoCursor> ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return Result.Success(new BitbucketRepoCursor(null, 1));
        }

        var match = CursorPattern().Match(cursor.Trim());
        if (!match.Success
            || match.Groups[1].Value.Length > MaxWorkspaceLength
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            || page < 1)
        {
            return Result.Failure<BitbucketRepoCursor>(new DomainError(ErrorCodes.Validation, "The request is invalid.")
            {
                Details = ["cursor: Use the nextCursor of the previous page."],
            });
        }

        return Result.Success(new BitbucketRepoCursor(match.Groups[1].Value, page));
    }

    /// <summary>
    /// FR-135: the cursor after a page of <paramref name="workspace"/>: its next page when Bitbucket sent a <c>next</c> link,
    /// else the first page of the workspace after it in <paramref name="workspaces"/>, else <c>null</c> (the last page; also
    /// when the workspace is no longer in the list and has no more pages).
    /// </summary>
    public static string? NextCursor(IReadOnlyList<string> workspaces, string workspace, int page, bool hasNext)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (hasNext)
        {
            return FormatCursor(workspace, page + 1);
        }

        var index = -1;
        for (var i = 0; i < workspaces.Count; i++)
        {
            if (string.Equals(workspaces[i], workspace, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < workspaces.Count ? FormatCursor(workspaces[index + 1], 1) : null;
    }

    /// <summary>
    /// FR-135: Bitbucket's slug for a repository name: lower case, runs of spaces and other characters as one <c>-</c>,
    /// only <c>a-z 0-9 . _ -</c>, no leading or trailing <c>-</c> or <c>.</c>, at most 62 characters.
    /// </summary>
    public static string Slug(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = DashRuns().Replace(builder.ToString(), "-").Trim('-', '.');
        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].Trim('-', '.');
    }

    /// <summary>
    /// FR-135: a new repository: a name with a non-empty slug (≤ 255 characters), a workspace slug (letters, digits,
    /// <c>_ . -</c>), the required project key (a letter, then letters, digits or underscores) and an optional description.
    /// </summary>
    public static Result ValidateNewRepo(string? workspace, string? projectKey, string? name, string? description)
    {
        var problems = new List<string>();
        var ws = workspace?.Trim() ?? string.Empty;
        if (ws.Length == 0 || ws.Length > MaxWorkspaceLength || !WorkspacePattern().IsMatch(ws))
        {
            problems.Add("owner: Choose the workspace to create the repository in.");
        }

        var key = projectKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            problems.Add("project: Choose a project. Bitbucket puts every repository in a project.");
        }
        else if (key.Length > MaxProjectKeyLength || !ProjectKeyPattern().IsMatch(key))
        {
            problems.Add("project: Use the key of a project in the workspace.");
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            problems.Add("name: Enter a repository name.");
        }
        else if (trimmed.Length > MaxRepoNameLength)
        {
            problems.Add($"name: The name can't be longer than {MaxRepoNameLength} characters.");
        }
        else if (Slug(trimmed).Length == 0)
        {
            problems.Add("name: Use at least one letter or digit.");
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            problems.Add($"description: The description can't be longer than {MaxDescriptionLength} characters.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The repository is invalid.") { Details = problems });
    }

    /// <summary>
    /// FR-135: the AIDE error for a failed <c>POST /repositories/{ws}/{slug}</c>: a slug that already exists (Bitbucket's
    /// 400 "… already exists") is <c>REPO_NAME_TAKEN</c>; 429 <c>UPSTREAM_RATE_LIMITED</c>; 403 <c>FORBIDDEN</c>; 404
    /// <c>NOT_FOUND</c> (no such workspace); another 400 <c>VALIDATION</c>; anything else <c>UPSTREAM_ERROR</c>.
    /// </summary>
    public static string CreateRepoErrorCode(int status, string? message)
    {
        var text = message ?? string.Empty;
        if (status == 429)
        {
            return ErrorCodes.UpstreamRateLimited;
        }

        if (status is 400 or 409 && text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCodes.RepoNameTaken;
        }

        return status switch
        {
            403 => ErrorCodes.Forbidden,
            404 => ErrorCodes.NotFound,
            400 => ErrorCodes.Validation,
            _ => ErrorCodes.UpstreamError,
        };
    }

    /// <summary>
    /// FR-134 token fallback: a non-empty API token without whitespace (≤ 1000 characters) and the Atlassian account email
    /// it belongs to. App passwords aren't supported (Atlassian removed them); they can't be told apart by shape, and
    /// Bitbucket rejects them for scoped calls.
    /// </summary>
    public static Result ValidateToken(string? token, string? email)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add("token: Paste an Atlassian API token with Bitbucket scopes.");
        }
        else if (token.Length > MaxTokenLength)
        {
            problems.Add($"token: The token can't be longer than {MaxTokenLength} characters.");
        }
        else if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
        {
            problems.Add("token: The token can't contain spaces or control characters.");
        }

        var address = email?.Trim() ?? string.Empty;
        if (address.Length == 0)
        {
            problems.Add("email: Enter the email of the Atlassian account the token belongs to.");
        }
        else if (address.Length > MaxEmailLength || !EmailPattern().IsMatch(address))
        {
            problems.Add("email: Enter a valid email address.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The token is invalid.") { Details = problems });
    }

    /// <summary>Granted scopes from a space- or comma-separated list (token response <c>scopes</c>, <c>X-OAuth-Scopes</c>).</summary>
    public static IReadOnlyList<string> ParseScopes(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : [.. scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private static string FormatCursor(string workspace, int page) =>
        string.Create(CultureInfo.InvariantCulture, $"{workspace}:{page}");

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9_.\-]*):(\d{1,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex CursorPattern();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DashRuns();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspacePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectKeyPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
