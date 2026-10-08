using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aide.Api.Json;

/// <summary>
/// Documents <see cref="JsonObject"/> (schemaless payloads such as <c>ActivityItemDto.payload</c>) as a free-form object
/// (<c>additionalProperties: {}</c>), so generated clients type it as a string-keyed map of unknown values rather than an
/// empty object.
/// </summary>
internal sealed class FreeFormObjectSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(JsonObject))
        {
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = new OpenApiSchema();
        }

        return Task.CompletedTask;
    }
}
