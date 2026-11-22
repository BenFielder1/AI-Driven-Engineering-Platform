using System.Text.Json;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

// FR-278: the extension points a pack implements or registers through. They live in the engine domain because a pack
// references only Tevrin.Engine.Domain (D-0.7.0-23). They're deliberately small: M58 (state machine, cascade) and M59
// (stage execution, agent context and tools) flesh out the contexts they receive, additively.

/// <summary>
/// FR-278: a pipeline pack. <see cref="Register"/> is called once when the runtime builds its pipeline registry, and
/// names everything the pack provides. Implementations of guards, handlers and tools are bound by name in the host's DI
/// (<c>AddPipelineGuard&lt;T&gt;("pack.name")</c> and so on), because a pack doesn't reference DI.
/// </summary>
public interface IPipelinePack
{
    /// <summary>The pack id (kebab-case, e.g. <c>sdlc</c>); every guard, handler and tool name the pack registers starts
    /// with <c>&lt;Id&gt;.</c>.</summary>
    string Id { get; }

    void Register(IPackRegistry registry);
}

/// <summary>
/// FR-278: what a pack registers. Guard, handler and tool names are namespaced by the pack id
/// (<c>sdlc.working-copy-prepared</c>); job type, setting and artifact names aren't (job types keep their stored values).
/// A malformed or wrongly namespaced name throws <see cref="ArgumentException"/>: it's a programming error.
/// </summary>
public interface IPackRegistry
{
    /// <summary>The id of the pack being registered.</summary>
    string PackId { get; }

    /// <summary>A pipeline definition (YAML text), e.g. an embedded <c>sdlc.pipeline.yaml</c>; <paramref name="name"/>
    /// names it in errors.</summary>
    void AddDefinition(string name, string yaml);

    /// <summary>A pipeline definition read from <paramref name="yaml"/> (UTF-8), e.g. an embedded resource stream.</summary>
    void AddDefinition(string name, Stream yaml);

    /// <summary>A JSON Schema file an artifact's <c>schema</c> can name (e.g. <c>plan.schema.json</c>).</summary>
    void AddSchema(string name, string json);

    /// <summary>A prompt template an agent stage's <c>prompt</c> can name.</summary>
    void AddPrompt(string name, string promptTemplate);

    /// <summary>FR-295: a context section name the pack's <see cref="IAgentContextProvider"/>s supply (e.g.
    /// <c>codebase_summary</c>), so a prompt may require its placeholder.</summary>
    void AddContextName(string name);

    /// <summary>An <see cref="IGuard"/> name (<c>&lt;pack&gt;.&lt;name&gt;</c>).</summary>
    void AddGuard(string name);

    /// <summary>An <see cref="IStageHandler"/> name (<c>&lt;pack&gt;.&lt;name&gt;</c>).</summary>
    void AddStageHandler(string name);

    /// <summary>An <see cref="IAgentTool"/> name (<c>&lt;pack&gt;.&lt;name&gt;</c>).</summary>
    void AddTool(string name);

    /// <summary>A job type the pack handles (its job handler is registered in the host's DI).</summary>
    void AddJobType(string jobType);

    /// <summary>A run setting the pack's <see cref="IRunSettingsProvider"/> supplies (e.g. <c>review_cycles</c>).</summary>
    void AddSetting(string name);

    /// <summary>An artifact key a pack handler produces outside any stage (e.g. an import job's output), so a definition
    /// may read it without a producing stage (<see cref="DefinitionErrorCodes.ArtifactNoProducer"/>).</summary>
    void AddArtifactProducer(string artifactKey);
}

/// <summary>FR-282: what a guard is evaluated against. <see cref="Facts"/> carries anything else (M58/M59 add facts).</summary>
public sealed record GuardContext(Guid RunId, Guid ProjectId, string StageId, ApprovalMode ApprovalMode, FactBag Facts);

/// <summary>A guard's verdict: <see cref="Ok"/>, or the reasons it failed (the user-facing messages, in order).</summary>
public sealed record GuardResult(bool Ok, IReadOnlyList<string> Reasons)
{
    public static GuardResult Pass { get; } = new(true, []);

    public static GuardResult Fail(params string[] reasons) => new(false, reasons);
}

/// <summary>FR-282: a named check, used in <c>requires:</c> (before entering a stage) and <c>validate:</c> (on a stage's
/// output, which arrives as a <see cref="StageOutputFact"/>).</summary>
public interface IGuard
{
    Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken);
}

/// <summary>The output a <c>validate:</c> guard checks.</summary>
public sealed record StageOutputFact(string ArtifactKey, ArtifactFormat Format, string Content);

/// <summary>
/// D-0.7.0-44: the agent attempt whose output a <c>validate:</c> guard checks, next to its <see cref="StageOutputFact"/>:
/// the attempt number (1 = first), the reply as the provider sent it (before the engine trims it into the output), and the
/// request's opaque host state, so a pack's guard can apply the check of that attempt (e.g. a corrective re-run checked
/// less strictly than the first). Absent outside an agent stage's validation.
/// </summary>
public sealed record StageAttemptFact(int Attempt, string Reply, object? HostState);

