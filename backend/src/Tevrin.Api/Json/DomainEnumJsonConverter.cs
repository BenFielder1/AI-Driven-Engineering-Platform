using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tevrin.Domain;
using Tevrin.Engine.Domain;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Api.Json;

/// <summary>
/// Domain enums on the wire use the same snake_case names as SPEC.md and the database (<c>SystemRole.Admin</c> ⇄
/// <c>"admin"</c>, <c>StepStatus.InProgress</c> ⇄ <c>"in_progress"</c>, matching <c>EnumStrings</c>). Only Tevrin.Domain
/// and Tevrin.Engine.Domain enums (FR-262: e.g. <c>JobStatus</c>, <c>GateStatus</c>, <c>LlmProvider</c> moved there) are
/// affected, so API-only enums keep their own converters. An enum with read-only aliases (<c>WireAliasAttribute</c>,
/// FR-326: <c>MergeStrategy</c> reads <c>aide_merges</c>) also accepts them; it is always written with its own name.
/// </summary>
public sealed class DomainEnumJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsEnum
            && (typeToConvert.Assembly == typeof(DomainAssembly).Assembly || typeToConvert.Assembly == typeof(EngineDomainAssembly).Assembly);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var factory = (JsonConverterFactory)Activator.CreateInstance(
            typeof(JsonStringEnumConverter<>).MakeGenericType(typeToConvert),
            JsonNamingPolicy.SnakeCaseLower,
            false)!;
        var converter = factory.CreateConverter(typeToConvert, options)!;
        return (JsonConverter?)typeof(DomainEnumJsonConverter)
            .GetMethod(nameof(WithAliases), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeToConvert)
            .Invoke(null, [converter]);
    }

    private static JsonConverter WithAliases<TEnum>(JsonConverter converter)
        where TEnum : struct, Enum =>
        EnumStrings.Aliases<TEnum>().Count == 0 ? converter : new AliasEnumJsonConverter<TEnum>((JsonConverter<TEnum>)converter);
}

/// <summary>
/// Reads a domain enum's aliases (<c>WireAliasAttribute</c>) and otherwise defers to the snake_case
/// <see cref="JsonStringEnumConverter{TEnum}"/>, so writes, errors and every other value stay as they were. The OpenAPI
/// schema of such an enum lists its written values only (<c>WireAliasEnumSchemaTransformer</c>).
/// </summary>
internal sealed class AliasEnumJsonConverter<TEnum>(JsonConverter<TEnum> inner) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value && EnumStrings.Aliases<TEnum>().TryGetValue(value, out var aliased)
            ? aliased
            : inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        inner.Write(writer, value, options);

    public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } value && EnumStrings.Aliases<TEnum>().TryGetValue(value, out var aliased)
            ? aliased
            : inner.ReadAsPropertyName(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        inner.WriteAsPropertyName(writer, value, options);
}
