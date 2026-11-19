using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// FR-279: checks every <c>$ref</c> in a pack schema at load. A local ref (<c>#</c>, <c>#/$defs/x</c>, <c>#anchor</c>)
/// must resolve within the schema; a ref to another file (<c>report.schema.json#/$defs/x</c>) must name a schema the same
/// pack registered, and its fragment must resolve there; an absolute URI (http, https or any other scheme) is refused,
/// because nothing is fetched. Values under <c>const</c>, <c>enum</c>, <c>examples</c> and <c>default</c> are data and
/// aren't searched.
/// </summary>
internal static partial class JsonSchemaRefs
{
    private static readonly string[] DataKeywords = ["const", "enum", "examples", "default"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUri();

    /// <summary>The first unresolvable ref in <paramref name="root"/>, or <c>null</c>. <paramref name="hasExternalRefs"/>:
    /// whether any ref points at another pack schema.</summary>
    public static string? Problem(JsonElement root, IReadOnlyDictionary<string, string> packSchemas, out bool hasExternalRefs)
    {
        hasExternalRefs = false;
        foreach (var (location, reference) in Refs(root, string.Empty))
        {
            var at = location.Length == 0 ? "/" : location;
            if (AbsoluteUri().IsMatch(reference))
            {
                return $"$ref '{reference}' at {at} is a remote reference; only refs within the schema or to the pack's own schemas are allowed";
            }

            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var file = hash < 0 ? reference : reference[..hash];
            var fragment = hash < 0 ? string.Empty : reference[(hash + 1)..];
            var target = root;
            JsonDocument? other = null;
            try
            {
                if (file.Length > 0)
                {
                    hasExternalRefs = true;
                    if (!packSchemas.TryGetValue(file, out var otherJson))
                    {
                        return $"$ref '{reference}' at {at} names '{file}', which the pack doesn't register";
                    }

                    try
                    {
                        other = JsonDocument.Parse(otherJson);
                    }
                    catch (JsonException)
                    {
                        return $"$ref '{reference}' at {at} names '{file}', which isn't JSON";
                    }

                    target = other.RootElement;
                }

                if (!Resolves(target, fragment))
                {
                    return $"$ref '{reference}' at {at} doesn't resolve";
                }
            }
            finally
            {
                other?.Dispose();
            }
        }

        return null;
    }

    private static IEnumerable<(string Location, string Ref)> Refs(JsonElement element, string location)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = location + "/" + Escape(property.Name);
                    if (property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return (location, property.Value.GetString()!);
                    }
                    else if (!DataKeywords.Contains(property.Name, StringComparer.Ordinal))
                    {
                        foreach (var found in Refs(property.Value, child))
                        {
                            yield return found;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var found in Refs(item, location + "/" + index.ToString(CultureInfo.InvariantCulture)))
                    {
                        yield return found;
                    }

                    index++;
                }

                break;
        }
    }

    /// <summary>An empty fragment, a JSON Pointer (<c>/…</c>, percent-encoded as in a URI) or a plain-name anchor.</summary>
    private static bool Resolves(JsonElement root, string fragment)
    {
        if (fragment.Length == 0)
        {
            return true;
        }

        if (!fragment.StartsWith('/'))
        {
            return HasAnchor(root, fragment);
        }

        var current = root;
        foreach (var raw in Uri.UnescapeDataString(fragment)[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var next))
            {
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < current.GetArrayLength())
            {
                current = current[i];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAnchor(JsonElement element, string anchor) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(p =>
            ((p.Name is "$anchor" or "$dynamicAnchor") && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == anchor)
            || HasAnchor(p.Value, anchor)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => HasAnchor(item, anchor)),
        _ => false,
    };

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
