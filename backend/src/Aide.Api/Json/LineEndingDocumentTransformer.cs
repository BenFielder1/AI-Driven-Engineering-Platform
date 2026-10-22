using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aide.Api.Json;

/// <summary>
/// XML-doc descriptions carry the source file's line endings, so a contract generated on Windows can differ from one
/// generated on CI (<c>\r\n</c> vs <c>\n</c>). Runs on the finished document and normalises every description to
/// <c>\n</c>: component schemas and everything nested in them (properties, items, additional properties,
/// <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>), plus operation parameters, request bodies and responses — including the
/// property wrappers (<c>{ description, $ref }</c>) that schema transformers never visit.
/// </summary>
internal sealed class LineEndingDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (document.Components?.Schemas is { } components)
        {
            foreach (var schema in components.Values)
            {
                Normalise(schema);
            }
        }

        if (document.Paths is { } paths)
        {
            foreach (var pathItem in paths.Values)
            {
                if (pathItem.Operations is not { } operations)
                {
                    continue;
                }

                foreach (var operation in operations.Values)
                {
                    NormaliseOperation(operation);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void NormaliseOperation(OpenApiOperation operation)
    {
        if (Lf(operation.Summary) is { } summary)
        {
            operation.Summary = summary;
        }

        if (Lf(operation.Description) is { } description)
        {
            operation.Description = description;
        }

        if (operation.Parameters is { } parameters)
        {
            foreach (var parameter in parameters)
            {
                if (parameter is OpenApiParameter inline && Lf(inline.Description) is { } parameterDescription)
                {
                    inline.Description = parameterDescription;
                }

                Normalise(parameter.Schema);
            }
        }

        NormaliseContent(operation.RequestBody?.Content);

        if (operation.Responses is { } responses)
        {
            foreach (var response in responses.Values)
            {
                NormaliseContent(response.Content);
            }
        }
    }

    private static void NormaliseContent(IDictionary<string, OpenApiMediaType>? content)
    {
        if (content is null)
        {
            return;
        }

        foreach (var mediaType in content.Values)
        {
            Normalise(mediaType.Schema);
        }
    }

    private static void Normalise(IOpenApiSchema? schema)
    {
        switch (schema)
        {
            case OpenApiSchema inline:
                if (Lf(inline.Description) is { } description)
                {
                    inline.Description = description;
                }

                if (inline.Properties is { } properties)
                {
                    foreach (var property in properties.Values)
                    {
                        Normalise(property);
                    }
                }

                Normalise(inline.Items);
                Normalise(inline.AdditionalProperties);
                NormaliseAll(inline.AllOf);
                NormaliseAll(inline.AnyOf);
                NormaliseAll(inline.OneOf);
                break;
            case OpenApiSchemaReference { Reference: { } reference }:
                // A reference carrying its own description (a documented property of a named type) keeps that override on
                // its Reference object. Never go through `OpenApiSchemaReference.Description`: it reads through to the
                // target when there is no override, and assigning it would pin a copy of the target's description onto
                // the `$ref`. The target itself is a component and is normalised from there.
                if (Lf(reference.Description) is { } referenceDescription)
                {
                    reference.Description = referenceDescription;
                }

                break;
            default:
                break;
        }
    }

    private static void NormaliseAll(IList<IOpenApiSchema>? schemas)
    {
        if (schemas is null)
        {
            return;
        }

        foreach (var schema in schemas)
        {
            Normalise(schema);
        }
    }

    /// <summary>
    /// The normalised text when it differs from <paramref name="text"/>, else <c>null</c>. Writing an unchanged value
    /// back is not harmless: a schema reference's description reads through to its target, and assigning it would pin
    /// a copy of the target's description onto every <c>$ref</c>.
    /// </summary>
    private static string? Lf(string? text)
    {
        if (text is null || !text.Contains('\r', StringComparison.Ordinal))
        {
            return null;
        }

        return text.ReplaceLineEndings("\n");
    }
}
