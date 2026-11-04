using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Jobs;
using Aide.Domain.Trackers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Trackers;

/// <summary>
/// FR-165, FR-169: the caller's trackers (<c>/api/me/trackers</c>), and FR-170, FR-171, FR-173: a project's imports
/// (<c>/api/projects/:id/sources</c>). Endpoints stay thin; the services hold the flow.
/// </summary>
public static class TrackerEndpoints
{
    private const string Browse409 = "TRACKER_NOT_CONNECTED (no usable connection for that tracker; a rejected token also marks it invalid) or TRACKER_PERMISSION_MISSING (GitHub: the App lacks Issues: read; extension installUrl is the App settings link)";
    private const string Browse400 = "VALIDATION, CONNECTION_SCOPE_INSUFFICIENT (the token can't read this) or TRACKER_QUERY_INVALID (the title holds the tracker's message)";
    private const string RateLimited429 = "UPSTREAM_RATE_LIMITED (the tracker's 429; Retry-After when known)";
    private const string Upstream502 = "UPSTREAM_ERROR (the tracker failed or didn't answer within Trackers:SearchTimeout)";

    public static IEndpointRouteBuilder MapTrackerEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me/trackers")
            .WithTags("Trackers")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapGet("/", ListTrackers)
            .WithName("ListTrackers")
            .WithSummary("FR-165: Jira, Linear, GitHub Issues and GitLab Issues for the caller: whether each is ready, why not (not_connected, app_not_registered, issues_permission_missing, with the App settings link) and its connections.")
            .Produces<IReadOnlyList<TrackerStatusDto>>();

