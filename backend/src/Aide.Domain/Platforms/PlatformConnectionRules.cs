using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.Domain.Platforms;

/// <summary>How a platform request failed (FR-142), as classified by <see cref="PlatformConnectionRules.Classify"/>.</summary>
public enum PlatformFailure
{
    /// <summary>The platform rejected the token (401, or the provider's invalid-token 403).</summary>
    Rejected = 0,

    /// <summary>A 403 for missing permissions (the names where the platform returns them).</summary>
    MissingPermissions = 1,

    /// <summary>429.</summary>
    RateLimited = 2,

    NotFound = 3,

    /// <summary>Any other failure, or the platform couldn't be reached.</summary>
    Upstream = 4,
}

/// <summary>Picker data a platform connection can list (FR-142 <c>GET …/resources?type=</c>). Wire values: <c>sites</c>,
/// <c>services</c>, <c>projects</c>, <c>workers</c>, <c>orgs</c>, <c>regions</c>.</summary>
public enum PlatformResourceType
{
    Sites = 0,
    Services = 1,
    Projects = 2,
    Workers = 3,
    Orgs = 4,
    Regions = 5,
}

/// <summary>
/// FR-142, §4.20, §4.21: rules for personal platform connections (pasted tokens). Tokens are checked with the platform
/// before saving; how a check or a later use ends decides the connection's status.
/// </summary>
public static partial class PlatformConnectionRules
{
    public const int MaxLabelLength = 100;

    /// <summary>Longest accepted token (platform tokens are a few dozen to a few hundred characters).</summary>
    public const int MaxTokenLength = 1000;

    public const int MaxAccountIdLength = 200;

    /// <summary>§4.20: Cloudflare account-owned API tokens start with this prefix; Workers Builds needs a user token.</summary>
    public const string CloudflareAccountTokenPrefix = "cfat_";

    /// <summary>
    /// FR-142: a new connection: a label (1–100 characters after trimming), a well-formed token (not blank, no whitespace or
    /// control characters) and, when given, an account id (letters, digits, <c>.</c>, <c>_</c>, <c>:</c>, <c>-</c>).
    /// </summary>
    public static Result ValidateNew(string? label, string? token, string? accountId)
    {
        var problems = new List<string>();
        AddLabelProblems(label, problems);
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add("token: The token is required.");
        }
        else if (token.Length > MaxTokenLength)
        {
            problems.Add($"token: The token can't be longer than {MaxTokenLength} characters.");
        }
        else if (!ProjectSettingsRules.IsWellFormedApiKey(token))
        {
            problems.Add("token: The token can't contain spaces, line breaks or other control characters.");
        }

