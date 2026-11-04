using Aide.Api.Features.Refinement;
using Aide.Api.Features.Stack;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;

namespace Aide.Api.Features.Analysis;

/// <summary>
/// <c>GET /api/projects/:id/analysis</c> (FR-182): the Analysis page of the project's current release version.
/// <list type="bullet">
/// <item><c>summary</c>: the Repository Analyst's state and the locked <c>codebase_summary</c> (every version with code).</item>
/// <item><c>stackSource</c> <c>infer</c> only: <c>run</c> (the Stack Analyst's latest run), <c>profile</c> (the latest
/// <c>stack_profile</c> version, parsed), <c>needsInput</c> (FR-181 "Needs your input", in layer order),
/// <c>unresolvedLayers</c> and <c>canApprove</c>/<c>approveBlockedReason</c> (the reason as text), <c>canContinue</c>
/// (FR-185: a locked profile while in analysis), <c>suggested</c> (FR-180 "Fill in manually": the detector's profile with no
/// agent findings), <c>selection</c> (the profile as a <c>POST /api/stack/options</c> body, for the editor's option states and
/// the Hosts section; FR-184), <c>composition</c> (pinned once approved), <c>previousComposition</c> (FR-186: the base
/// version's, as the comparison) and <c>deployment</c> (FR-184: whether deployment setup can be offered).</item>
/// </list>
/// </summary>
public sealed record AnalysisDto(
    Guid ReleaseVersionId,
    string ReleaseVersion,
    Phase Phase,
    StackSource StackSource,
    bool HasExistingCode,
    string? BaseCommitSha,
    CodebaseSummaryStatusDto Summary,
    StackAnalysisRunDto? Run,
    StackProfileViewDto? Profile,
    IReadOnlyList<StackProfileBlankDto> NeedsInput,
    IReadOnlyList<string> UnresolvedLayers,
    bool CanApprove,
    string? ApproveBlockedReason,
    bool CanContinue,
    StackProfileDocumentDto? Suggested,
    StackCompositionRequest? Selection,
    StackCompositionDto? Composition,
    StackCompositionDto? PreviousComposition,
    AnalysisDeploymentDto Deployment);

/// <summary>Where the Stack Analyst is: <c>queued</c>, <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public enum StackAnalysisRunState
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("queued")]
    Queued = 0,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("running")]
    Running = 1,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("succeeded")]
    Succeeded = 2,

    [System.Text.Json.Serialization.JsonStringEnumMemberName("failed")]
    Failed = 3,
}

