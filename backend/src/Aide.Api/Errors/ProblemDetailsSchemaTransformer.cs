using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aide.Api.Errors;

/// <summary>Documents the <c>errorCode</c>, <c>traceId</c>, <c>details</c>, <c>reason</c> and <c>installUrl</c> extensions on the ProblemDetails
/// schemas, so the generated client types them.</summary>
internal sealed class ProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type != typeof(ProblemDetails) && type != typeof(HttpValidationProblemDetails))
        {
            return Task.CompletedTask;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties[ApiProblems.ErrorCodeKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Stable UPPER_SNAKE error code from the spec's error table (SPEC §6).",
        };
        schema.Properties[ApiProblems.TraceIdKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Trace id for correlating the error with server logs.",
        };
        schema.Properties[ApiProblems.DetailsKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = new OpenApiSchema { Type = JsonSchemaType.String },
            Description = "Individual reasons, when there are several.",
        };
        schema.Properties[ApiProblems.ReasonKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Machine-readable sub-reason for some codes. RESTORE_NOT_ALLOWED: stage_completed, stage_not_reached, other_release_version, corrupted or releasing.",
        };
        schema.Properties[ApiProblems.InstallUrlKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "GITHUB_APP_NOT_INSTALLED: the AIDE GitHub App's install link, when an App is registered (FR-81, FR-83).",
        };
        return Task.CompletedTask;
    }
}
