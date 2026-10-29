using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// Bitbucket Cloud (FR-133–FR-135, §6): Admin → Integrations → Bitbucket (<c>/api/admin/integrations/bitbucket</c>),
/// sign-in and connect through the OAuth consumer (<c>/api/auth/bitbucket/…</c>), the caller's connections
/// (<c>/api/me/connections/bitbucket</c>) and the repo picker and repository creation (<c>/api/me/git/bitbucket/…</c>).
/// Admin and connection mutations need a browser session (§6 Notes). Tokens, emails of API tokens and the consumer secret are
/// never returned.
/// </summary>
public static class BitbucketEndpoints
{
    private const string Upstream502 = "UPSTREAM_ERROR (Bitbucket failed or couldn't be reached)";
    private const string RateLimited429 = "UPSTREAM_RATE_LIMITED (Bitbucket's 429, or the connection's budget of 900 requests an hour; Retry-After when known)";
    private const string NotConnected409 = "GIT_NOT_CONNECTED (the connection is invalid)";

    public static IEndpointRouteBuilder MapBitbucketEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdmin(app);
        MapAuth(app);
        MapConnections(app);
        MapGit(app);
        return app;
    }

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/bitbucket")
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        admin.MapGet("/", GetAdminBitbucket)
            .WithName("GetAdminBitbucketIntegration")
            .WithSummary("FR-133: the Bitbucket OAuth consumer (secret as a flag), where it comes from (database or Bitbucket__OAuth__* configuration), the callback URL and the consumer permissions to tick, and the number of Bitbucket connections. Admins only.")
            .Produces<AdminBitbucketIntegrationDto>();

        admin.MapPut("/", UpdateAdminBitbucket)
            .WithName("UpdateAdminBitbucketIntegration")
            .WithSummary("FR-133: save the Bitbucket OAuth consumer's key and secret (encrypted; an omitted secret keeps the saved one). Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<AdminBitbucketIntegrationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions((StatusCodes.Status409Conflict, "CONFLICT (the consumer is configured in the environment, Bitbucket__OAuth__*)"));

        admin.MapDelete("/", DeleteAdminBitbucket)
            .WithName("DeleteAdminBitbucketIntegration")
            .WithSummary("FR-133: remove the saved Bitbucket OAuth consumer. Sign-in and OAuth connect stop being offered and OAuth connections become invalid at their next refresh; API-token connections keep working. Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions((StatusCodes.Status409Conflict, "CONFLICT (the consumer is configured in the environment, Bitbucket__OAuth__*)"));
    }

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth/bitbucket").WithTags("Auth");

        auth.MapGet("/start", StartBitbucketOAuth)
            .WithName("StartBitbucketOAuth")
            .WithSummary("FR-134: redirects to Bitbucket's OAuth authorize page (authorization code; scopes fixed on the consumer; signed single-use state bound to this browser). mode=signin (\"Continue with Bitbucket\", anonymous) or connect (Account → Connections; signed-in browser session). returnUrl is a same-origin path. 404 when no consumer is configured.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status401Unauthorized, "UNAUTHENTICATED (mode=connect without a session)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (mode=connect with an API token)"));

        auth.MapGet("/callback", BitbucketOAuthCallback)
            .WithName("BitbucketOAuthCallback")
            .WithSummary("FR-134: Bitbucket's redirect: checks the state, exchanges the code (client Basic auth), reads the identity (GET /2.0/user) and confirmed email (GET /2.0/user/emails), then signs in (account matched by bitbucket + account uuid, or created for a new confirmed email; the first account becomes Admin), or links to the signed-in user, and stores the connection. Redirects to returnUrl; on failure to /login (sign-in) or returnUrl (connect) with error=GIT_AUTH_FAILED&reason=…&provider=bitbucket; when the confirmed email belongs to an existing unlinked account, to /login?link=bitbucket&returnUrl=… (confirm with POST /api/auth/bitbucket/link).")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found);

        auth.MapGet("/link", GetBitbucketPendingLink)
            .WithName("GetBitbucketPendingLink")
            .WithSummary("FR-134: the Bitbucket sign-in waiting in this browser to be linked to the existing account with the same confirmed email (10 minutes). 404 when there is none.")
            .AllowAnonymous()
            .Produces<BitbucketPendingLinkDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        auth.MapPost("/link", ConfirmBitbucketLink)
            .WithName("ConfirmBitbucketLink")
            .WithSummary("FR-134, D-V4-18: the signed-in owner of the matching account confirms: the Bitbucket identity is linked (later sign-ins match it) and connected. Never done silently. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN (signed in as a different account than the one with that email)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (no pending link, or it expired)"),
                (StatusCodes.Status409Conflict, "CONFLICT (the Bitbucket identity was linked to another account meanwhile)"));

        auth.MapDelete("/link", CancelBitbucketLink)
            .WithName("CancelBitbucketLink")
            .WithSummary("FR-134: discard this browser's pending Bitbucket link.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapConnections(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/connections/bitbucket")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListBitbucketConnections)
            .WithName("ListBitbucketConnections")
            .WithSummary("FR-134: the caller's Bitbucket connections, newest first (login, account uuid, auth kind, scopes, status; never a token or an API token's email).")
            .Produces<IReadOnlyList<BitbucketConnectionDto>>();

        group.MapPost("/", CreateBitbucketTokenConnection)
            .WithName("CreateBitbucketTokenConnection")
            .WithSummary("FR-134 token fallback: save an Atlassian API token with the Bitbucket scopes read:user, read:workspace, read:project, read:repository, write:repository, admin:repository, read:pullrequest, write:pullrequest and read:pipeline, with its account email (Basic email:token). Checked with GET /2.0/user and a harmless call per scope first; nothing is saved on failure. App passwords aren't supported. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<BitbucketConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION; CREDENTIAL_INVALID (Bitbucket rejected the token and email); CONNECTION_SCOPE_INSUFFICIENT (missing scopes in details, scopes AIDE couldn't confirm in unverifiableScopes; both in the title). A token is saved only when all nine scopes are verified"),
                (StatusCodes.Status409Conflict, "CONFLICT (connected concurrently; retry)"),
                (StatusCodes.Status429TooManyRequests, "UPSTREAM_RATE_LIMITED (Bitbucket's 429, or the caller's budget of 900 Bitbucket requests an hour for token checks; Retry-After when known)"),
                (StatusCodes.Status502BadGateway, Upstream502));

        group.MapDelete("/{id:guid}", DeleteBitbucketConnection)
            .WithName("DeleteBitbucketConnection")
            .WithSummary("FR-134, FR-136: delete one of the caller's Bitbucket connections (its tokens). Projects bound to it lose the binding until an Owner re-binds. Sign-in with Bitbucket keeps working and reconnects. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", TestBitbucketConnection)
            .WithName("TestBitbucketConnection")
            .WithSummary("FR-134: check the connection with Bitbucket again (refreshing an OAuth token first when due; re-checking an API token's scopes) and record valid, invalid or insufficient_scope. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<BitbucketConnectionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502 + "; the status is unchanged"));
    }

    private static void MapGit(IEndpointRouteBuilder app)
    {
        var git = app.MapGroup("/api/me/git/bitbucket")
            .WithTags("Bitbucket")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        git.MapGet("/repos", ListBitbucketRepos)
            .WithName("ListBitbucketRepos")
            .WithSummary("FR-135: the Bitbucket repositories the caller can write to (role=contributor), across their workspaces one workspace at a time, searchable by name, 30 per page most recently updated first; pass nextCursor (workspace:page) as cursor for the next page. The workspace list is reused for five minutes.")
            .Produces<BitbucketRepoPageDto>()
            .ProducesValidationProblem()
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's Bitbucket connections)"),
                (StatusCodes.Status409Conflict, NotConnected409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        git.MapGet("/owners", ListBitbucketOwners)
            .WithName("ListBitbucketOwners")
            .WithSummary("FR-135: where the caller can create a repository: their first 50 workspaces, each with its first 100 projects (Bitbucket requires a project); truncated and projectsTruncated say more exist.")
            .Produces<BitbucketOwnersDto>()
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's Bitbucket connections)"),
                (StatusCodes.Status409Conflict, NotConnected409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        git.MapPost("/repos", CreateBitbucketRepo)
            .WithName("CreateBitbucketRepo")
            .WithSummary("FR-135: create a Bitbucket repository in a workspace (owner = its slug) and project (its key), then push an initial commit with a README to main, so the default branch exists. The slug is Bitbucket's slug of the name.")
            .Produces<BitbucketRepoDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (Bitbucket doesn't let the caller create repositories there)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's connections, or no such workspace or project)"),
                (StatusCodes.Status409Conflict, "REPO_NAME_TAKEN, or " + NotConnected409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502 + ", or UPSTREAM_ERROR after the repository was created: its README commit couldn't be made or pushed (git failed or is missing, or the server couldn't write the temp folder); the empty repository stays"));
    }

    // Admin

    private static async Task<Ok<AdminBitbucketIntegrationDto>> GetAdminBitbucket(BitbucketAdmin admin, CancellationToken cancellationToken) =>
        TypedResults.Ok(await admin.GetAsync(cancellationToken));

    private static async Task<Results<Ok<AdminBitbucketIntegrationDto>, ValidationProblem, ProblemHttpResult>> UpdateAdminBitbucket(
        UpdateBitbucketConsumerRequest request, BitbucketAdmin admin, CancellationToken cancellationToken)
    {
        var result = await admin.UpdateAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAdminBitbucket(BitbucketAdmin admin, CancellationToken cancellationToken)
    {
        var result = await admin.DeleteAsync(cancellationToken);
        return result.IsFailure ? ApiProblems.FromError(result.Error!) : TypedResults.NoContent();
    }

    // Auth

    private static async Task<Results<RedirectHttpResult, ValidationProblem, ProblemHttpResult>> StartBitbucketOAuth(
        HttpContext http,
        BitbucketSignIn signIn,
        [WireEnum(typeof(GitOAuthMode))] string? mode,
        [MaxLength(2048)] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var oauthMode = mode is null ? GitOAuthMode.Signin : EnumStrings.Parse<GitOAuthMode>(mode);
        if (oauthMode == GitOAuthMode.Connect)
        {
            if (http.User.GetUserId() is null)
            {
                return ApiProblems.Of(ErrorCodes.Unauthenticated, "Sign in to connect Bitbucket.");
            }

            if (http.User.HasClaim(c => c.Type == AuthConstants.TokenIdClaim))
            {
                return ApiProblems.Forbidden("Connecting Bitbucket needs a browser session.");
            }
        }

        var url = await signIn.StartAsync(http, oauthMode, returnUrl, cancellationToken);
        return url is null
            ? ApiProblems.NotFound("Bitbucket sign-in isn't configured on this server.")
            : TypedResults.Redirect(url);
    }

    private static async Task<RedirectHttpResult> BitbucketOAuthCallback(
        HttpContext http,
        BitbucketSignIn signIn,
        [MaxLength(2000)] string? code,
        [MaxLength(400)] string? state,
        [MaxLength(200)] string? error,
        CancellationToken cancellationToken) =>
        TypedResults.Redirect(await signIn.CallbackAsync(http, code, state, error, cancellationToken));

    private static Results<Ok<BitbucketPendingLinkDto>, ProblemHttpResult> GetBitbucketPendingLink(HttpContext http, BitbucketSignIn signIn) =>
        signIn.PendingLink(http) is { } link
            ? TypedResults.Ok(link)
            : ApiProblems.NotFound("There's no Bitbucket sign-in waiting to be linked.");

    private static async Task<Results<NoContent, ProblemHttpResult>> ConfirmBitbucketLink(
        HttpContext http, BitbucketSignIn signIn, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var result = await signIn.ConfirmLinkAsync(http, currentUser.RequiredUserId, cancellationToken);
        return result.IsFailure ? ApiProblems.FromError(result.Error!) : TypedResults.NoContent();
    }

    private static NoContent CancelBitbucketLink(HttpContext http, BitbucketSignIn signIn)
    {
        signIn.CancelLink(http);
        return TypedResults.NoContent();
    }

    // Connections

    private static async Task<Ok<IReadOnlyList<BitbucketConnectionDto>>> ListBitbucketConnections(
        ICurrentUser currentUser, BitbucketConnectionService connections, CancellationToken cancellationToken) =>
        TypedResults.Ok(await connections.ListAsync(currentUser.RequiredUserId, cancellationToken));

    private static async Task<Results<Created<BitbucketConnectionDto>, ValidationProblem, ProblemHttpResult>> CreateBitbucketTokenConnection(
        CreateBitbucketTokenConnectionRequest request,
        ICurrentUser currentUser,
        BitbucketConnectionService connections,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await connections.CreateFromTokenAsync(currentUser.RequiredUserId, request.Token, request.Email, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created($"/api/me/connections/bitbucket/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteBitbucketConnection(
        Guid id, ICurrentUser currentUser, BitbucketConnectionService connections, CancellationToken cancellationToken) =>
        await connections.DeleteAsync(currentUser.RequiredUserId, id, cancellationToken, currentUser.Actor)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound("The Bitbucket connection was not found.");

    private static async Task<Results<Ok<BitbucketConnectionDto>, ProblemHttpResult>> TestBitbucketConnection(
        Guid id, ICurrentUser currentUser, BitbucketConnectionService connections, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await connections.TestAsync(currentUser.RequiredUserId, id, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    // Repos

    private static async Task<Results<Ok<BitbucketRepoPageDto>, ValidationProblem, ProblemHttpResult>> ListBitbucketRepos(
        [Required] Guid? connectionId,
        [MaxLength(BitbucketRules.MaxSearchLength)] string? search,
        [MaxLength(BitbucketRules.MaxCursorLength)] string? cursor,
        ICurrentUser currentUser,
        BitbucketRepoService repos,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await repos.ListReposAsync(currentUser.RequiredUserId, connectionId!.Value, search, cursor, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<BitbucketOwnersDto>, ProblemHttpResult>> ListBitbucketOwners(
        [Required] Guid? connectionId, ICurrentUser currentUser, BitbucketRepoService repos, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await repos.ListOwnersAsync(currentUser.RequiredUserId, connectionId!.Value, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Created<BitbucketRepoDto>, ValidationProblem, ProblemHttpResult>> CreateBitbucketRepo(
        CreateBitbucketRepoRequest request, ICurrentUser currentUser, BitbucketRepoService repos, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await repos.CreateRepoAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created(result.Value.WebUrl ?? $"/api/me/git/bitbucket/repos/{Uri.EscapeDataString(result.Value.Id)}", result.Value);
    }
}

public static class BitbucketServiceCollectionExtensions
{
    /// <summary>FR-133–FR-135: Bitbucket admin, OAuth sign-in/connect, connections and the repo picker.</summary>
    public static IServiceCollection AddBitbucket(this IServiceCollection services)
    {
        services.AddSingleton<BitbucketOAuthStates>();
        services.AddSingleton<BitbucketPendingLinks>();
        services.AddScoped<BitbucketAdmin>();
        services.AddScoped<BitbucketSignIn>();
        services.AddScoped<BitbucketConnectionService>();
        services.AddScoped<BitbucketRepoService>();
        services.AddSingleton<BitbucketWorkspaceCache>();
        return services;
    }
}
