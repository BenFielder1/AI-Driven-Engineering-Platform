using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// Parsed stored definitions by hash, with their pack (singleton): a hash's content never changes, so it is read and parsed
/// once (§8), and a run pinned to a stored-only hash doesn't query <c>pipeline_definitions</c> on every move.
/// </summary>
public sealed class PipelineDefinitionCache
{
    private readonly ConcurrentDictionary<string, StoredPipelineDefinition> _byHash = new(StringComparer.Ordinal);

    public bool TryGet(string hash, out StoredPipelineDefinition stored) => _byHash.TryGetValue(hash, out stored!);

    public void Add(string hash, StoredPipelineDefinition stored) => _byHash.TryAdd(hash, stored);
}

/// <summary>A stored definition's pack and parsed model.</summary>
public sealed record StoredPipelineDefinition(string PackId, PipelineDefinition Definition);

/// <summary>A pinned definition and whether this build can run it.</summary>
public sealed record ResolvedDefinition(string Hash, string PackId, PipelineDefinition Definition, bool Available);

/// <summary>
/// FR-299, FR-301: a run's pinned definition by hash: the registered definition, else the stored canonical JSON in
/// <c>pipeline_definitions</c> (checked against its hash, parsed once and cached). A definition is available when it is
/// registered, or when every guard, handler and tool it names is registered in this build (§6
/// <c>DEFINITION_UNAVAILABLE</c>).
/// </summary>
public sealed class PipelineDefinitionResolver(PipelineRegistryHost registryHost, IEngineDbContext db, PipelineDefinitionCache cache)
{
    /// <summary>The definition with <paramref name="hash"/>, or <c>null</c> when it is neither registered nor stored
    /// (or the stored row doesn't match its hash or doesn't parse).</summary>
    public async Task<ResolvedDefinition?> ResolveAsync(string hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        var registry = registryHost.Registry;
        if (registry?.GetByHash(hash) is { Definition: { } registered } loaded)
        {
            return new ResolvedDefinition(hash, loaded.PackId, registered, true);
        }

        if (!cache.TryGet(hash, out var stored))
        {
            var record = await db.PipelineDefinitions.AsNoTracking()
                .Where(d => d.Hash == hash)
                .Select(d => new { d.PackId, d.CanonicalJson })
                .FirstOrDefaultAsync(cancellationToken);
            if (record is null || ParseStored(hash, record.CanonicalJson) is not { } parsed)
            {
                // Not cached: a missing row may be stored later, and a row that doesn't match its hash is never usable.
                return null;
            }

            stored = new StoredPipelineDefinition(record.PackId, parsed);
            cache.Add(hash, stored);
        }

        return new ResolvedDefinition(hash, stored.PackId, stored.Definition, IsRunnable(registry, stored.Definition));
    }

    /// <summary>Every registered pipeline's current valid definition.</summary>
    public IReadOnlyList<LoadedPipelineDefinition> Registered() =>
        registryHost.Registry is { } registry ? [.. registry.Definitions.Where(d => d.IsValid)] : [];

    /// <summary>A stored definition's canonical JSON parsed, or <c>null</c> when it isn't JSON, doesn't match
    /// <paramref name="hash"/> (§4.50) or doesn't parse.</summary>
    public static PipelineDefinition? ParseStored(string hash, string storedJson)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(storedJson);
        try
        {
            using var document = JsonDocument.Parse(storedJson);
            var canonical = StoredDefinitions.Canonical(document.RootElement);
            if (!string.Equals(StoredDefinitions.HashOf(canonical), hash, StringComparison.Ordinal))
            {
                return null;
            }

            // DefinitionHash.CanonicalJson is valid loader input and reads back to the same model.
            return PipelineDefinitionLoader.Parse(canonical).Definition;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>§6 <c>DEFINITION_UNAVAILABLE</c>: whether every guard (in <c>requires</c>, <c>validate</c> and
    /// <c>loop.requires</c>), handler and tool <paramref name="definition"/> names is registered; never without a registry.</summary>
    public static bool IsRunnable(PipelineRegistry? registry, PipelineDefinition definition) =>
        registry is not null && MissingNames(registry, definition).Count == 0;

    /// <summary>
    /// FR-277 acceptance: the guards, handlers and tools <paramref name="definition"/> names that <paramref name="registry"/>
    /// doesn't register, as <c>guard:&lt;name&gt;</c>, <c>handler:&lt;name&gt;</c> and <c>tool:&lt;name&gt;</c> (distinct,
    /// in stage order), for the <c>DEFINITION_UNAVAILABLE</c> log. Empty when the definition is runnable.
    /// </summary>
    public static IReadOnlyList<string> MissingNames(PipelineRegistry registry, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definition);
        var missing = new List<string>();
        void Check(PipelineComponentKind kind, string prefix, string name)
        {
            var label = $"{prefix}:{name}";
            if (!registry.Registers(kind, name) && !missing.Contains(label, StringComparer.Ordinal))
            {
                missing.Add(label);
            }
        }

        // FR-343: parallel branch stages too; FR-344, FR-346: veto and disagreement guards.
        foreach (var stage in definition.AllStages)
        {
            var guards = stage.Requires.OfType<GuardRequirement>().Select(r => r.Guard)
                .Concat(stage.Validate.Select(v => v.Guard).OfType<string>()) // FR-350: an agent entry has no guard
                .Concat(stage.Loop?.Requires.OfType<GuardRequirement>().Select(r => r.Guard) ?? [])
                .Concat(stage.Agents.SelectMany(b => b.Validate).Select(v => v.Guard).OfType<string>()) // D-0.7.0-44: the bindings' own checks
                .Concat(stage.Veto.Select(v => v.Guard).OfType<string>())
                .Concat(stage.Aggregate?.DisagreementGuard is { } disagreement ? [disagreement] : []);
            foreach (var guard in guards)
            {
                Check(PipelineComponentKind.Guard, "guard", guard);
            }

            // D-0.7.0-43: the primary handler, then the secondary ones.
            foreach (var handler in stage.AllHandlers())
            {
                Check(PipelineComponentKind.StageHandler, "handler", handler);
            }

            // D-0.7.0-42: the secondary agents' tools too.
            foreach (var tool in (stage.Agent?.Tools ?? []).Concat(stage.Agents.SelectMany(b => b.Tools)))
            {
                Check(PipelineComponentKind.Tool, "tool", tool);
            }
        }

        return missing;
    }
}
