using Microsoft.OpenApi;

namespace Aide.Api.Errors;

/// <summary>Documents which <c>errorCode</c>s an endpoint's error responses carry (descriptions in the OpenAPI contract).</summary>
public static class ResponseDocs
{
    public const string RetryAfterHeader = "Retry-After";

    /// <summary>Sets the description of each listed response status (e.g. <c>409 → "PHASE_PRECONDITION_FAILED, GATE_PENDING or CONFLICT"</c>).</summary>
    public static RouteHandlerBuilder WithResponseDescriptions(this RouteHandlerBuilder builder, params (int Status, string Description)[] descriptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (var (status, description) in descriptions)
            {
                if (operation.Responses?.TryGetValue(status.ToString(System.Globalization.CultureInfo.InvariantCulture), out var response) == true
                    && response is OpenApiResponse concrete)
                {
                    concrete.Description = description;
                }
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Declares the <c>Retry-After</c> header (seconds) on the 429 response.</summary>
    public static RouteHandlerBuilder WithRetryAfterOn429(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Responses?.TryGetValue("429", out var response) == true && response is OpenApiResponse concrete)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                concrete.Headers[RetryAfterHeader] = new OpenApiHeader
                {
                    Description = "Seconds until the action is allowed again.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                };
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Sets <c>Retry-After</c> (whole seconds, rounded up) for a domain error that carries one.</summary>
    public static void SetRetryAfter(HttpContext httpContext, TimeSpan? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (retryAfter is { } wait)
        {
            httpContext.Response.Headers[RetryAfterHeader] =
                Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
