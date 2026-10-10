using System.Net;

namespace Aide.Cli;

/// <summary>Process exit codes (SPEC FR-52).</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Auth = 3;
    public const int Conflict = 4;

    /// <summary>401/403 → auth, 409 → conflict, anything else that failed → error.</summary>
    public static int ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Auth,
        HttpStatusCode.Conflict => Conflict,
        _ => Error,
    };
}
