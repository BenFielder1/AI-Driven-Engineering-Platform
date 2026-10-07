using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Domain;

namespace Aide.Api.Json;

/// <summary>
/// Domain enums on the wire use the same snake_case names as SPEC.md and the database (<c>SystemRole.Admin</c> ⇄
/// <c>"admin"</c>, <c>StepStatus.InProgress</c> ⇄ <c>"in_progress"</c>, matching <c>EnumStrings</c>). Only Aide.Domain
/// enums are affected, so API-only enums keep their own converters.
/// </summary>
public sealed class DomainEnumJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsEnum && typeToConvert.Assembly == typeof(DomainAssembly).Assembly;
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
