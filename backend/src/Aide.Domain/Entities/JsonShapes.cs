using Aide.Domain.Execution;
using Aide.Domain.Review;
using Tevrin.Engine.Domain.Entities;

namespace Aide.Domain.Entities;

// Shapes of the jsonb columns. Enums inside them use the same snake_case wire values as columns.

/// <summary>A file written by an accepted step (FR-34): path, action and SHA-256 of the content.</summary>
public sealed record FileManifestEntry(string Path, FileAction Action, string ContentHash);

/// <summary>An artifact version an agent run used as input (FR-23).</summary>
public sealed record InputArtifactRef(Guid ArtifactId, string Type, int Version);

/// <summary>A compliance category score (FR-38, §4.7): the agent's 0–1 score, the deterministic penalty applied to it,
/// the resulting <see cref="Score"/> (floored at 0) and the agent's evidence. Reports written before the breakdown existed
/// read with <see cref="AgentScore"/> and <see cref="Penalty"/> 0 and their evidence string as one reason.</summary>
public sealed record CategoryScoreEntry(
    ReviewCategory Category,
    decimal Score,
    decimal AgentScore = 0m,
    decimal Penalty = 0m,
    IReadOnlyList<ScoreEvidence>? Evidence = null)
{
    public IReadOnlyList<ScoreEvidence> Evidence { get; init; } = Evidence ?? [];
}

/// <summary>One piece of evidence for a category score (FR-38): the file it refers to, if any, and why.</summary>
public sealed record ScoreEvidence(string? File, string Reason);

/// <summary>
/// §4.13: a regression — an <c>[unchanged]</c> requirement the review found broken — as stored in
/// <c>compliance_report.regressions</c> (same shape as the API's <c>RegressionDto</c>): the requirement ID, the category it
/// was reported under, what is broken and the evidence. No deterministic penalty applies.
/// </summary>
public sealed record RegressionEntry(string RequirementId, ReviewCategory Category, string Description, string? Evidence);

/// <summary>A drift area reported by the review agent (§4.7).</summary>
public sealed record DriftAreaEntry(string Area, Severity Severity, string Description);

/// <summary>A configured project notification channel (FR-47). The secret is encrypted with Data Protection: the
/// HMAC signing key of a webhook, or the incoming-webhook URL of a Slack or Teams channel (a bearer credential), whose
/// <see cref="Target"/> then only holds the masked display form (<c>hooks.slack.com/…</c>). <see cref="Id"/>
/// identifies the channel across settings updates (a kept channel keeps its secret).</summary>
public sealed record NotificationChannelConfig(
    Guid Id,
    NotificationChannelType Type,
    string Target,
    string? SecretEncrypted,
    IReadOnlyList<string> Events);

/// <summary>A source a build check read (§5, FR-255): the preview rollout or a CI check, its conclusion
/// (e.g. <c>success</c>, <c>failure</c>, <c>timed_out</c>, <c>unavailable</c>; null while pending) and a link to it.
/// D-0.6.1-23: <see cref="Status"/> is where a pending source stands (a preview component's deployment state <c>queued</c>,
/// <c>building</c>, …, or a check's <c>queued</c>/<c>in_progress</c>; <c>null</c> when not started or not known), and
/// <see cref="Message"/> explains an <c>unavailable</c> or informational source. <see cref="Informational"/> marks a source
/// shown for information only (the branch rules couldn't be read): never waited for and never a failure. All three are
/// absent in rows stored before.</summary>
public sealed record BuildCheckSource(
    string Name, BuildCheckSourceKind Kind, string? Conclusion, string? Url, string? Status = null, string? Message = null, bool Informational = false);

/// <summary>A failure found by a build check (§5, §4.48): the failure id (as in review findings), the normalised
/// fingerprint used for repeat marks (§4.37), the message, the source it came from, the log excerpt (§4.38) and the
/// de-duplicated error-summary lines.</summary>
public sealed record BuildCheckFailure(
    string FailureId,
    string Fingerprint,
    string Message,
    string Source,
    string? Excerpt,
    IReadOnlyList<string>? ErrorSummary = null)
{
    public IReadOnlyList<string> ErrorSummary { get; init; } = ErrorSummary ?? [];
}
