using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-279, FR-296: validates an artifact's JSON content against its pack schema. Each pack's schemas are built once, in
/// one private registry under <c>https://schemas.tevrin.invalid/&lt;pack&gt;/</c>, so refs between a pack's own files
/// resolve and nothing is fetched (singleton).
/// </summary>
public sealed class ArtifactSchemas(PipelineRegistry registry)
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, JsonSchema>> _byPack = new(StringComparer.Ordinal);

    /// <summary>The schema errors of <paramref name="content"/> against <paramref name="schemaName"/> of pack
    /// <paramref name="packId"/>; empty when valid. Content that isn't JSON is one error at <c>/</c>.</summary>
    public IReadOnlyList<SchemaFinding> Validate(string packId, string schemaName, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!SchemasOf(packId).TryGetValue(schemaName, out var schema))
        {
            throw new InvalidOperationException($"Pack '{packId}' has no usable schema '{schemaName}'.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return [new SchemaFinding("/", $"The output is not valid JSON ({ex.Message})")];
        }

        using (document)
        {
            var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (results.IsValid)
            {
                return [];
            }

            var findings = (results.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Values.Select(m => new SchemaFinding(Pointer(d.InstanceLocation.ToString()), m)))
                .Distinct()
                .ToList();
            return findings.Count > 0 ? findings : [new SchemaFinding("/", "The output does not match the schema.")];
        }
    }

    private static string Pointer(string location) => string.IsNullOrEmpty(location) ? "/" : location;

    private IReadOnlyDictionary<string, JsonSchema> SchemasOf(string packId) =>
        _byPack.GetOrAdd(packId, id => Build(id, registry.Packs.FirstOrDefault(p => p.PackId == id)?.Schemas ?? new Dictionary<string, string>()));

    /// <summary>Builds every schema of the pack; one that refers to another not built yet is retried after it.</summary>
    private static Dictionary<string, JsonSchema> Build(string packId, IReadOnlyDictionary<string, string> sources)
    {
        var schemaRegistry = new SchemaRegistry();
        var built = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var pending = sources.Keys.ToList();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var name in pending.ToList())
            {
                try
                {
                    using var json = JsonDocument.Parse(sources[name]);
                    var baseUri = new Uri($"https://schemas.tevrin.invalid/{packId}/{name}");
                    built[name] = JsonSchema.Build(json.RootElement.Clone(), new BuildOptions { SchemaRegistry = schemaRegistry }, baseUri);
                    pending.Remove(name);
                    progress = true;
                }
                catch (Exception ex) when (ex is JsonSchemaException or JsonException or InvalidOperationException or ArgumentException or RefResolutionException)
                {
                    // An unresolved ref to a schema built later in this pass; the registry rejects invalid schemas at load.
                }
            }
        }

        return built;
    }
}
