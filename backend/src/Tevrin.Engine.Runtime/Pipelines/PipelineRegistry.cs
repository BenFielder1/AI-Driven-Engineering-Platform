using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// One registered definition after loading: its pack and source name, the model, hash and canonical JSON (when it
/// parsed) and every error. <see cref="IsValid"/> when there are none.
/// </summary>
public sealed record LoadedPipelineDefinition(
    string PackId,
    string Name,
    PipelineDefinition? Definition,
    string? Hash,
    string? CanonicalJson,
    IReadOnlyList<DefinitionError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

/// <summary>
/// FR-275–FR-278: every registered pack's definitions, loaded, hashed (§4.50) and validated (§4.49) once at startup
/// (singleton). Built from DI: all <see cref="IPipelinePack"/>s plus the host's name bindings
/// (<see cref="PipelineComponentRegistration"/>) and job handlers (<see cref="JobHandlerRegistration"/>). A name counts as
/// registered when a pack declares it or DI binds it. <see cref="TryGet"/> and <see cref="GetByHash"/> return only valid
/// definitions; <see cref="Errors"/> (and <see cref="PipelineDefinitionsHealthCheck"/>) report the rest.
/// </summary>
public sealed class PipelineRegistry
{
    private readonly Dictionary<string, LoadedPipelineDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedPipelineDefinition> _byHash = new(StringComparer.Ordinal);

    public PipelineRegistry(
        IEnumerable<IPipelinePack> packs,
        IEnumerable<PipelineComponentRegistration> components,
        IEnumerable<JobHandlerRegistration> jobHandlers)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(jobHandlers);

        var registrations = new List<PackRegistration>();
        foreach (var pack in packs)
        {
            if (registrations.Any(r => r.PackId == pack.Id))
            {
                throw new InvalidOperationException($"Pipeline pack '{pack.Id}' is registered twice.");
            }

            var registration = new PackRegistration(pack.Id);
            pack.Register(registration);
            registrations.Add(registration);
        }

        Packs = registrations;
        var componentList = components.ToList();
        foreach (var duplicate in componentList.GroupBy(c => (c.Kind, c.Name)).Where(g => g.Select(c => c.ImplementationType).Distinct().Count() > 1))
        {
            throw new InvalidOperationException($"Pipeline {duplicate.Key.Kind} '{duplicate.Key.Name}' is bound to more than one implementation.");
        }

        Components = componentList;
        var jobTypes = jobHandlers.Select(j => j.JobType).ToList();
        Definitions = [.. registrations.SelectMany(r => r.Definitions.Select(d => Load(d, CatalogFor(r, registrations, componentList, jobTypes))))];
        Definitions = RejectDuplicateIds(Definitions);

        foreach (var loaded in Definitions.Where(d => d.IsValid))
        {
            _byId[loaded.Definition!.Pipeline] = loaded;
            _byHash[loaded.Hash!] = loaded;
        }
    }

    /// <summary>What each pack registered.</summary>
    public IReadOnlyList<PackRegistration> Packs { get; }

    /// <summary>The host's DI name bindings.</summary>
    public IReadOnlyList<PipelineComponentRegistration> Components { get; }

    /// <summary>Every registered definition, valid or not, in registration order.</summary>
    public IReadOnlyList<LoadedPipelineDefinition> Definitions { get; }

    /// <summary>Every definition error, across definitions.</summary>
    public IReadOnlyList<(LoadedPipelineDefinition Source, DefinitionError Error)> Errors =>
        [.. Definitions.SelectMany(d => d.Errors.Select(e => (d, e)))];

    public bool IsValid => Definitions.All(d => d.IsValid);

    /// <summary>The valid definition with id <paramref name="pipelineId"/>.</summary>
    public bool TryGet(string pipelineId, out LoadedPipelineDefinition definition) =>
        _byId.TryGetValue(pipelineId, out definition!);

    /// <summary>The valid definition whose hash is <paramref name="hash"/>, or <c>null</c> (M58 falls back to the stored
    /// canonical content for older hashes).</summary>
    public LoadedPipelineDefinition? GetByHash(string hash) => _byHash.GetValueOrDefault(hash);

    /// <summary>The DI implementation type bound to <paramref name="name"/>, or <c>null</c>.</summary>
    public Type? ImplementationOf(PipelineComponentKind kind, string name) =>
        Components.FirstOrDefault(c => c.Kind == kind && c.Name == name)?.ImplementationType;

    /// <summary>The catalog a pack's definitions are validated against: global names, the pack's own schemas and settings.</summary>
    private static PackCatalog CatalogFor(
        PackRegistration pack,
        List<PackRegistration> all,
        List<PipelineComponentRegistration> components,
        List<string> jobTypes)
    {
        IReadOnlyCollection<string> Names(Func<PackRegistration, IEnumerable<string>> fromPack, PipelineComponentKind kind) =>
            all.SelectMany(fromPack).Concat(components.Where(c => c.Kind == kind).Select(c => c.Name)).Distinct(StringComparer.Ordinal).ToList();

        var invalidSchemas = JsonSchemaCheck.Problems(pack.Schemas);

        return new PackCatalog
        {
            Guards = Names(p => p.Guards, PipelineComponentKind.Guard),
            Handlers = Names(p => p.Handlers, PipelineComponentKind.StageHandler),
            Tools = Names(p => p.Tools, PipelineComponentKind.Tool),
            JobTypes = [.. all.SelectMany(p => p.JobTypes).Concat(jobTypes).Distinct(StringComparer.Ordinal)],
            Settings = [.. pack.Settings],
            Schemas = [.. pack.Schemas.Keys],
            InvalidSchemas = invalidSchemas,
            ProducedArtifacts = [.. all.SelectMany(p => p.ProducedArtifacts).Distinct(StringComparer.Ordinal)],
            Prompts = pack.Prompts.Count == 0 ? null : pack.Prompts,
            ContextNames = [.. pack.ContextNames],
        };
    }

    private static LoadedPipelineDefinition Load(PackDefinitionSource source, PackCatalog catalog)
    {
        var result = PipelineDefinitionLoader.ParseAndValidate(source.Yaml, catalog);
        if (result.Definition is not { } definition)
        {
            return new LoadedPipelineDefinition(source.PackId, source.Name, null, null, null, result.Errors);
        }

        var canonical = DefinitionHash.CanonicalJson(definition);
        return new LoadedPipelineDefinition(source.PackId, source.Name, definition, DefinitionHash.Compute(definition), canonical, result.Errors);
    }

    /// <summary>A pipeline id is unique across packs (§4.49): every definition after the first with an id gets
    /// <c>DEF_DUPLICATE_ID</c> at <c>pipeline</c>.</summary>
    private static List<LoadedPipelineDefinition> RejectDuplicateIds(IReadOnlyList<LoadedPipelineDefinition> definitions)
    {
        var seen = new Dictionary<string, LoadedPipelineDefinition>(StringComparer.Ordinal);
        var result = new List<LoadedPipelineDefinition>(definitions.Count);
        foreach (var loaded in definitions)
        {
            if (loaded.Definition is { Pipeline.Length: > 0 } definition)
            {
                if (seen.TryGetValue(definition.Pipeline, out var first))
                {
                    var error = new DefinitionError(DefinitionErrorCodes.DuplicateId, "pipeline",
                        $"Pipeline '{definition.Pipeline}' is already defined by {first.PackId}/{first.Name}.");
                    result.Add(loaded with { Errors = [.. loaded.Errors, error] });
                    continue;
                }

                seen[definition.Pipeline] = loaded;
            }

            result.Add(loaded);
        }

        return result;
    }
}
