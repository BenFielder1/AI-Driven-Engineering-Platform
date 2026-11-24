using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// FR-277, §4.50: storing definitions in <c>pipeline_definitions</c> on first use, and checking a stored one against its
/// hash. The stored canonical JSON is <c>jsonb</c>, which keeps the JSON's meaning but not its bytes (PostgreSQL reorders
/// keys and adds spaces), so it is read with a JSON reader and written back in canonical form before hashing
/// (D-0.7.0-26: never through the YAML loader).
/// </summary>
public static class StoredDefinitions
{
    /// <summary>
    /// Stores <paramref name="definition"/> unless its hash is already stored (<c>ON CONFLICT DO NOTHING</c>, so concurrent
    /// first uses on several replicas are safe). The row is immutable and content-addressed, so it may be committed on its
    /// own; inside the caller's transaction it commits with it.
    /// </summary>
    public static Task StoreAsync(DbContext db, LoadedPipelineDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return StoreAsync(db.Database, definition, now, cancellationToken);
    }

    /// <summary>As <see cref="StoreAsync(DbContext, LoadedPipelineDefinition, DateTimeOffset, CancellationToken)"/>, through
    /// the engine's view of the host's context (FR-331: a run started by the engine).</summary>
    public static Task StoreForEngineAsync(IEngineDbContext db, LoadedPipelineDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return StoreAsync(db.Database, definition, now, cancellationToken);
    }

    private static Task<int> StoreAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, LoadedPipelineDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = ToRecord(definition, now);
        return database.ExecuteSqlAsync(
            $"""
            INSERT INTO pipeline_definitions (hash, pipeline_id, version, pack_id, canonical_json, created_at)
            VALUES ({record.Hash}, {record.PipelineId}, {record.Version}, {record.PackId}, CAST({record.CanonicalJson} AS jsonb), {record.CreatedAt})
            ON CONFLICT (hash) DO NOTHING
            """,
            cancellationToken);
    }

    /// <summary>The record for a valid loaded definition.</summary>
    public static PipelineDefinitionRecord ToRecord(LoadedPipelineDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsValid || definition.Hash is null || definition.CanonicalJson is null)
        {
            throw new InvalidOperationException($"Pipeline definition {definition.PackId}/{definition.Name} is not valid and can't be stored.");
        }

        return new PipelineDefinitionRecord
        {
            Hash = definition.Hash,
            PipelineId = definition.Definition!.Pipeline,
            Version = definition.Definition.Version,
            PackId = definition.PackId,
            CanonicalJson = definition.CanonicalJson,
            CreatedAt = now,
        };
    }

    /// <summary>The §4.50 hash of stored canonical JSON: parsed with a JSON reader, re-written canonically (keys sorted
    /// ordinally, no insignificant whitespace, the <c>DefinitionHash</c> string escaping) and hashed.</summary>
    public static string HashOf(string storedJson)
    {
        ArgumentNullException.ThrowIfNull(storedJson);
        using var document = JsonDocument.Parse(storedJson);
        return ContentHash.Compute(Canonical(document.RootElement));
    }

    /// <summary>Canonical JSON text of <paramref name="element"/> (§4.50).</summary>
    public static string Canonical(JsonElement element)
    {
        var json = new StringBuilder();
        Write(json, element);
        return json.ToString();
    }

    private static void Write(StringBuilder json, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;
                    WriteString(json, property.Name);
                    json.Append(':');
                    Write(json, property.Value);
                }

                json.Append('}');
                break;
            case JsonValueKind.Array:
                json.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        json.Append(',');
                    }

                    Write(json, item);
                }

                json.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(json, element.GetString()!);
                break;
            case JsonValueKind.Number:
                json.Append(element.GetInt64().ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                json.Append("true");
                break;
            case JsonValueKind.False:
                json.Append("false");
                break;
            default:
                json.Append("null");
                break;
        }
    }

    /// <summary>The escaping of <c>DefinitionHash</c>: only <c>"</c>, <c>\</c> and U+0000–U+001F (<c>\u00xx</c>), after
    /// normalising line endings.</summary>
    private static void WriteString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var c in ContentHash.NormaliseLineEndings(text))
        {
            switch (c)
            {
                case '"':
                    json.Append(@"\""");
                    break;
                case '\\':
                    json.Append(@"\\");
                    break;
                case < ' ':
                    json.Append(@"\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }
}
