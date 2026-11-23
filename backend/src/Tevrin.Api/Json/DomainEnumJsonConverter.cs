using System.Text.Json;
using System.Text.Json.Serialization;
using Tevrin.Domain;
using Tevrin.Engine.Domain;

namespace Tevrin.Api.Json;

/// <summary>
/// Domain enums on the wire use the same snake_case names as SPEC.md and the database (<c>SystemRole.Admin</c> ⇄
/// <c>"admin"</c>, <c>StepStatus.InProgress</c> ⇄ <c>"in_progress"</c>, matching <c>EnumStrings</c>). Only Tevrin.Domain
/// and Tevrin.Engine.Domain enums (FR-262: e.g. <c>JobStatus</c>, <c>GateStatus</c>, <c>LlmProvider</c> moved there) are
/// affected, so API-only enums keep their own converters.
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
        return factory.CreateConverter(typeToConvert, options);
    }
}
