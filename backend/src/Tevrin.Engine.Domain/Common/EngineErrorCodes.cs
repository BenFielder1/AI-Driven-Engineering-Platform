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

    // FR-287, FR-288: the pipeline state machine's codes (the values of 0.6.1's lifecycle codes).
    public const string PhasePreconditionFailed = "PHASE_PRECONDITION_FAILED";
    public const string GatePending = "GATE_PENDING";
    public const string WrongPhase = "WRONG_PHASE";
    public const string InvalidTargetVersion = "INVALID_TARGET_VERSION";

    /// <summary>A tool result (not an API error): the run used its tool calls or bytes and must answer with what it has.</summary>
    public const string ToolLimit = "TOOL_LIMIT";

    // FR-299–FR-303: the generic API's codes (the host's values).
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string DefinitionUnavailable = "DEFINITION_UNAVAILABLE";
}
