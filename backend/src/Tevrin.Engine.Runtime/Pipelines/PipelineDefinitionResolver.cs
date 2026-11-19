using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>Parsed stored definitions by hash (singleton): a hash's content never changes, so it is parsed once (§8).</summary>
public sealed class PipelineDefinitionCache
{
    private readonly ConcurrentDictionary<string, PipelineDefinition> _byHash = new(StringComparer.Ordinal);

    public bool TryGet(string hash, out PipelineDefinition definition) => _byHash.TryGetValue(hash, out definition!);

    public void Add(string hash, PipelineDefinition definition) => _byHash.TryAdd(hash, definition);
}

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

        var record = await db.PipelineDefinitions.AsNoTracking()
            .Where(d => d.Hash == hash)
            .Select(d => new { d.PackId, d.CanonicalJson })
            .FirstOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (!cache.TryGet(hash, out var definition))
        {
            if (ParseStored(hash, record.CanonicalJson) is not { } parsed)
            {
                return null;
            }

            definition = parsed;
            cache.Add(hash, definition);
        }

        return new ResolvedDefinition(hash, record.PackId, definition, IsRunnable(registry, definition));
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
    public static bool IsRunnable(PipelineRegistry? registry, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (registry is null)
        {
            return false;
        }

        foreach (var stage in definition.Stages)
        {
            var guards = stage.Requires.OfType<GuardRequirement>().Select(r => r.Guard)
                .Concat(stage.Validate.Select(v => v.Guard))
                .Concat(stage.Loop?.Requires.OfType<GuardRequirement>().Select(r => r.Guard) ?? []);
            if (guards.Any(g => !registry.Registers(PipelineComponentKind.Guard, g))
                || (stage.Handler is { } handler && !registry.Registers(PipelineComponentKind.StageHandler, handler))
                || (stage.Agent?.Tools ?? []).Any(t => !registry.Registers(PipelineComponentKind.Tool, t)))
            {
                return false;
            }
        }

        return true;
    }
}
