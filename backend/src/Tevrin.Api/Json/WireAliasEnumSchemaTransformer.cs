using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Api.Json;

/// <summary>
/// FR-326 (D-0.8.0-10): a domain enum with read-only aliases is serialised by <see cref="AliasEnumJsonConverter{TEnum}"/>,
/// which the schema generator can't see into, so its schema loses its values. This puts back the values it writes
/// (<c>MergeStrategy</c>: <c>tevrin_merges</c>, not the <c>aide_merges</c> alias it still reads), as the
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter{TEnum}"/> schema had them.
/// </summary>
internal sealed class WireAliasEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly MethodInfo ValuesMethod = typeof(EnumStrings).GetMethod(nameof(EnumStrings.Values))!;

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        var type = context.JsonTypeInfo.Type;
        if (type.IsEnum
            && context.JsonTypeInfo.Converter.GetType() is { IsGenericType: true } converter
            && converter.GetGenericTypeDefinition() == typeof(AliasEnumJsonConverter<>))
        {
            var values = (IReadOnlyList<string>)ValuesMethod.MakeGenericMethod(type).Invoke(null, null)!;
            schema.Enum = [.. values.Select(v => (JsonNode)v)];
        }

        return Task.CompletedTask;
    }
}
