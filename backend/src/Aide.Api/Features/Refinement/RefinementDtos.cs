using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Artifacts;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;

namespace Aide.Api.Features.Refinement;

/// <summary>
/// An answer for the next refinement round (FR-27): <c>questionId</c> of a question from the latest round, or the
/// <c>question</c> text for anything else, and the <c>answer</c>.
/// </summary>
public sealed record RefinementAnswerRequest
{
    [MaxLength(20)]
    public string? QuestionId { get; init; }

    [MaxLength(RefinementRules.MaxQuestionLength)]
    public string? Question { get; init; }

    [Required]
    [StringLength(RefinementRules.MaxAnswerLength, MinimumLength = 1)]
    public required string Answer { get; init; }
}

/// <summary>Start a refinement round (FR-27), optionally answering some or all clarifying questions of the latest round.</summary>
public sealed record StartRefinementRequest
{
    [MaxLength(RefinementRules.MaxAnswers)]
    public IReadOnlyList<RefinementAnswerRequest>? Answers { get; init; }
}

/// <summary>An answer given to the agent: <c>questionId</c> when it answered a question of a round, and the question text.</summary>
public sealed record RefinementAnswerDto(string? QuestionId, string Question, string Answer);

/// <summary>
/// A clarifying question of the latest completed round (FR-27). <c>answer</c> is the answer already given for the next
/// round (while that round is queued or running, or after it failed), else <c>null</c>.
/// </summary>
public sealed record RefinementQuestionDto(string Id, string Question, string? Reason, string? Answer);

/// <summary>
/// One refinement round (FR-27): an idea refinement job and its agent run. <c>runId</c> is the run that produced (or will
/// produce) the draft — the corrective re-run when the first reply couldn't be read; the run appears under
/// <c>/runs/{runId}</c> once the job has started. <c>status</c> is <c>queued</c> until then. <c>trigger</c> says who started
/// it: a writer (<c>user_requested</c>), or a rejected or corrupted project request. A round that ended without a draft
/// because the project no longer allowed one (e.g. a release in progress) is <c>cancelled</c>, with the reason in <c>error</c>. <c>answers</c> and <c>feedback</c> are
/// what the agent was given; <c>draftArtifactId</c>/<c>draftVersion</c> the project request version it created.
/// </summary>
public sealed record RefinementRoundDto(
    Guid RunId,
    Guid JobId,
    AgentJobTrigger? Trigger,
    AgentRunStatus Status,
    string? Error,
    IReadOnlyList<RefinementAnswerDto> Answers,
    string? Feedback,
    Guid? DraftArtifactId,
    int? DraftVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// FR-70: the Repository Analyst for the release version. <c>status</c>: <c>not_needed</c> (no existing code),
/// <c>queued</c>, <c>running</c>, <c>completed</c> (the locked summary exists: <c>artifactId</c>/<c>artifactVersion</c>),
/// <c>failed</c> (the analyst ended without one) or <c>timed_out</c> (unfinished 10 minutes after the version started;
/// refinement no longer waits). <c>runId</c> is the analyst's agent run once it has started (the run that wrote the
/// summary when completed), else <c>null</c>.
/// </summary>
public sealed record CodebaseSummaryStatusDto(CodebaseSummaryState Status, Guid? RunId, Guid? ArtifactId, int? ArtifactVersion);

/// <summary>
/// The refinement page (FR-27): the latest project request version (<c>latestDraft</c>, without content — read it from
/// the artifacts API), the latest round and its status, and the clarifying questions of the latest completed round
/// (<c>questionsRunId</c>) with any answers already given. <c>codebaseSummary</c> is the Repository Analyst's state for
/// the version (FR-70).
/// </summary>
public sealed record RefinementDto(
    ArtifactSummaryDto? LatestDraft,
    RefinementRoundDto? LatestRound,
    Guid? QuestionsRunId,
    IReadOnlyList<RefinementQuestionDto> Questions,
    CodebaseSummaryStatusDto CodebaseSummary);
