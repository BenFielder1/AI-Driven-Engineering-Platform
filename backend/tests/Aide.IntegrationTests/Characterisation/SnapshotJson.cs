using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aide.IntegrationTests.Characterisation;

/// <summary>§4.58 serialisation: sorted keys (ordinal), 2-space indent, LF line endings and a trailing newline.</summary>
internal static class SnapshotJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(JsonNode? node) => Write(node, Options) + "\n";

    /// <summary>One line, sorted keys (sort keys and comparisons).</summary>
    public static string WriteCompact(JsonNode? node) => Write(node, Compact);

    private static string Write(JsonNode? node, JsonWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