/// <summary>
/// FR-180, FR-182: the latest Stack Analyst job of the version and its run (the corrective re-run's when there was one).
/// <c>errorCode</c> is set when it failed (e.g. <c>STACK_PROFILE_INVALID</c>, <c>BUDGET_EXCEEDED</c>); <c>warnings</c> are the run's
/// (e.g. <c>summary_unavailable</c>). <c>artifactId</c>/<c>artifactVersion</c> is the draft the run wrote, if any.
/// </summary>
public sealed record StackAnalysisRunDto(
    Guid JobId,
    Guid? RunId,
    StackAnalysisRunState Status,
    string Trigger,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<string> Warnings,
    Guid? ArtifactId,
    int? ArtifactVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// The latest <c>stack_profile</c> version (FR-182): its status, author, narrative and parsed document. <c>document</c> is
/// <c>null</c> (with <c>errors</c>) when the content can't be read as a profile, e.g. a corrupted version.
/// </summary>
public sealed record StackProfileViewDto(
    Guid ArtifactId,
    int Version,
    ArtifactStatus Status,
    ArtifactIntegrity Integrity,
    bool AuthoredByAgent,
    Guid? AgentRunId,
    DateTimeOffset CreatedAt,
    string Narrative,
    StackProfileDocumentDto? Document,
    IReadOnlyList<string> Errors);

/// <summary>§4.27: the structured profile, as in the <c>aide-stack-profile</c> block (the editor posts it back inside that block).</summary>
public sealed record StackProfileDocumentDto(
    int SchemaVersion,
    string? BaseCommitSha,
    bool Generalist,
    StackProfileLayersDto Layers,
    StackProfileHostsDto Hosts,
    IReadOnlyList<StackProfileStructureDto> Structure,
    IReadOnlyList<StackProfileQuestionDto> Questions);

public sealed record StackProfileLayersDto(StackProfileLayerDto Frontend, StackProfileLayerDto Backend, StackProfileLayerDto Data, StackProfileLayerDto Vcs);

/// <summary>A layer: catalog option (or <c>null</c>), path (frontend, backend), mode (data), status, confidence, the technology of an <c>unsupported</c> layer, evidence and a conflict's candidates.</summary>
public sealed record StackProfileLayerDto(
    string? Option,
    string? Path,
    string? Mode,
    StackProfileLayerStatus Status,
    StackProfileConfidence? Confidence,
    string? Technology,
    IReadOnlyList<StackProfileEvidenceDto> Evidence,
    IReadOnlyList<StackProfileCandidateDto> Candidates);

public sealed record StackProfileEvidenceDto(string File, string Text);

/// <summary>A candidate of a <c>conflict</c> layer; <c>source</c> is <c>agent</c> or <c>detector</c>; <c>option</c> may be <c>none</c>.</summary>
public sealed record StackProfileCandidateDto(string Option, string? Path, string Source, IReadOnlyList<StackProfileEvidenceDto> Evidence);

/// <summary>FR-184: hosts (option ids, <c>null</c> = None), local dev loop, origin model, separate parts and the host choices the detector found.</summary>
public sealed record StackProfileHostsDto(
    string? FrontendHost,
    string? BackendHost,
    string? DatabaseHost,
    string? Local,
    string? OriginModel,
    IReadOnlyList<string> SeparateParts,
    IReadOnlyList<string> Detected);

public sealed record StackProfileStructureDto(string Path, string Role, string Note);

public sealed record StackProfileQuestionDto(string Layer, string Text);

/// <summary>FR-181: one "Needs your input" entry: the layer, why (status), the agent's question, the unsupported technology and a conflict's candidates.</summary>
public sealed record StackProfileBlankDto(
    string Layer,
    StackProfileLayerStatus Status,
    string? Question,
    string? Technology,
    IReadOnlyList<StackProfileCandidateDto> Candidates);

/// <summary>
/// FR-184: <c>available</c> once the profile is locked with a composition that has a host (the page then shows the Deployment
/// setup windows, from <c>GET …/settings/deployment</c> and the <c>windows</c> of <c>POST /api/stack/options</c>); else
/// <c>reason</c> says why not.
/// </summary>
public sealed record AnalysisDeploymentDto(bool Available, string? Reason);

/// <summary><c>POST /api/projects/:id/analysis/run</c>: the queued Stack Analyst job.</summary>
public sealed record StackAnalysisQueuedDto(Guid JobId, string Trigger, DateTimeOffset QueuedAt);

internal static class StackProfileMapping
{
    public static StackProfileDocumentDto ToDto(StackProfileDocument d) => new(
        d.SchemaVersion,
        d.BaseCommitSha,
        d.Generalist,
        new StackProfileLayersDto(ToDto(d.Layers.Frontend), ToDto(d.Layers.Backend), ToDto(d.Layers.Data), ToDto(d.Layers.Vcs)),
        new StackProfileHostsDto(d.Hosts.FrontendHost, d.Hosts.BackendHost, d.Hosts.DatabaseHost, d.Hosts.Local, d.Hosts.OriginModel, d.Hosts.SeparateParts, d.Hosts.Detected),
        [.. d.Structure.Select(s => new StackProfileStructureDto(s.Path, s.Role, s.Note))],
        [.. d.Questions.Select(q => new StackProfileQuestionDto(q.Layer, q.Text))]);

    public static StackProfileLayerDto ToDto(StackProfileLayer l) => new(
        l.Option,
        l.Path,
        l.Mode,
        l.Status,
        l.Confidence,
        l.Technology,
        [.. l.Evidence.Select(ToDto)],
        [.. l.Candidates.Select(ToDto)]);

    public static StackProfileCandidateDto ToDto(StackProfileCandidate c) => new(c.Option, c.Path, c.Source, [.. c.Evidence.Select(ToDto)]);

    private static StackProfileEvidenceDto ToDto(StackProfileEvidence e) => new(e.File, e.Text);

    /// <summary>The profile as a <c>POST /api/stack/options</c> body (FR-184): None as <c>none</c>, unresolved layers left out.</summary>
    public static StackCompositionRequest ToRequest(StackProfileDocument d)
    {
        StackComponentSelection? Component(StackProfileLayer l) => l.Status switch
        {
            StackProfileLayerStatus.None => new StackComponentSelection { Pack = StackIds.None },
            _ when l.Option is { } option => new StackComponentSelection { Pack = option, Path = l.Path },
            _ => null,
        };

        var data = d.Layers.Data;
        return new StackCompositionRequest
        {
            Frontend = Component(d.Layers.Frontend),
            Backend = Component(d.Layers.Backend),
            Data = data.Status == StackProfileLayerStatus.None
                ? new StackDataSelection { Pack = StackIds.None }
                : data.Option is { } dataOption ? new StackDataSelection { Pack = dataOption, Mode = data.Mode } : null,
            Vcs = d.Layers.Vcs.Option is { } vcs ? new StackVcsSelection { Pack = vcs } : null,
            Deploy = new StackDeploySelection
            {
                FrontendHost = d.Hosts.FrontendHost ?? StackIds.None,
                BackendHost = d.Hosts.BackendHost ?? StackIds.None,
                DatabaseHost = d.Hosts.DatabaseHost ?? StackIds.None,
                Local = d.Hosts.Local,
                SeparateParts = d.Hosts.SeparateParts.Count == 0 ? null : d.Hosts.SeparateParts,
            },
            OriginModel = d.Hosts.OriginModel,
        };
    }
}