/// <summary>
/// An immutable bag of typed facts, keyed by type: one value per fact type. Extensible without changing the contexts
/// that carry it.
/// </summary>
public sealed class FactBag
{
    private readonly Dictionary<Type, object> _facts;

    private FactBag(Dictionary<Type, object> facts) => _facts = facts;

    public static FactBag Empty { get; } = new([]);

    /// <summary>A copy with <paramref name="fact"/> added (replacing any fact of the same type).</summary>
    public FactBag With<T>(T fact)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new FactBag(new Dictionary<Type, object>(_facts) { [typeof(T)] = fact });
    }

    public T? Get<T>()
        where T : class =>
        _facts.TryGetValue(typeof(T), out var fact) ? (T)fact : null;

    public bool TryGet<T>(out T fact)
        where T : class
    {
        fact = Get<T>()!;
        return fact is not null;
    }
}

/// <summary>
/// What a code stage's handler runs with: the run, its project, the stage that declares the handler in the run's pinned
/// definition, and <see cref="Facts"/> from the host (e.g. the job the work runs in, as the runtime's <c>StageJobFact</c>).
/// </summary>
public sealed record StageHandlerContext(Guid RunId, Guid ProjectId, string StageId, FactBag Facts);

public enum StageHandlerOutcome
{
    /// <summary>The stage's work is done.</summary>
    Completed = 0,

    /// <summary>The work continues elsewhere (e.g. a job was enqueued); the stage stays current.</summary>
    Waiting = 1,

    Failed = 2,
}

public sealed record StageHandlerResult(StageHandlerOutcome Outcome, string? Message = null)
{
    /// <summary>D-0.7.0-43: a <see cref="StageHandlerOutcome.Failed"/> result's error code (the job fails with it);
    /// <c>null</c> means <c>VALIDATION</c>.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>FR-280: the work of a code stage (<c>handler: &lt;pack&gt;.&lt;name&gt;</c>).</summary>
public interface IStageHandler
{
    Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>A tool's description for the model: name, what it does, and its arguments as a JSON Schema object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema);

/// <summary>A tool call's result: text for the model, and whether the call failed.</summary>
public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>FR-280: a tool an agent stage lists in <c>agent.tools</c>. Bad arguments are an error result, not an
/// exception.</summary>
public interface IAgentTool
{
    ToolSpec Spec { get; }

    Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken);
}

/// <summary>What an agent's context is being built for.</summary>
public sealed record AgentContextRequest(Guid RunId, Guid ProjectId, string PipelineId, string StageId, string AgentKey)
{
    /// <summary>
    /// The placeholders the host already supplies (its prompt values win over context): a provider may skip reading
    /// sections that only fill these.
    /// </summary>
    public IReadOnlySet<string> SuppliedPlaceholders { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>One named block of context for the prompt.</summary>
public sealed record AgentContextSection(string Name, string Content);

/// <summary>FR-278: adds pack context (e.g. the SDLC's codebase summary) to an agent stage's prompt.</summary>
public interface IAgentContextProvider
{
    /// <summary>The pipeline the provider serves.</summary>
    string PipelineId { get; }

    Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken);
}

/// <summary>An approval that invalidated artifacts (§4.54).</summary>
public sealed record InvalidationContext(Guid RunId, Guid ProjectId, string ApprovedArtifactKey, IReadOnlyList<string> InvalidatedArtifactKeys);

/// <summary>FR-278, FR-289, §4.54: pack effects of an invalidation (the SDLC resets steps).</summary>
public interface IInvalidationHook
{
    string PipelineId { get; }

    /// <summary>
    /// Pure, for <see cref="DefinitionCascade.Effect"/>: the stages the pack's own effect of approving
    /// <paramref name="approvedArtifactKey"/> counts as produced in, so they're considered for the fallback stage (the
    /// SDLC's step reset counts as produced in <c>execution</c>). Called only when the artifact's cascade isn't
    /// <see cref="CascadeMode.None"/>; empty when the pack has no effect.
    /// </summary>
    IReadOnlyList<string> FallbackStages(string approvedArtifactKey, IReadOnlyList<string> invalidatedArtifactKeys);

    Task OnInvalidatedAsync(InvalidationContext context, CancellationToken cancellationToken);
}

/// <summary>FR-278: the per-project values of a pipeline's <c>settings</c> (e.g. <c>review_cycles</c>).</summary>
public interface IRunSettingsProvider
{
    string PipelineId { get; }

    /// <summary>Setting <paramref name="name"/> for <paramref name="projectId"/>, or <c>null</c> when not provided.</summary>
    Task<int?> GetSettingAsync(Guid projectId, string name, CancellationToken cancellationToken);
}

/// <summary>What a new run's start stage is chosen for.</summary>
public sealed record RunStartContext(Guid ProjectId, string PipelineId, IReadOnlyList<string> StartStages, FactBag Facts);

/// <summary>§4.49: picks a new run's start stage among the definition's <c>start</c> (default: the first).</summary>
public interface IRunStartSelector
{
    string PipelineId { get; }

    /// <summary>One of <see cref="RunStartContext.StartStages"/>.</summary>
    Task<string> SelectStartAsync(RunStartContext context, CancellationToken cancellationToken);
}
