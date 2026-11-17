namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// A business-rule failure: an <c>errorCode</c> value (<see cref="EngineErrorCodes"/>, or the host's <c>ErrorCodes</c>) plus a
/// human-readable reason. The API maps it to ProblemDetails (status from the host's <c>ErrorCodes.StatusCodeFor</c>,
/// <c>errorCode</c> extension).
/// </summary>
public sealed record DomainError(string Code, string Message)
{
    /// <summary>Individual reasons (e.g. every unmet precondition or plan problem), for the ProblemDetails body.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>For <c>ErrorCodes.RateLimited</c>: how long until the action is allowed again.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>A machine-readable sub-reason (e.g. <c>RestoreReasons</c> for <c>ErrorCodes.RestoreNotAllowed</c>), returned as the ProblemDetails <c>reason</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// For <c>ErrorCodes.StackIncompatible</c> (FR-116): the broken rules, returned as the ProblemDetails <c>violations</c>.
    /// The items are pack-defined records (the SDLC's <c>StackViolation</c>); the engine doesn't interpret them.
    /// </summary>
    public IReadOnlyList<object> Violations { get; init; } = [];

    /// <summary>
    /// The request field the error is about (e.g. <c>git.connectionId</c>), returned as the ProblemDetails <c>errors</c>
    /// entry <c>{field: [message]}</c> (the VALIDATION shape), so a client can tell which choice failed.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// For <c>ErrorCodes.ConnectionScopeInsufficient</c> (FR-134): the scopes Tevrin couldn't confirm the token has (no
    /// harmless call could run for them), returned as the ProblemDetails <c>unverifiableScopes</c>, next to the missing
    /// scopes in <see cref="Details"/>.
    /// </summary>
    public IReadOnlyList<string> UnverifiableScopes { get; init; } = [];
}
