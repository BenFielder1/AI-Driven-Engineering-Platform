using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-276, FR-278: what the registered packs (and the host's DI registrations) provide, as names, so the validator can
/// check a definition's references without loading anything. Guards, handlers, tools and job types are global (names are
/// namespaced by pack id); <see cref="Schemas"/> and <see cref="Settings"/> are the defining pack's own. The engine's job
/// types (<see cref="EngineJobTypes"/>) are always known.
/// </summary>
public sealed record PackCatalog
{
    public static PackCatalog Empty { get; } = new();

    public IReadOnlyCollection<string> Guards { get; init; } = [];

    public IReadOnlyCollection<string> Handlers { get; init; } = [];

    public IReadOnlyCollection<string> Tools { get; init; } = [];

    /// <summary>Pack job types (registered handlers); <see cref="EngineJobTypes.All"/> are added implicitly.</summary>
    public IReadOnlyCollection<string> JobTypes { get; init; } = [];

    /// <summary>Settings the pack's <see cref="IRunSettingsProvider"/> supplies.</summary>
    public IReadOnlyCollection<string> Settings { get; init; } = [];

    /// <summary>JSON Schema files the pack embeds, by name (e.g. <c>plan.schema.json</c>).</summary>
    public IReadOnlyCollection<string> Schemas { get; init; } = [];

    /// <summary>Embedded schemas that aren't valid JSON Schema: name → reason (checked by the runtime).</summary>
    public IReadOnlyDictionary<string, string> InvalidSchemas { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>FR-295, D-0.7.0-33: the defining pack's prompts (name → template), checked against every agent stage's
    /// prompt (<c>DEF_UNKNOWN_PROMPT</c>); <c>null</c> when the pack registers no prompts (its prompts live in the host),
    /// so the check is skipped.</summary>
    public IReadOnlyDictionary<string, string>? Prompts { get; init; }

    /// <summary>FR-295: the context section names the pack's <see cref="IAgentContextProvider"/>s supply, which a prompt's
    /// required placeholders may use.</summary>
    public IReadOnlyCollection<string> ContextNames { get; init; } = [];

    /// <summary>Artifact keys a pack handler (not a stage) produces, e.g. an import job's output.</summary>
    public IReadOnlyCollection<string> ProducedArtifacts { get; init; } = [];

    public bool HasJobType(string jobType) =>
        EngineJobTypes.All.Contains(jobType, StringComparer.Ordinal) || JobTypes.Contains(jobType, StringComparer.Ordinal);
}
