using System.Diagnostics;
using System.Text.Json;
using Aide.Api.Logging;
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
            if (problem is HttpValidationProblemDetails validation)
            {
                CamelCaseErrorKeys(validation.Errors);
            }

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

    /// <summary>
    /// Validation error keys name fields as they appear on the wire: camelCase per path segment
    /// (<c>Git.RepoUrl</c> → <c>git.repoUrl</c>, <c>Channels[0].Target</c> → <c>channels[0].target</c>). The built-in
    /// endpoint validation keys errors by CLR property name; domain validation already uses wire names.
    /// </summary>
    internal static void CamelCaseErrorKeys(IDictionary<string, string[]> errors)
    {
        foreach (var key in errors.Keys.ToList())
        {
            var wireKey = string.Join('.', key.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
            if (wireKey == key)
            {
                continue;
            }

            var messages = errors[key];
            errors.Remove(key);
            errors[wireKey] = errors.TryGetValue(wireKey, out var existing) ? [.. existing, .. messages] : messages;
        }
    }
}

/// <summary>Maps exceptions to ProblemDetails: optimistic-concurrency failures → 409 <c>CONFLICT</c>, malformed
/// requests → 400 <c>VALIDATION</c>, GitHub rate limits → 429 <c>GITHUB_RATE_LIMITED</c> with <c>Retry-After</c> (§4.14),
/// everything else → 500 without exception details.</summary>
public sealed partial class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is Infrastructure.GitHub.GitHubRateLimitedException limited)
        {
            // §4.14: GitHub's rate limit for the token in use; Retry-After is the seconds until its reset.
            var now = httpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            ResponseDocs.SetRetryAfter(httpContext, TimeSpan.FromSeconds(Domain.GitHub.GitHubRules.RetryAfterSeconds(limited.ResetAt, now)));
        }

        if (exception is Infrastructure.GitLab.GitLabRateLimitedException gitLabLimited)
        {
            var now = httpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            ResponseDocs.SetRetryAfter(httpContext, TimeSpan.FromSeconds(Domain.GitHub.GitHubRules.RetryAfterSeconds(gitLabLimited.ResetAt, now)));
        }

        var (status, code, title) = exception switch
        {
            Infrastructure.GitLab.GitLabRateLimitedException => (StatusCodes.Status429TooManyRequests, ErrorCodes.UpstreamRateLimited,
                "GitLab is rate limiting requests. Try again after the time in Retry-After."),
            Infrastructure.GitHub.GitHubRateLimitedException => (StatusCodes.Status429TooManyRequests, ErrorCodes.GitHubRateLimited,
                "GitHub's API rate limit is exhausted. Try again after the time in Retry-After."),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "The resource was changed by someone else. Reload and try again."),
            BadHttpRequestException bad => (bad.StatusCode, ApiProblems.DefaultCodeFor(bad.StatusCode), "The request is invalid."),
            _ => (StatusCodes.Status500InternalServerError, (string?)null, "An unexpected error occurred."),
        };

        var path = LogPaths.Redact(httpContext.Request.Path);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, path);
        }
        else
        {
            LogHandled(logger, exception, status, httpContext.Request.Method, path);
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
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exception mapped to {Status} for {Method} {Path}")]
    private static partial void LogHandled(ILogger logger, Exception exception, int status, string method, string path);
}
