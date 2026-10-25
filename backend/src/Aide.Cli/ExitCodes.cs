using System.Net;

namespace Aide.Cli;

/// <summary>Process exit codes (SPEC FR-52, FR-93).</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Auth = 3;
    public const int Conflict = 4;

    /// <summary>
    /// FR-93: release-version errors are conflicts with the project's versions or tags whatever their HTTP status
    /// (<c>VERSION_INVALID</c> is a 400), so scripts can tell them apart from other failures. FR-156 adds
    /// <c>LLM_PROVIDER_UNAVAILABLE</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> ConflictErrorCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "VERSION_INVALID",
        "VERSION_NOT_GREATER",
        "VERSION_IN_PROGRESS",
        "TAG_EXISTS",
        "RELEASE_NOT_MERGED",
        "NOT_RELEASED",

        // FR-156: the chosen LLM provider is disabled on the server or not usable by the caller (a 400).
        "LLM_PROVIDER_UNAVAILABLE",
    };

    /// <summary>401/403 → auth, 409 → conflict, anything else that failed → error.</summary>
    public static int ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Auth,
        HttpStatusCode.Conflict => Conflict,
        _ => Error,
    };

    /// <summary><see cref="ForStatus"/>, except that the release-version error codes (<see cref="ConflictErrorCodes"/>) are always 4.</summary>
    public static int ForProblem(HttpStatusCode status, string? errorCode) =>
        errorCode is not null && ConflictErrorCodes.Contains(errorCode) ? Conflict : ForStatus(status);
}