        if (accountId is not null && !IsAccountId(accountId))
        {
            problems.Add($"accountId: Letters, digits, '.', '_', ':' or '-', up to {MaxAccountIdLength} characters.");
        }

        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>FR-142: renaming a connection.</summary>
    public static Result ValidateLabel(string? label)
    {
        var problems = new List<string>();
        AddLabelProblems(label, problems);
        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>
    /// FR-142: the token kind is checked before calling the platform. A Cloudflare account-owned token (<c>cfat_</c>) is
    /// <c>CONNECTION_SCOPE_INSUFFICIENT</c>: Workers Builds needs a user API token.
    /// </summary>
    public static Result CheckTokenKind(PlatformProvider provider, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (provider == PlatformProvider.Cloudflare && token.StartsWith(CloudflareAccountTokenPrefix, StringComparison.Ordinal))
        {
            return Result.Failure(new DomainError(
                ErrorCodes.ConnectionScopeInsufficient,
                "This is a Cloudflare account-owned token (cfat_). Workers Builds needs a user API token: create one under My Profile → API Tokens with Workers Scripts Edit, Workers CI Edit, Account Settings Read and User Details Read.")
            {
                Details = ["user API token"],
            });
        }

        return Result.Success();
    }

    /// <summary>The token's last 4 characters (the whole token when shorter).</summary>
    public static string Last4(string token) => LlmCredential.Last4Of(token);

    /// <summary>
    /// FR-142: a platform's answer (HTTP status, its error code, whether it named missing permissions) as a failure. 429 is
    /// rate limited; 401 rejects the token, as does a Vercel 403 with <c>invalidToken</c> or a Cloudflare 403 with an
    /// invalid-token code (1000); another 403 (including Cloudflare's 9109 and 10000, which a valid token lacking a
    /// permission gets) means missing permissions; 404 not found; the rest upstream.
    /// </summary>
    public static PlatformFailure Classify(PlatformProvider provider, int? status, string? errorCode = null, bool invalidToken = false)
    {
        return status switch
        {
            429 => PlatformFailure.RateLimited,
            401 => PlatformFailure.Rejected,
            403 when invalidToken || IsInvalidTokenCode(provider, errorCode) => PlatformFailure.Rejected,
            403 => PlatformFailure.MissingPermissions,
            404 => PlatformFailure.NotFound,
            _ => PlatformFailure.Upstream,
        };
    }

    /// <summary>
    /// FR-142 for Vercel (v2's classification kept): a rejected token (401, or 403 with <c>invalidToken</c>) is rejected,
    /// another 403 is missing permissions, 429 rate limited, 404 not found, anything else upstream.
    /// </summary>
    public static PlatformFailure FromVercel(Deployments.DeploymentFailure failure) => failure switch
    {
        Deployments.DeploymentFailure.CredentialRejected => PlatformFailure.Rejected,
        Deployments.DeploymentFailure.Forbidden => PlatformFailure.MissingPermissions,
        Deployments.DeploymentFailure.RateLimited => PlatformFailure.RateLimited,
        Deployments.DeploymentFailure.NotFound => PlatformFailure.NotFound,
        _ => PlatformFailure.Upstream,
    };

    /// <summary>
    /// FR-142 for Railway, whose GraphQL API answers 200 with <c>errors</c>: <c>Not Authorized</c> rejects the token, a rate
    /// limit message is rate limited, <c>not found</c> is not found, anything else upstream.
    /// </summary>
    public static PlatformFailure ClassifyRailwayError(string? message)
    {
        var lower = message?.ToLowerInvariant() ?? string.Empty;
        return lower.Contains("not authorized", StringComparison.Ordinal) || lower.Contains("unauthorized", StringComparison.Ordinal) ? PlatformFailure.Rejected
            : lower.Contains("rate limit", StringComparison.Ordinal) || lower.Contains("too many requests", StringComparison.Ordinal) ? PlatformFailure.RateLimited
            : lower.Contains("not found", StringComparison.Ordinal) ? PlatformFailure.NotFound
            : PlatformFailure.Upstream;
    }

    /// <summary>
    /// FR-142: the status a check ends in: <c>valid</c>, or <c>insufficient_scope</c> when permissions are missing (a 403 for
    /// missing permissions, or names the platform listed); <c>invalid</c> when the token was rejected; <c>null</c> (status
    /// unchanged) when the platform failed or rate limited.
    /// </summary>
    public static PlatformConnectionStatus? StatusAfter(PlatformFailure? failure, int missingPermissions) => failure switch
    {
        null => missingPermissions > 0 ? PlatformConnectionStatus.InsufficientScope : PlatformConnectionStatus.Valid,
        PlatformFailure.Rejected => PlatformConnectionStatus.Invalid,
        PlatformFailure.MissingPermissions => PlatformConnectionStatus.InsufficientScope,
        _ => null,
    };

    /// <summary>
    /// FR-142: the error for a failed check on save (nothing is saved): rejected → <c>CREDENTIAL_INVALID</c>, 429 →
    /// <c>UPSTREAM_RATE_LIMITED</c>, anything else → <c>UPSTREAM_ERROR</c>. Missing permissions isn't an error on save: the
    /// connection is saved as <c>insufficient_scope</c> (<c>null</c>).
    /// </summary>
    public static DomainError? SaveError(PlatformProvider provider, PlatformFailure failure) => failure switch
    {
        PlatformFailure.MissingPermissions => null,
        PlatformFailure.Rejected => new DomainError(
            ErrorCodes.CredentialInvalid, $"{DisplayName(provider)} rejected the token. Check it was copied completely and hasn't expired."),
        PlatformFailure.RateLimited => new DomainError(
            ErrorCodes.UpstreamRateLimited, $"{DisplayName(provider)} is rate limiting requests. Try again in a few minutes."),
        _ => new DomainError(ErrorCodes.UpstreamError, $"The token couldn't be checked: {DisplayName(provider)} failed or couldn't be reached."),
    };

    /// <summary>
    /// FR-142: the error when a saved connection is used (re-test, picker): rejected → <c>CREDENTIAL_INVALID</c> (the
    /// connection is now invalid), missing permissions → <c>CONNECTION_SCOPE_INSUFFICIENT</c>, 429 →
    /// <c>UPSTREAM_RATE_LIMITED</c>, not found → <c>NOT_FOUND</c>, anything else → <c>UPSTREAM_ERROR</c>.
    /// </summary>
    public static DomainError UseError(PlatformProvider provider, PlatformFailure failure, IReadOnlyList<string>? missing = null) => failure switch
    {
        PlatformFailure.Rejected => new DomainError(
            ErrorCodes.CredentialInvalid, $"{DisplayName(provider)} rejected the connection's token; it is now marked invalid. Add a new token in Account → Connections."),
        PlatformFailure.MissingPermissions => new DomainError(
            ErrorCodes.ConnectionScopeInsufficient, $"The {DisplayName(provider)} token is missing permissions for this.")
        { Details = missing ?? [] },
        PlatformFailure.RateLimited => new DomainError(
            ErrorCodes.UpstreamRateLimited, $"{DisplayName(provider)} is rate limiting requests. Try again in a few minutes."),
        PlatformFailure.NotFound => new DomainError(ErrorCodes.NotFound, $"{DisplayName(provider)} couldn't find that, or the token can't see it."),
        _ => new DomainError(ErrorCodes.UpstreamError, $"{DisplayName(provider)} returned an error or couldn't be reached. Try again later."),
    };

    /// <summary>FR-142: the owner is notified (<c>CONNECTION_INVALID</c>) when a connection becomes invalid, once.</summary>
    public static bool NotifiesInvalid(PlatformConnectionStatus before, PlatformConnectionStatus after) =>
        after == PlatformConnectionStatus.Invalid && before != PlatformConnectionStatus.Invalid;

    /// <summary>FR-142: what each provider's picker lists; the first is the default when no type is asked for.</summary>
    public static IReadOnlyList<PlatformResourceType> ResourceTypes(PlatformProvider provider) => provider switch
    {
        PlatformProvider.Vercel => [PlatformResourceType.Projects],
        PlatformProvider.Netlify => [PlatformResourceType.Sites],
        PlatformProvider.Cloudflare => [PlatformResourceType.Workers, PlatformResourceType.Projects],
        PlatformProvider.Render => [PlatformResourceType.Services, PlatformResourceType.Regions],
        PlatformProvider.Railway => [PlatformResourceType.Projects, PlatformResourceType.Regions],
        PlatformProvider.Supabase => [PlatformResourceType.Projects, PlatformResourceType.Orgs, PlatformResourceType.Regions],
        _ => [],
    };

    /// <summary>
    /// FR-142: the picker type asked for (<paramref name="type"/>; <c>null</c>: the provider's default), or
    /// <c>VALIDATION</c> for an unknown type or one the provider doesn't list.
    /// </summary>
    public static Result<PlatformResourceType> ResourceType(PlatformProvider provider, string? type)
    {
        var supported = ResourceTypes(provider);
        if (type is null)
        {
            return Result.Success(supported[0]);
        }

        if (EnumStrings.TryParse<PlatformResourceType>(type, out var parsed) && supported.Contains(parsed))
        {
            return Result.Success(parsed);
        }

        return Result.Failure<PlatformResourceType>(new DomainError(ErrorCodes.Validation, "The resource type is invalid.")
        {
            Details = [$"type: {DisplayName(provider)} lists {string.Join(", ", supported.Select(t => t.ToWire()))}."],
        });
    }

    /// <summary>An account, team, workspace or organisation id as platforms issue them.</summary>
    public static bool IsAccountId(string? accountId) =>
        accountId is { Length: > 0 and <= MaxAccountIdLength } && AccountIdPattern().IsMatch(accountId);

    /// <summary>
    /// FR-142: the account a connection is for: the one asked for (<paramref name="requested"/>), else the only one the token
    /// reaches, else <c>null</c> (the user picks one later).
    /// </summary>
    public static PlatformAccountRef? ChooseAccount(IReadOnlyList<PlatformAccountRef> accounts, string? requested)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (requested is not null)
        {
            return accounts.FirstOrDefault(a => string.Equals(a.Id, requested, StringComparison.Ordinal)
                || string.Equals(a.Slug, requested, StringComparison.Ordinal));
        }

        return accounts.Count == 1 ? accounts[0] : null;
    }

