using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aide.Infrastructure.Persistence.Conversion;

/// <summary>JSON used for typed <c>jsonb</c> columns: camelCase properties, enums as snake_case wire values.</summary>
public static class AideJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumWireJsonConverterFactory(), new ScoreEvidenceListJsonConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Null JSON for {typeof(T).Name}.");
}

/// <summary>Maps a typed property to a <c>jsonb</c> column via System.Text.Json, compared by serialised value.</summary>
public static class JsonColumnExtensions
{
    public static PropertyBuilder<T> HasJsonbConversion<T>(this PropertyBuilder<T> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var converter = new ValueConverter<T, string>(v => AideJson.Serialize(v), s => AideJson.Deserialize<T>(s));
        var comparer = new ValueComparer<T>(
            (a, b) => AideJson.Serialize(a) == AideJson.Serialize(b),
            v => AideJson.Serialize(v).GetHashCode(StringComparison.Ordinal),
            v => AideJson.Deserialize<T>(AideJson.Serialize(v)));
        return property.HasConversion(converter, comparer).HasColumnType("jsonb");
    }
}

internal sealed class EnumWireJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(EnumWireJsonConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class EnumWireJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EnumStrings.TryParse<TEnum>(reader.GetString(), out var value)
            ? value
            : throw new JsonException($"Invalid {typeof(TEnum).Name} value.");

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToWire());
    }
}

/// <summary>Reads category-score evidence as a list of <see cref="ScoreEvidence"/>, or a legacy single string (reports
/// written before FR-38's evidence list) as one reason without a file. Always writes the list form.</summary>
internal sealed class ScoreEvidenceListJsonConverter : JsonConverter<IReadOnlyList<ScoreEvidence>>
{
    public override IReadOnlyList<ScoreEvidence> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? [new ScoreEvidence(null, reader.GetString() ?? string.Empty)]
            : JsonSerializer.Deserialize<List<ScoreEvidence>>(ref reader, options) ?? [];

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ScoreEvidence> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToList(), options);
}
