using System.Text.Json;
using Json.Schema;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// FR-279: whether an embedded schema file is valid JSON Schema: the JSON Schema 2020-12 meta-schema, every <c>$ref</c>
/// resolvable without fetching (<see cref="JsonSchemaRefs"/>), then a build with a private registry (skipped when the
/// schema refers to another pack schema, which the private registry doesn't hold). Validating artifacts is M59.
/// </summary>
internal static class JsonSchemaCheck
{
    /// <summary>Each of the pack's <paramref name="schemas"/> (name → JSON) that isn't valid, with the reason.</summary>
    public static Dictionary<string, string> Problems(IReadOnlyDictionary<string, string> schemas)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, json) in schemas)
        {
            if (Problem(json, schemas) is { } problem)
            {
                problems[name] = problem;
            }
        }

        return problems;
    }

    /// <summary>Why <paramref name="json"/> isn't a valid JSON Schema, or <c>null</c> when it is.</summary>
    public static string? Problem(string json, IReadOnlyDictionary<string, string> packSchemas)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return $"not JSON ({ex.Message})";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            {
                return "a schema must be an object or a boolean";
            }

            var results = MetaSchemas.Draft202012.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!results.IsValid)
            {
                var first = (results.Details ?? [])
                    .Where(d => d.Errors is { Count: > 0 })
                    .Select(d => $"{d.InstanceLocation}: {d.Errors!.Values.First()}")
                    .FirstOrDefault();
                return "it doesn't match the JSON Schema 2020-12 meta-schema" + (first is null ? string.Empty : $" ({first})");
            }

            if (JsonSchemaRefs.Problem(root, packSchemas, out var hasExternalRefs) is { } refProblem)
            {
                return refProblem;
            }

            if (hasExternalRefs)
            {
                return null;
            }

            try
            {
                JsonSchema.Build(root, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            }
            catch (Exception ex) when (ex is JsonSchemaException or JsonException or InvalidOperationException or ArgumentException or RefResolutionException)
            {
                return ex.Message;
            }

            return null;
        }
    }
}