    /// <summary>The platform's name in messages.</summary>
    public static string DisplayName(PlatformProvider provider) => provider switch
    {
        PlatformProvider.Vercel => "Vercel",
        PlatformProvider.Netlify => "Netlify",
        PlatformProvider.Cloudflare => "Cloudflare",
        PlatformProvider.Render => "Render",
        PlatformProvider.Railway => "Railway",
        PlatformProvider.Supabase => "Supabase",
        _ => "The platform",
    };

    /// <summary>
    /// FR-142: the names to record for a 403 for missing permissions: the platform's, or <paramref name="placeholder"/>
    /// (the permission probed, or <see cref="UnknownPermission"/>) when it named none, so the connection still reads
    /// <c>insufficient_scope</c>.
    /// </summary>
    public static IReadOnlyList<string> MissingOrPlaceholder(IReadOnlyList<string>? named, string placeholder = UnknownPermission) =>
        named is { Count: > 0 } ? named : [placeholder];

    /// <summary>Recorded when a platform refuses for a missing permission without naming it.</summary>
    public const string UnknownPermission = "unknown";

    /// <summary>
    /// Cloudflare's invalid-token error code (1000, invalid API token). Codes 9109 and 10000 also come back for a valid
    /// token that lacks a permission, so they are missing permissions, not a rejection.
    /// </summary>
    private static bool IsInvalidTokenCode(PlatformProvider provider, string? errorCode) =>
        provider == PlatformProvider.Cloudflare && errorCode is "1000";

    private static void AddLabelProblems(string? label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            problems.Add("label: The label is required.");
        }
        else if (label.Trim().Length > MaxLabelLength)
        {
            problems.Add($"label: The label can't be longer than {MaxLabelLength} characters.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountIdPattern();
}

/// <summary>FR-142 picker data that needs no API call: the regions a platform offers (as documented in 2026).</summary>
public static class PlatformRegions
{
    /// <summary>Render service and Postgres regions.</summary>
    public static IReadOnlyList<string> Render { get; } = ["oregon", "ohio", "virginia", "frankfurt", "singapore"];

    /// <summary>Supabase project regions (<c>region_selection: {type: "specific", code}</c>).</summary>
    public static IReadOnlyList<string> Supabase { get; } =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "sa-east-1",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-central-2", "eu-north-1",
        "ap-south-1", "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ap-northeast-2",
    ];
}
