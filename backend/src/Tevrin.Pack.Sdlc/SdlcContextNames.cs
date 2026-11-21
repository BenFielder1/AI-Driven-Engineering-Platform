namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-317: the context section names the SDLC's <c>IAgentContextProvider</c>s supply. Each fills the prompt placeholder
/// named after it (<c>project_name</c> → <c>{{PROJECT_NAME}}</c>), with the values 0.6.1's job handlers put there.
/// </summary>
public static class SdlcContextNames
{
    /// <summary><c>{{PROJECT_NAME}}</c>: the project's name.</summary>
    public const string ProjectName = "project_name";

    /// <summary><c>{{IDEA}}</c>: the project's idea.</summary>
    public const string Idea = "idea";

    /// <summary><c>{{MAX_FILES_PER_STEP}}</c>: the project's file limit per plan step (invariant digits).</summary>
    public const string MaxFilesPerStep = "max_files_per_step";

    /// <summary><c>{{CODEBASE_SUMMARY}}</c>: the version's current intact codebase summary (also an artifact key).</summary>
    public const string CodebaseSummary = SdlcArtifacts.CodebaseSummary;

    /// <summary><c>{{VERSION_REQUEST}}</c>: the release version's request.</summary>
    public const string VersionRequest = "version_request";

    /// <summary><c>{{BASE}}</c>: what the version builds on (<c>&lt;tag&gt; (&lt;sha&gt;)</c> or <c>commit &lt;sha&gt;</c>).</summary>
    public const string Base = "base";

    /// <summary><c>{{SPEC_MODE}}</c>: the spec validation mode of the version (§4.13).</summary>
    public const string SpecMode = "spec_mode";

    /// <summary><c>{{BASELINE_SPECIFICATION}}</c>: the previous released version's locked spec (spec generator).</summary>
    public const string BaselineSpecification = "baseline_specification";

    /// <summary><c>{{PREVIOUS_PROJECT_REQUEST}}</c>: the previous released version's locked project request (refinement).</summary>
    public const string PreviousProjectRequest = "previous_project_request";

    /// <summary><c>{{PREVIOUS_SPECIFICATION}}</c> of refinement: the previous released version's locked spec. (The spec
    /// generator's <c>{{PREVIOUS_SPECIFICATION}}</c> is the rejected draft it revises: a job value, not context.)</summary>
    public const string PreviousSpecification = "previous_specification";

    /// <summary>Every SDLC context name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ProjectName, Idea, MaxFilesPerStep, CodebaseSummary, VersionRequest, Base, SpecMode, BaselineSpecification,
        PreviousProjectRequest, PreviousSpecification,
    ];
}
