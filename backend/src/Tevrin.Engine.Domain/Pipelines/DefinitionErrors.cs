namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-276: one problem in a definition: a §4.49 code, the YAML path (e.g. <c>stages[1].validate[0].guard</c>;
/// <c>$</c> for the whole document) and a human-readable message.</summary>
public sealed record DefinitionError(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

/// <summary>FR-276: the validator codes of §4.49 (exact values).</summary>
public static class DefinitionErrorCodes
{
    /// <summary>The YAML doesn't parse, a key has the wrong type, or a value is out of range or not allowed.</summary>
    public const string Parse = "DEF_PARSE";

    public const string DuplicateId = "DEF_DUPLICATE_ID";
    public const string UnknownStage = "DEF_UNKNOWN_STAGE";
    public const string UnknownArtifact = "DEF_UNKNOWN_ARTIFACT";
    public const string UnknownStatus = "DEF_UNKNOWN_STATUS";
    public const string UnknownGuard = "DEF_UNKNOWN_GUARD";
    public const string UnknownHandler = "DEF_UNKNOWN_HANDLER";
    public const string UnknownTool = "DEF_UNKNOWN_TOOL";
    public const string UnknownJobType = "DEF_UNKNOWN_JOB_TYPE";
    public const string StageKind = "DEF_STAGE_KIND";
    public const string UnreachableStage = "DEF_UNREACHABLE_STAGE";
    public const string UncappedLoop = "DEF_UNCAPPED_LOOP";
    public const string LoopForward = "DEF_LOOP_FORWARD";
    public const string SchemaNotFound = "DEF_SCHEMA_NOT_FOUND";
    public const string ArtifactNoProducer = "DEF_ARTIFACT_NO_PRODUCER";
    public const string UnknownSetting = "DEF_UNKNOWN_SETTING";
    public const string TriggerNotSupported = "DEF_TRIGGER_NOT_SUPPORTED";

    /// <summary>FR-295, D-0.7.0-33: an agent stage's prompt isn't one its pack registered.</summary>
    public const string UnknownPrompt = "DEF_UNKNOWN_PROMPT";

    /// <summary>
    /// FR-352 (0.8.0): a project-scope artifact a run would produce (a stage or agent output, a gate's <c>approves</c>, a
    /// pack handler's product) or supply (<c>input: true</c>), or declared differently from another pipeline sharing its
    /// key (D-0.8.0-19).
    /// </summary>
    public const string ScopeInput = "DEF_SCOPE_INPUT";

    /// <summary>FR-343 (0.8.0): a stage in one branch of a <c>parallel</c> group reads an artifact another branch of the
    /// same group produces.</summary>
    public const string BranchInput = "DEF_BRANCH_INPUT";

    /// <summary>FR-344, FR-347 (0.8.0): a <c>routes:</c> entry or <c>on_veto.to</c> that isn't an earlier stage.</summary>
    public const string RouteTarget = "DEF_ROUTE_TARGET";

    /// <summary>FR-345 (0.8.0, also FR-344 and FR-349 agent references): an agent key the stage doesn't bind in
    /// <c>agents:</c>.</summary>
    public const string FanOutAgent = "DEF_FANOUT_AGENT";

    /// <summary>FR-354 (0.8.0): a <c>followups:</c> entry whose stage isn't a top-level stage, is listed twice, is reached by
    /// the main flow (a start, the linear default, <c>on_success</c>, <c>on_reject</c>, <c>loop</c>, a route or
    /// <c>on_veto</c>), or whose <c>after</c> is empty, repeats a duration or holds one that isn't <c>&lt;n&gt;d|h|m</c> with
    /// <c>n</c> &gt; 0.</summary>
    public const string FollowUpStage = "DEF_FOLLOWUP_STAGE";

    /// <summary>Every code, in §4.49 table order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Parse, DuplicateId, UnknownStage, UnknownArtifact, UnknownStatus, UnknownGuard, UnknownHandler, UnknownTool,
        UnknownJobType, StageKind, UnreachableStage, UncappedLoop, LoopForward, SchemaNotFound, ArtifactNoProducer,
        UnknownSetting, TriggerNotSupported, UnknownPrompt, ScopeInput, BranchInput, RouteTarget, FanOutAgent,
        FollowUpStage,
    ];

    /// <summary>
    /// Parse errors plus validator errors as one list: a validator error at a path the parser already reported (a value
    /// of the wrong type reads as missing) is dropped, so each problem is reported once.
    /// </summary>
    public static IReadOnlyList<DefinitionError> Merge(IReadOnlyList<DefinitionError> parseErrors, IReadOnlyList<DefinitionError> validationErrors)
    {
        ArgumentNullException.ThrowIfNull(parseErrors);
        ArgumentNullException.ThrowIfNull(validationErrors);
        var parsedPaths = parseErrors.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        return [.. parseErrors, .. validationErrors.Where(e => !parsedPaths.Contains(e.Path))];
    }
}
