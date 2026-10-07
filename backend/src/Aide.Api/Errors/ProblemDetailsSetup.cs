using System.Diagnostics;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Errors;

/// <summary>ProblemDetails everywhere (§6): <c>errorCode</c> + <c>traceId</c> on every error body, including
/// framework-produced ones (status-code pages, validation, auth challenges, rate limiting).</summary>
public static class ProblemDetailsSetup
{
    public static IServiceCollection AddAideProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            problem.Extensions[ApiProblems.TraceIdKey] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            if (!problem.Extensions.ContainsKey(ApiProblems.ErrorCodeKey) && ApiProblems.DefaultCodeFor(status) is { } code)
            {
                problem.Extensions[ApiProblems.ErrorCodeKey] = code;
            }
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }
}

/// <summary>Maps exceptions to ProblemDetails: optimistic-concurrency failures → 409 <c>CONFLICT</c>, malformed
/// requests → 400 <c>VALIDATION</c>, everything else → 500 without exception details.</summary>
public sealed partial class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (status, code, title) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "The resource was changed by someone else. Reload and try again."),
            BadHttpRequestException bad => (bad.StatusCode, ApiProblems.DefaultCodeFor(bad.StatusCode), "The request is invalid."),
            _ => (StatusCodes.Status500InternalServerError, (string?)null, "An unexpected error occurred."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogHandled(logger, exception, status, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        if (code is not null)
        {
            problem.Extensions[ApiProblems.ErrorCodeKey] = code;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exception mapped to {Status} for {Method} {Path}")]
    private static partial void LogHandled(ILogger logger, Exception exception, int status, string method, PathString path);
}
