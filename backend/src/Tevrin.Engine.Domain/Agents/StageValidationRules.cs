using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>One JSON Schema error in an agent's output: the JSON Pointer of the value (<c>/</c> for the root) and why.</summary>
public sealed record SchemaFinding(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>What the stage does with an output that failed validation (§4.55).</summary>
public enum InvalidOutputAction
{
    /// <summary>Run the agent again with the findings as feedback (trigger <c>validation_failed</c>).</summary>
    Retry = 0,

    /// <summary>Raise a pending <c>escalation</c> gate on the run.</summary>
    Escalate = 1,

    /// <summary>The agent run and its job end <c>failed</c>.</summary>
    Fail = 2,
}

/// <summary>FR-296, §4.55: the pure rules of <c>validate:</c> and <c>on_invalid:</c>.</summary>
public static class StageValidationRules
{
    /// <summary>Findings are capped at 8,000 characters (§4.55).</summary>
    public const int MaxFindingsChars = 8000;

    /// <summary>
    /// The <c>error</c> prefix of an agent run whose output was rejected by validation (D-0.7.0-33): the run keeps status
    /// <c>succeeded</c> (the call worked) but counts as a failure in the prompt comparison (FR-298).
    /// </summary>
    public const string RejectedErrorPrefix = "validation_rejected: ";

    private const char Ellipsis = '…';

    /// <summary>The <c>error</c> of a rejected attempt: <see cref="RejectedErrorPrefix"/> and the findings.</summary>
    public static string RejectedError(string findings) => RejectedErrorPrefix + findings;

    /// <summary>
    /// §4.55: the feedback for the next attempt: the request's own feedback (e.g. a rejection comment) kept, with the
    /// findings appended.
    /// </summary>
    public static string RetryFeedback(string? originalFeedback, string findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return string.IsNullOrWhiteSpace(originalFeedback) ? findings : $"{originalFeedback.TrimEnd()}\n\n{findings}";
    }

    /// <summary>
    /// The findings of a rejected output: the guards' reasons, then the schema errors (<c>path: message</c>), one per line,
    /// capped at <see cref="MaxFindingsChars"/> characters (cut on a character boundary, ending with an ellipsis).
    /// </summary>
    public static string Findings(IEnumerable<string> guardReasons, IEnumerable<SchemaFinding> schemaErrors)
    {
        ArgumentNullException.ThrowIfNull(guardReasons);
        ArgumentNullException.ThrowIfNull(schemaErrors);
        var lines = guardReasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
            .Concat(schemaErrors.Select(e => e.ToString()));
        return Cap(string.Join("\n", lines));
    }

    /// <summary><paramref name="text"/> cut to at most <see cref="MaxFindingsChars"/> characters (a surrogate pair stays whole).</summary>
    public static string Cap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxFindingsChars)
        {
            return text;
        }

        var length = MaxFindingsChars - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), Ellipsis.ToString());
    }

    /// <summary>
    /// §4.55: attempt <paramref name="attempt"/> (1-based) failed validation. Retry while <c>retry_with_feedback</c> and
    /// <paramref name="attempt"/> &lt; <c>max_attempts</c>; else follow <c>then</c>. A stage with <c>validate:</c> but no
    /// <c>on_invalid</c> fails at once (D-0.7.0-33).
    /// </summary>
    public static InvalidOutputAction OnInvalid(OnInvalidPolicy? policy, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (policy is null)
        {
            return InvalidOutputAction.Fail;
        }

        if (policy.RetryWithFeedback && attempt < policy.MaxAttempts)
        {
            return InvalidOutputAction.Retry;
        }

        return policy.Then == InvalidOutcome.Escalate ? InvalidOutputAction.Escalate : InvalidOutputAction.Fail;
    }
}
