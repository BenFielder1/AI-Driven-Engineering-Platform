namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// The <c>errorCode</c> values the engine's own rules return (SPEC.md §6). The host's error table (<c>ErrorCodes</c> in
/// Tevrin.Domain) uses the same values and maps every code to its HTTP status.
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

    // FR-330–FR-335 (0.8.0): run control (the host's values).
    public const string RunControlledByHost = "RUN_CONTROLLED_BY_HOST";
    public const string RunAlreadyActive = "RUN_ALREADY_ACTIVE";
    public const string RunNotActive = "RUN_NOT_ACTIVE";
    public const string InputInvalid = "INPUT_INVALID";
    public const string GateNotPending = "GATE_NOT_PENDING";

    // FR-352 (0.8.0): a project-scope artifact the run needs has no locked version.
    public const string SourceArtifactMissing = "SOURCE_ARTIFACT_MISSING";

    // FR-336, FR-337 (0.8.0): schedule and webhook triggers (the host's values).
    public const string ScheduleInvalid = "SCHEDULE_INVALID";
    public const string TriggerNotSupported = "TRIGGER_NOT_SUPPORTED";
    public const string WebhookSignatureInvalid = "WEBHOOK_SIGNATURE_INVALID";
    public const string WebhookReplay = "WEBHOOK_REPLAY";

    /// <summary>FR-345, §4.65, §6 (job failure): a fan-out branch's rendered input held a sibling branch's output.</summary>
    public const string FanoutIsolationBroken = "FANOUT_ISOLATION_BROKEN";

    /// <summary>FR-349, §4.67, §6: a variant choice outside <c>v1…vn</c> (a job failure for an agent selector; 400 on a
    /// <c>variant_select</c> gate's approve).</summary>
    public const string VariantChoiceInvalid = "VARIANT_CHOICE_INVALID";

    /// <summary>FR-350, §6: a validator agent's answer isn't valid JSON matching the verdict schema (a job failure).</summary>
    public const string ValidatorInvalidOutput = "VALIDATOR_INVALID_OUTPUT";
}
