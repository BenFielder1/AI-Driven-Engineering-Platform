using Aide.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Errors;

/// <summary>
/// The one mapping from error codes to ProblemDetails responses (CLAUDE.md rule 4). Every error body carries an
/// <c>errorCode</c> from <see cref="ErrorCodes"/>; its status comes from <see cref="ErrorCodes.StatusCodeFor"/>.
/// <c>traceId</c> is added by <see cref="ProblemDetailsSetup"/>.
/// </summary>
public static class ApiProblems
{
    public const string ErrorCodeKey = "errorCode";
    public const string TraceIdKey = "traceId";
    public const string DetailsKey = "details";
    public const string ReasonKey = "reason";

    /// <summary>FR-81, FR-83: the AIDE GitHub App's install link on a <c>GITHUB_APP_NOT_INSTALLED</c> problem.</summary>
    public const string InstallUrlKey = "installUrl";

    /// <summary>
    /// The <c>errorCode</c> of a 501 contract stub: the endpoint is in the contract (so the client can be built against
    /// it) but not implemented yet. Not in <see cref="ErrorCodes"/>: no implemented endpoint returns it.
    /// </summary>
    public const string NotImplementedCode = "NOT_IMPLEMENTED";

    /// <summary>A ProblemDetails result for a domain error (status from the domain's HTTP status map).</summary>
    public static ProblemHttpResult FromError(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?> { [ErrorCodeKey] = error.Code };
        if (error.Details.Count > 0)
        {
            extensions[DetailsKey] = error.Details;
        }

        if (error.Reason is not null)
        {
            extensions[ReasonKey] = error.Reason;
        }

        return TypedResults.Problem(
            title: error.Message,
            statusCode: StatusFor(error.Code),
            extensions: extensions);
    }

    public static ProblemHttpResult Of(string code, string title) => FromError(new DomainError(code, title));

    /// <summary>501 for an endpoint whose contract is committed but whose behaviour isn't built yet.</summary>
    public static ProblemHttpResult NotImplemented(string title = "This endpoint is not implemented yet.") =>
        TypedResults.Problem(
            title: title,
            statusCode: StatusCodes.Status501NotImplemented,
            extensions: new Dictionary<string, object?> { [ErrorCodeKey] = NotImplementedCode });

    /// <summary>409 <c>GITHUB_APP_NOT_INSTALLED</c> with the App's install link (when an App is registered).</summary>
    public static ProblemHttpResult GitHubAppNotInstalled(string? installUrl, string title = "The AIDE GitHub App isn't installed on this account or repository.")
    {
        var extensions = new Dictionary<string, object?> { [ErrorCodeKey] = ErrorCodes.GitHubAppNotInstalled };
        if (installUrl is not null)
        {
            extensions[InstallUrlKey] = installUrl;
        }

        return TypedResults.Problem(title: title, statusCode: StatusFor(ErrorCodes.GitHubAppNotInstalled), extensions: extensions);
    }

    public static ProblemHttpResult NotFound(string title = "The resource was not found.") => Of(ErrorCodes.NotFound, title);

    public static ProblemHttpResult Forbidden(string title = "You don't have permission to do that.") => Of(ErrorCodes.Forbidden, title);

    /// <summary>400 <c>VALIDATION</c> with an <c>errors</c> dictionary (field → messages).</summary>
    public static ValidationProblem Validation(IDictionary<string, string[]> errors, string title = "One or more fields are invalid.") =>
        TypedResults.ValidationProblem(
            errors,
            title: title,
            extensions: new Dictionary<string, object?> { [ErrorCodeKey] = ErrorCodes.Validation });

    /// <summary>400 <c>VALIDATION</c> from a domain validation error whose details read <c>"field: message"</c>.</summary>
    public static ValidationProblem Validation(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var errors = error.Details
            .Select(d => d.Split(": ", 2))
            .GroupBy(parts => parts.Length == 2 ? parts[0] : string.Empty, parts => parts[^1], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return Validation(errors, error.Message);
    }

    /// <summary>The status for a code; unknown codes are a server bug, so 500.</summary>
    public static int StatusFor(string code) => ErrorCodes.StatusCodeFor(code) ?? StatusCodes.Status500InternalServerError;

    /// <summary>The default <c>errorCode</c> for a bare status (framework-produced errors such as 401/404/429).</summary>
    public static string? DefaultCodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.Validation,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.Validation,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.Validation,
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        StatusCodes.Status429TooManyRequests => ErrorCodes.RateLimited,
        _ => null,
    };

    /// <summary>Writes a ProblemDetails body outside an endpoint (auth handlers, middleware).</summary>
    public static async Task WriteAsync(HttpContext httpContext, string code, string title)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var status = StatusFor(code);
        httpContext.Response.StatusCode = status;
        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions = { [ErrorCodeKey] = code },
            },
        });
    }
}
