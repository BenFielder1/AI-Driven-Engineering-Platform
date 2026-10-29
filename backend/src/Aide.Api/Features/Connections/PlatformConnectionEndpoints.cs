using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Platforms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Connections;

/// <summary>
/// FR-142: the caller's platform connections (§6 <c>/api/me/connections/platforms</c>, replacing v2's
/// <c>/api/me/connections/vercel</c>, D-V4-4): Vercel, Netlify, Cloudflare, Render, Railway and Supabase tokens, plus picker
/// data (accounts and resources). Connections are personal: every route sees only the caller's own (another user's id is
/// <c>NOT_FOUND</c>), and nobody can read a token back. Mutations need a browser session: API tokens get <c>FORBIDDEN</c>
/// (§6 Notes). Endpoints stay thin: <see cref="PlatformConnectionService"/> holds the flow.
/// </summary>
public static class PlatformConnectionEndpoints
{
    private const string Invalid400 = "VALIDATION, CREDENTIAL_INVALID (the platform rejected the token; nothing is saved) or CONNECTION_SCOPE_INSUFFICIENT (a Cloudflare account-owned cfat_ token: Workers Builds needs a user token)";
    private const string RateLimited429 = "UPSTREAM_RATE_LIMITED (the platform's 429; Retry-After when known)";
    private const string Upstream502 = "UPSTREAM_ERROR (the platform failed or couldn't be reached)";
    private const string Use400 = "VALIDATION (a type the provider doesn't list, or a malformed accountId), CREDENTIAL_INVALID (the token is invalid or was just rejected; the connection is now invalid) or CONNECTION_SCOPE_INSUFFICIENT (the token lacks a permission for this request; the connection's status is unchanged)";

    public static IEndpointRouteBuilder MapPlatformConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/connections/platforms")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListPlatformConnections)
            .WithName("ListPlatformConnections")
            .WithSummary("FR-142: the caller's platform connections (Vercel, Netlify, Cloudflare, Render, Railway, Supabase), newest first: label, last 4, account, metadata (teams, workspaces, orgs, policies, missing permissions), scopes, expiry, status and bound projects. Never the token.")
            .Produces<IReadOnlyList<PlatformConnectionDto>>();

        group.MapPost("/", CreatePlatformConnection)
            .WithName("CreatePlatformConnection")
            .WithSummary("FR-142: add a pasted platform token. It is checked with the platform first (§4.20 / §4.21): rejected → 400 CREDENTIAL_INVALID (nothing saved); a 403 for missing permissions saves it with status insufficient_scope and the missing names; a Cloudflare cfat_ token → 400 CONNECTION_SCOPE_INSUFFICIENT. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<PlatformConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, Invalid400),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        group.MapPatch("/{id:guid}", RenamePlatformConnection)
            .WithName("RenamePlatformConnection")
            .WithSummary("FR-142: rename one of the caller's platform connections. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<PlatformConnectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeletePlatformConnection)
            .WithName("DeletePlatformConnection")
            .WithSummary("FR-142, FR-144: delete one of the caller's platform connections. Deployment targets bound to it lose their binding (targets and history stay); deployments then fail with DEPLOYMENT_NOT_CONFIGURED until an Owner binds another. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", TestPlatformConnection)
            .WithName("TestPlatformConnection")
            .WithSummary("FR-142: check the token with the platform again and record the result (valid, insufficient_scope with the missing names, or invalid — then its owner is notified; lastValidatedAt; account and metadata refreshed). 429 UPSTREAM_RATE_LIMITED or 502 UPSTREAM_ERROR leave the status unchanged. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<PlatformConnectionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        group.MapGet("/{id:guid}/accounts", ListPlatformConnectionAccounts)
            .WithName("ListPlatformConnectionAccounts")
            .WithSummary("FR-142 picker: the teams (Vercel), accounts (Netlify, Cloudflare), workspaces (Render, Railway) or organisations (Supabase) the connection's token reaches. A rejected token marks the connection invalid (its owner is notified).")
            .Produces<IReadOnlyList<PlatformAccountDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, Use400),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        group.MapGet("/{id:guid}/resources", ListPlatformConnectionResources)
            .WithName("ListPlatformConnectionResources")
            .WithSummary("FR-142 picker: the connection's resources of a type (default: the provider's first): Vercel projects; Netlify sites; Cloudflare workers or projects (Pages); Render services or regions; Railway projects or regions; Supabase projects, orgs or regions. accountId scopes the list (default: the connection's account). A type the provider doesn't list → 400 VALIDATION.")
            .Produces<IReadOnlyList<PlatformResourceDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, Use400),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        return app;
    }

    private static async Task<Ok<IReadOnlyList<PlatformConnectionDto>>> ListPlatformConnections(
        ICurrentUser currentUser, PlatformConnectionService connections, CancellationToken cancellationToken) =>
        TypedResults.Ok(await connections.ListAsync(currentUser.RequiredUserId, cancellationToken));

    private static async Task<Results<Created<PlatformConnectionDto>, ValidationProblem, ProblemHttpResult>> CreatePlatformConnection(
        CreatePlatformConnectionRequest request,
        ICurrentUser currentUser,
        PlatformConnectionService connections,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await connections.CreateAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created($"/api/me/connections/platforms/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<PlatformConnectionDto>, ValidationProblem, ProblemHttpResult>> RenamePlatformConnection(
        Guid id,
        UpdatePlatformConnectionRequest request,
        ICurrentUser currentUser,
        PlatformConnectionService connections,
        CancellationToken cancellationToken)
    {
        var result = await connections.RenameAsync(currentUser.RequiredUserId, id, request.Label, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeletePlatformConnection(
        Guid id, ICurrentUser currentUser, PlatformConnectionService connections, CancellationToken cancellationToken) =>
        await connections.DeleteAsync(currentUser.RequiredUserId, id, currentUser.Actor, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound("The connection was not found.");

    private static async Task<Results<Ok<PlatformConnectionDto>, ProblemHttpResult>> TestPlatformConnection(
        Guid id, ICurrentUser currentUser, PlatformConnectionService connections, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await connections.TestAsync(currentUser.RequiredUserId, id, currentUser.Actor, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<IReadOnlyList<PlatformAccountDto>>, ProblemHttpResult>> ListPlatformConnectionAccounts(
        Guid id, ICurrentUser currentUser, PlatformConnectionService connections, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await connections.AccountsAsync(currentUser.RequiredUserId, id, currentUser.Actor, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<IReadOnlyList<PlatformResourceDto>>, ValidationProblem, ProblemHttpResult>> ListPlatformConnectionResources(
        Guid id,
        ICurrentUser currentUser,
        PlatformConnectionService connections,
        HttpContext http,
        CancellationToken cancellationToken,
        [WireEnum(typeof(PlatformResourceType))] string? type = null,
        [MaxLength(PlatformConnectionRules.MaxAccountIdLength)] string? accountId = null)
    {
        var result = await connections.ResourcesAsync(currentUser.RequiredUserId, id, type, accountId, currentUser.Actor, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }
}
