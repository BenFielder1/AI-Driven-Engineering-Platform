namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// The <c>errorCode</c> values the engine's own rules return (SPEC.md §6). The host's error table (<c>ErrorCodes</c> in
/// Aide.Domain) uses the same values and maps every code to its HTTP status.
/// </summary>
public static class EngineErrorCodes
{
    public const string Validation = "VALIDATION";
    public const string Conflict = "CONFLICT";
    public const string BudgetExceeded = "BUDGET_EXCEEDED";
    public const string ArtifactNotDraft = "ARTIFACT_NOT_DRAFT";
    public const string ArtifactLocked = "ARTIFACT_LOCKED";
    public const string MissingInput = "MISSING_INPUT";
    public const string ProviderNotConfigured = "PROVIDER_NOT_CONFIGURED";
    public const string ProviderError = "PROVIDER_ERROR";

    /// <summary>A tool result (not an API error): the run used its tool calls or bytes and must answer with what it has.</summary>
    public const string ToolLimit = "TOOL_LIMIT";
}
