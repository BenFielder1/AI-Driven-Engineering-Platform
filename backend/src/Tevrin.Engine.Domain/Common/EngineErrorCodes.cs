namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// The <c>errorCode</c> values the engine's own rules return (SPEC.md §6). The host's error table (<c>ErrorCodes</c> in
/// Aide.Domain) uses the same values and maps every code to its HTTP status.
/// </summary>
public static class EngineErrorCodes
{
    public const string Validation = "VALIDATION";
    public const string Conflict = "CONFLICT";
}