        me.MapGet("/{provider}/containers", ListTrackerContainers)
            .WithName("ListTrackerContainers")
            .WithSummary("FR-169: Jira projects, Linear teams and projects, GitHub repositories or GitLab projects the connection reaches, a page at a time. The next page's cursor is nextCursor. An unknown provider: 400 VALIDATION.")
            .Produces<TrackerContainerPageDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, Browse400),
                (StatusCodes.Status403Forbidden, "CREDENTIAL_NOT_OWNER: another user's connection."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the connection."),
                (StatusCodes.Status409Conflict, Browse409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        me.MapPost("/{provider}/search", SearchTrackerItems)
            .WithName("SearchTrackerItems")
            .WithSummary("FR-169: item summaries matching a native query (JQL for Jira, search qualifiers for GitHub) or structured filters, paginated with the provider's cursor. Done items are hidden unless the filter asks for them. GitHub never returns pull requests. Never a description or comment.")
            .Produces<TrackerSearchResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, Browse400),
                (StatusCodes.Status403Forbidden, "CREDENTIAL_NOT_OWNER: another user's connection."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the connection or container."),
                (StatusCodes.Status409Conflict, Browse409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        var sources = app.MapGroup("/api/projects/{id:guid}/sources")
            .WithTags("Sources")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        sources.MapGet("/", GetProjectSources)
            .WithName("GetProjectSources")
            .WithSummary("FR-170, FR-173 (members): a release version's imports, newest first (default: the current version), the current source_items version, whether the latest project request draft is behind it (\"Sources changed since this draft\", approval confirmation) and whether items can be changed now.")
            .Produces<ProjectSourcesDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or a releaseVersionId of another project."));

        sources.MapPost("/", AddProjectSource)
            .WithName("AddProjectSource")
            .WithSummary("FR-171 (Contributor or Owner): queues a tracker_import job for the selection, in refinement only. Returns 202 with the queued import; it writes a new source_items version when it completes.")
            .Produces<SourceImportDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION or SOURCE_ITEMS_TOO_LARGE (over 200 items with what's active)."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (a Viewer) or CREDENTIAL_NOT_OWNER (another user's connection)."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or connection."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE (D-V5-10: new work belongs in a new version, or after a rollback) or TRACKER_NOT_CONNECTED."));

        sources.MapPost("/check", CheckProjectSources)
            .WithName("CheckProjectSources")
            .WithSummary("FR-173 (members): compares updatedAt per item with the tracker: unchanged, changed, removed or unknown. Uses the importer's connection, else the caller's own, else unknown. Cached for 5 minutes, capped at 10 seconds. Never changes a snapshot.")
            .Produces<SourcesCheckDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project."));

        sources.MapPost("/{importId:guid}/reimport", ReimportProjectSource)
            .WithName("ReimportProjectSource")
            .WithSummary("FR-173 (Contributor or Owner): repeats an import's query (also the retry of a failed one). Returns 202 with the new queued import; when it completes it supersedes the old one and writes a new source_items version. Earlier versions stay readable.")
            .Produces<SourceImportDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN: a Viewer."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or an import that isn't active in the current version."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE or TRACKER_NOT_CONNECTED."));

        sources.MapDelete("/{importId:guid}", RemoveProjectSource)
            .WithName("RemoveProjectSource")
            .WithSummary("FR-173 (Contributor or Owner): removes an import. A completed one writes a new source_items version without it. Earlier versions stay readable (rule 9).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN: a Viewer."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project or an import that isn't active in the current version."),
                (StatusCodes.Status409Conflict, "WRONG_PHASE or CONFLICT."));

        return app;
    }

    private static async Task<Ok<IReadOnlyList<TrackerStatusDto>>> ListTrackers(ICurrentUser currentUser, TrackerBrowseService trackers, CancellationToken cancellationToken) =>
        TypedResults.Ok(await trackers.ListAsync(currentUser.RequiredUserId, cancellationToken));

    private static async Task<Results<Ok<TrackerContainerPageDto>, ValidationProblem, ProblemHttpResult>> ListTrackerContainers(
        [WireEnum(typeof(TrackerProvider))] string provider,
        ICurrentUser currentUser,
        TrackerBrowseService trackers,
        HttpContext http,
        CancellationToken cancellationToken,
        Guid? connectionId = null,
        [MaxLength(TrackerRules.MaxFilterTextLength)] string? search = null,
        [MaxLength(4000)] string? cursor = null)
    {
        if (!EnumStrings.TryParse<TrackerProvider>(provider, out var parsed))
        {
            return UnknownProvider();
        }

        var result = await trackers.ContainersAsync(currentUser.RequiredUserId, currentUser.Actor, parsed, connectionId, search, cursor, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<TrackerSearchResultDto>, ValidationProblem, ProblemHttpResult>> SearchTrackerItems(
        [WireEnum(typeof(TrackerProvider))] string provider,
        TrackerSearchRequest request,
        ICurrentUser currentUser,
        TrackerBrowseService trackers,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!EnumStrings.TryParse<TrackerProvider>(provider, out var parsed))
        {
            return UnknownProvider();
        }

        var result = await trackers.SearchAsync(currentUser.RequiredUserId, currentUser.Actor, parsed, request, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ProjectSourcesDto>, ProblemHttpResult>> GetProjectSources(
        Guid id,
        IProjectAccess access,
        SourceImportService imports,
        CancellationToken cancellationToken,
        Guid? releaseVersionId = null)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await imports.GetAsync(id, releaseVersionId, cancellationToken);
        if (result.IsFailure)
        {
            return ApiProblems.FromError(result.Error!);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Accepted<SourceImportDto>, ValidationProblem, ProblemHttpResult>> AddProjectSource(
        Guid id,
        SourceImportRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        SourceImportService imports,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RunAgents, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await imports.AddAsync(id, request, currentUser.RequiredUserId, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Accepted($"/api/projects/{id}/sources", result.Value);
    }

    private static async Task<Results<Ok<SourcesCheckDto>, ProblemHttpResult>> CheckProjectSources(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        SourceImportService imports,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await imports.CheckAsync(id, currentUser.RequiredUserId, cancellationToken);
        if (result.IsFailure)
        {
            return ApiProblems.FromError(result.Error!);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Accepted<SourceImportDto>, ProblemHttpResult>> ReimportProjectSource(
        Guid id,
        Guid importId,
        IProjectAccess access,
        ICurrentUser currentUser,
        SourceImportService imports,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RunAgents, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await imports.ReimportAsync(id, importId, currentUser.RequiredUserId, cancellationToken);
        if (result.IsFailure)
        {
            return ApiProblems.FromError(result.Error!);
        }

        return TypedResults.Accepted($"/api/projects/{id}/sources", result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveProjectSource(
        Guid id,
        Guid importId,
        IProjectAccess access,
        ICurrentUser currentUser,
        SourceImportService imports,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.RunAgents, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var result = await imports.RemoveAsync(id, importId, currentUser.RequiredUserId, currentUser.Actor, cancellationToken);
        if (result.IsFailure)
        {
            return ApiProblems.FromError(result.Error!);
        }

        return TypedResults.NoContent();
    }

    private static ValidationProblem UnknownProvider() =>
        ApiProblems.Validation(new Dictionary<string, string[]> { ["provider"] = ["Choose jira, linear, github or gitlab."] });

}

public static class TrackerServiceCollectionExtensions
{
    /// <summary>FR-165–FR-173: tracker browsing, project imports and the import job.</summary>
    public static IServiceCollection AddTrackers(this IServiceCollection services)
    {
        services.AddScoped<TrackerConnectionResolver>();
        services.AddScoped<TrackerBrowseService>();
        services.AddScoped<SourceSnapshotWriter>();
        services.AddScoped<SourceImportService>();
        services.AddScoped<IJobEndedHook, TrackerImportJobEndedHook>();
        services.AddJobHandler<TrackerImportJobHandler>(JobTypes.TrackerImport);
        return services;
    }
}
