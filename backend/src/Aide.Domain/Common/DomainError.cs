namespace Aide.Domain.Common;

/// <summary>
/// A business-rule failure: an <see cref="ErrorCodes"/> value plus a human-readable reason.
/// The API maps it to ProblemDetails (status from <see cref="ErrorCodes.StatusCodeFor"/>, <c>errorCode</c> extension).
/// </summary>
public sealed record DomainError(string Code, string Message)
{
    /// <summary>Individual reasons (e.g. every unmet precondition or plan problem), for the ProblemDetails body.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>For <see cref="ErrorCodes.RateLimited"/>: how long until the action is allowed again.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
