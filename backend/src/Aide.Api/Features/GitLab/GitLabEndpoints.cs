using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// GitLab (FR-130–FR-132, §6): Admin → Integrations → GitLab (<c>/api/admin/integrations/gitlab</c>), sign-in and connect
/// through the instance's OAuth application (<c>/api/auth/gitlab/…</c>), the caller's connections
/// (<c>/api/me/connections/gitlab</c>) and the repo picker and project creation (<c>/api/me/git/gitlab/…</c>). Admin and
/// connection mutations need a browser session (§6 Notes). Tokens and client secrets are never returned.
/// </summary>
public static class GitLabEndpoints
{
    private const string Upstream502 = "UPSTREAM_ERROR (GitLab failed or couldn't be reached)";
    private const string RateLimited429 = "UPSTREAM_RATE_LIMITED (Retry-After when GitLab sent one)";
    private const string NotConnected409 = "GIT_NOT_CONNECTED (the connection is invalid, or its instance is disabled)";

    public static IEndpointRouteBuilder MapGitLabEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdmin(app);
        MapAuth(app);
        MapConnections(app);
        MapGit(app);
        return app;
    }

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/gitlab")
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        admin.MapGet("/", GetAdminGitLab)
            .WithName("GetAdminGitLabIntegration")
            .WithSummary("FR-130: the GitLab instances (gitlab.com first; secrets as flags), the callback URL and scopes to register on each OAuth application, the minimum version and GitLab:AllowPrivateNetworks. Admins only.")
            .Produces<AdminGitLabIntegrationDto>();

        admin.MapGet("/{instanceId:guid}", GetAdminGitLabInstance)
            .WithName("GetAdminGitLabInstance")
            .WithSummary("FR-130: one GitLab instance. Admins only.")
            .Produces<AdminGitLabInstanceDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/", CreateAdminGitLabInstance)
            .WithName("CreateAdminGitLabInstance")
            .WithSummary("FR-130: add a self-managed GitLab instance. The base URL is normalised and checked by the SSRF guard (§8: https only unless allowInsecureHttp; loopback, link-local, metadata and 0.0.0.0 always refused; private ranges only with GitLab:AllowPrivateNetworks) and GET /api/v4/version must answer like GitLab (401 counts as reachable; a version below 17.11 warns). Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<AdminGitLabInstanceDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION, or GIT_HOST_NOT_ALLOWED (the base URL fails the SSRF guard)"),
                (StatusCodes.Status409Conflict, "CONFLICT (the base URL is gitlab.com, already registered, or configured in the environment)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (the host didn't resolve, couldn't be reached, or didn't answer like GitLab)"));

        admin.MapPatch("/{instanceId:guid}", UpdateAdminGitLabInstance)
            .WithName("UpdateAdminGitLabInstance")
            .WithSummary("FR-130: change a GitLab instance (name, base URL, OAuth application, allowInsecureHttp, enabled). A new base URL runs the SSRF guard and the reachability check. Configured instances only accept enabled. Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<AdminGitLabInstanceDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION, or GIT_HOST_NOT_ALLOWED (the base URL fails the SSRF guard)"),
                (StatusCodes.Status409Conflict, "CONFLICT (configured instance, duplicate base URL, or a base URL change while connections or projects use it)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (the new base URL didn't answer like GitLab)"));

        admin.MapPost("/{instanceId:guid}/check", CheckAdminGitLabInstance)
            .WithName("CheckAdminGitLabInstance")
            .WithSummary("FR-130: run the SSRF guard and the reachability check again and record the GitLab version. Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<AdminGitLabInstanceDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "GIT_HOST_NOT_ALLOWED (the host now resolves to a refused address)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR"));

        admin.MapDelete("/{instanceId:guid}", DeleteAdminGitLabInstance)
            .WithName("DeleteAdminGitLabInstance")
            .WithSummary("FR-130: remove a GitLab instance no project uses, with its users' connections and sign-in links. gitlab.com and configured instances can't be removed (disable them). Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions((StatusCodes.Status409Conflict, "CONFLICT (projects use it, or it is gitlab.com or configured)"));
    }

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth/gitlab").WithTags("Auth");

        auth.MapGet("/start", StartGitLabOAuth)
            .WithName("StartGitLabOAuth")
            .WithSummary("FR-131: redirects to the GitLab instance's OAuth authorize page (code + PKCE S256, scopes api write_repository openid profile email, signed single-use state bound to this browser). mode=signin (\"Continue with GitLab\", anonymous) or connect (Account → Connections; signed-in browser session). instanceId omitted = gitlab.com. returnUrl is a same-origin path. 404 when the instance doesn't exist, is disabled, or has no OAuth application.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions(
                (StatusCodes.Status401Unauthorized, "UNAUTHENTICATED (mode=connect without a session)"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (mode=connect with an API token)"));

        auth.MapGet("/callback", GitLabOAuthCallback)
            .WithName("GitLabOAuthCallback")
            .WithSummary("FR-131: GitLab's redirect: checks the state, exchanges the code (with the PKCE verifier), reads the identity, then signs in (account matched by gitlab + instance + user id, or created for a new verified email; the first account becomes Admin), or links to the signed-in user, and stores the connection. Redirects to returnUrl; on failure to /login (sign-in) or returnUrl (connect) with error=GIT_AUTH_FAILED&reason=…&provider=gitlab; when the verified email belongs to an existing unlinked account, to /login?link=gitlab&returnUrl=… (confirm with POST /api/auth/gitlab/link).")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found);

        auth.MapGet("/link", GetGitLabPendingLink)
            .WithName("GetGitLabPendingLink")
            .WithSummary("FR-131: the GitLab sign-in waiting in this browser to be linked to the existing account with the same verified email (10 minutes). 404 when there is none.")
            .AllowAnonymous()
            .Produces<GitLabPendingLinkDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        auth.MapPost("/link", ConfirmGitLabLink)
            .WithName("ConfirmGitLabLink")
            .WithSummary("FR-131: the signed-in owner of the matching account confirms: the GitLab identity is linked (later sign-ins match it) and connected. Never done silently. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN (signed in as a different account than the one with that email)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (no pending link, or it expired)"),
                (StatusCodes.Status409Conflict, "CONFLICT (the GitLab identity was linked to another account meanwhile)"));

        auth.MapDelete("/link", CancelGitLabLink)
            .WithName("CancelGitLabLink")
            .WithSummary("FR-131: discard this browser's pending GitLab link.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapConnections(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/connections/gitlab")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListGitLabConnections)
            .WithName("ListGitLabConnections")
            .WithSummary("FR-131: the caller's GitLab connections, newest first (login, instance, auth kind, scopes, expiry and its 14-day warning, status; never a token).")
            .Produces<IReadOnlyList<GitLabConnectionDto>>();

        group.MapGet("/instances", ListGitLabInstances)
            .WithName("ListGitLabInstances")
            .WithSummary("FR-131: the enabled GitLab instances the caller can connect to (oauthConfigured: OAuth connect offered; otherwise the token fallback only).")
            .Produces<IReadOnlyList<GitLabInstanceSummaryDto>>();

        group.MapPost("/", CreateGitLabTokenConnection)
            .WithName("CreateGitLabTokenConnection")
            .WithSummary("FR-131 token fallback: save a personal, project or group access token with api and write_repository. Checked with GET /api/v4/user and GET /api/v4/personal_access_tokens/self first; nothing is saved on failure. instanceId omitted = gitlab.com. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<GitLabConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION; CREDENTIAL_INVALID (GitLab rejected the token, or it is expired or revoked); CONNECTION_SCOPE_INSUFFICIENT (missing scopes in details and the title); GIT_HOST_NOT_ALLOWED"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (no such enabled instance)"),
                (StatusCodes.Status409Conflict, "CONFLICT (connected concurrently; retry)"),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        group.MapDelete("/{id:guid}", DeleteGitLabConnection)
            .WithName("DeleteGitLabConnection")
            .WithSummary("FR-131, FR-136: delete one of the caller's GitLab connections (its tokens). Projects bound to it lose the binding until an Owner re-binds. Sign-in with GitLab keeps working and reconnects. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", TestGitLabConnection)
            .WithName("TestGitLabConnection")
            .WithSummary("FR-131: check the connection with GitLab again (refreshing an OAuth token first when due) and record valid, invalid or insufficient_scope, scopes and expiry. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<GitLabConnectionDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "GIT_HOST_NOT_ALLOWED"),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502 + "; the status is unchanged"));
    }

    private static void MapGit(IEndpointRouteBuilder app)
    {
        var git = app.MapGroup("/api/me/git/gitlab")
            .WithTags("GitLab")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        git.MapGet("/repos", ListGitLabRepos)
            .WithName("ListGitLabRepos")
            .WithSummary("FR-132: the GitLab projects the caller can develop in (membership, Developer or above), searchable by name, 30 per page newest first; pass nextCursor as cursor for the next page.")
            .Produces<GitLabRepoPageDto>()
            .ProducesValidationProblem()
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION or GIT_HOST_NOT_ALLOWED"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's GitLab connections)"),
                (StatusCodes.Status409Conflict, NotConnected409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        git.MapGet("/owners", ListGitLabOwners)
            .WithName("ListGitLabOwners")
            .WithSummary("FR-132: where the caller can create a project: their personal namespace and the groups where they are Developer or above, with whether the group's project_creation_level allows them.")
            .Produces<IReadOnlyList<GitLabOwnerDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "GIT_HOST_NOT_ALLOWED"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's GitLab connections)"),
                (StatusCodes.Status409Conflict, NotConnected409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        git.MapPost("/repos", CreateGitLabRepo)
            .WithName("CreateGitLabRepo")
            .WithSummary("FR-132: create a GitLab project in the personal namespace or a group (owner = its full path), with initialize_with_readme and default_branch main, so main exists with a README.")
            .Produces<GitLabRepoDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION or GIT_HOST_NOT_ALLOWED"),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (GitLab doesn't let the caller create projects there)"),
                (StatusCodes.Status404NotFound, "NOT_FOUND (not one of the caller's connections, or no such group)"),
                (StatusCodes.Status409Conflict, "REPO_NAME_TAKEN, or " + NotConnected409),
                (StatusCodes.Status429TooManyRequests, "UPSTREAM_RATE_LIMITED (the GitLab.com project-creation limit; Retry-After when sent)"),
                (StatusCodes.Status502BadGateway, Upstream502));
    }

    // Admin

    private static async Task<Ok<AdminGitLabIntegrationDto>> GetAdminGitLab(GitLabAdmin admin, CancellationToken cancellationToken) =>
        TypedResults.Ok(await admin.OverviewAsync(cancellationToken));

    private static async Task<Results<Ok<AdminGitLabInstanceDto>, ProblemHttpResult>> GetAdminGitLabInstance(Guid instanceId, GitLabAdmin admin, CancellationToken cancellationToken) =>
        await admin.GetAsync(instanceId, cancellationToken) is { } dto
            ? TypedResults.Ok(dto)
            : ApiProblems.NotFound("The GitLab instance was not found.");

    private static async Task<Results<Created<AdminGitLabInstanceDto>, ValidationProblem, ProblemHttpResult>> CreateAdminGitLabInstance(
        CreateGitLabInstanceRequest request, GitLabAdmin admin, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var result = await admin.CreateAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created($"/api/admin/integrations/gitlab/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<AdminGitLabInstanceDto>, ValidationProblem, ProblemHttpResult>> UpdateAdminGitLabInstance(
        Guid instanceId, UpdateGitLabInstanceRequest request, GitLabAdmin admin, CancellationToken cancellationToken)
    {
        var result = await admin.UpdateAsync(instanceId, request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<AdminGitLabInstanceDto>, ValidationProblem, ProblemHttpResult>> CheckAdminGitLabInstance(
        Guid instanceId, GitLabAdmin admin, CancellationToken cancellationToken)
    {
        var result = await admin.CheckAsync(instanceId, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAdminGitLabInstance(Guid instanceId, GitLabAdmin admin, CancellationToken cancellationToken)
    {
        var result = await admin.DeleteAsync(instanceId, cancellationToken);
        return result.IsFailure ? ApiProblems.FromError(result.Error!) : TypedResults.NoContent();
    }

    // Auth

    private static async Task<Results<RedirectHttpResult, ValidationProblem, ProblemHttpResult>> StartGitLabOAuth(
        HttpContext http,
        GitLabSignIn signIn,
        Guid? instanceId,
        [WireEnum(typeof(GitOAuthMode))] string? mode,
        [MaxLength(2048)] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var oauthMode = mode is null ? GitOAuthMode.Signin : EnumStrings.Parse<GitOAuthMode>(mode);
        if (oauthMode == GitOAuthMode.Connect)
        {
            if (http.User.GetUserId() is null)
            {
                return ApiProblems.Of(ErrorCodes.Unauthenticated, "Sign in to connect GitLab.");
            }

            if (http.User.HasClaim(c => c.Type == AuthConstants.TokenIdClaim))
            {
                return ApiProblems.Forbidden("Connecting GitLab needs a browser session.");
            }
        }

        var url = await signIn.StartAsync(http, instanceId, oauthMode, returnUrl, cancellationToken);
        return url is null
            ? ApiProblems.NotFound("GitLab sign-in isn't configured for that instance.")
            : TypedResults.Redirect(url);
    }

    private static async Task<RedirectHttpResult> GitLabOAuthCallback(
        HttpContext http,
        GitLabSignIn signIn,
        [MaxLength(2000)] string? code,
        [MaxLength(400)] string? state,
        [MaxLength(200)] string? error,
        CancellationToken cancellationToken) =>
        TypedResults.Redirect(await signIn.CallbackAsync(http, code, state, error, cancellationToken));

    private static Results<Ok<GitLabPendingLinkDto>, ProblemHttpResult> GetGitLabPendingLink(HttpContext http, GitLabSignIn signIn) =>
        signIn.PendingLink(http) is { } link
            ? TypedResults.Ok(link)
            : ApiProblems.NotFound("There's no GitLab sign-in waiting to be linked.");

    private static async Task<Results<NoContent, ProblemHttpResult>> ConfirmGitLabLink(
        HttpContext http, GitLabSignIn signIn, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var result = await signIn.ConfirmLinkAsync(http, currentUser.RequiredUserId, cancellationToken);
        return result.IsFailure ? ApiProblems.FromError(result.Error!) : TypedResults.NoContent();
    }

    private static NoContent CancelGitLabLink(HttpContext http, GitLabSignIn signIn)
    {
        signIn.CancelLink(http);
        return TypedResults.NoContent();
    }

    // Connections

    private static async Task<Ok<IReadOnlyList<GitLabConnectionDto>>> ListGitLabConnections(
        ICurrentUser currentUser, GitLabConnectionService connections, CancellationToken cancellationToken) =>
        TypedResults.Ok(await connections.ListAsync(currentUser.RequiredUserId, cancellationToken));

    private static async Task<Ok<IReadOnlyList<GitLabInstanceSummaryDto>>> ListGitLabInstances(IGitLabInstances instances, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<GitLabInstanceSummaryDto>>([.. (await instances.ListAsync(cancellationToken)).Where(i => i.Enabled).Select(ToSummary)]);

    private static async Task<Results<Created<GitLabConnectionDto>, ValidationProblem, ProblemHttpResult>> CreateGitLabTokenConnection(
        CreateGitLabTokenConnectionRequest request,
        ICurrentUser currentUser,
        GitLabConnectionService connections,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await connections.CreateFromTokenAsync(currentUser.RequiredUserId, request.InstanceId, request.Token, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created($"/api/me/connections/gitlab/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteGitLabConnection(
        Guid id, ICurrentUser currentUser, GitLabConnectionService connections, CancellationToken cancellationToken) =>
        await connections.DeleteAsync(currentUser.RequiredUserId, id, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound("The GitLab connection was not found.");

    private static async Task<Results<Ok<GitLabConnectionDto>, ProblemHttpResult>> TestGitLabConnection(
        Guid id, ICurrentUser currentUser, GitLabConnectionService connections, HttpContext http, CancellationToken cancellationToken)
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

    private static async Task<Results<Ok<GitLabRepoPageDto>, ValidationProblem, ProblemHttpResult>> ListGitLabRepos(
        [Required] Guid? connectionId,
        [MaxLength(Domain.GitHosts.GitLabRules.MaxSearchLength)] string? search,
        [MaxLength(Domain.GitHosts.GitLabRules.MaxCursorLength)] string? cursor,
        ICurrentUser currentUser,
        GitLabRepoService repos,
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

    private static async Task<Results<Ok<IReadOnlyList<GitLabOwnerDto>>, ProblemHttpResult>> ListGitLabOwners(
        [Required] Guid? connectionId, ICurrentUser currentUser, GitLabRepoService repos, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await repos.ListOwnersAsync(currentUser.RequiredUserId, connectionId!.Value, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return ApiProblems.FromError(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Created<GitLabRepoDto>, ValidationProblem, ProblemHttpResult>> CreateGitLabRepo(
        CreateGitLabRepoRequest request, ICurrentUser currentUser, GitLabRepoService repos, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await repos.CreateRepoAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (result.IsFailure)
        {
            ResponseDocs.SetRetryAfter(http, result.Error!.RetryAfter);
            return result.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(result.Error) : ApiProblems.FromError(result.Error);
        }

        return TypedResults.Created(result.Value.WebUrl ?? $"/api/me/git/gitlab/repos/{result.Value.Id}", result.Value);
    }

    /// <summary>The enabled instances offered on the sign-in page ("Continue with GitLab" needs an OAuth application).</summary>
    public static IReadOnlyList<GitLabInstanceSummaryDto> SignInInstances(IEnumerable<GitLabInstance> instances) =>
        [.. instances.Where(i => i.Enabled && i.OAuthConfigured).Select(ToSummary)];

    private static GitLabInstanceSummaryDto ToSummary(GitLabInstance i) =>
        new(i.Id, i.Name, i.BaseUrl.AbsoluteUri.TrimEnd('/'), i.IsGitLabCom, i.OAuthConfigured);

}

public static class GitLabServiceCollectionExtensions
{
    /// <summary>FR-130–FR-132: GitLab admin, OAuth sign-in/connect, connections and the repo picker.</summary>
    public static IServiceCollection AddGitLab(this IServiceCollection services)
    {
        services.AddSingleton<GitLabOAuthStates>();
        services.AddSingleton<GitLabPendingLinks>();
        services.AddScoped<GitLabAdmin>();
        services.AddScoped<GitLabSignIn>();
        services.AddScoped<GitLabConnectionService>();
        services.AddScoped<GitLabRepoService>();
        return services;
    }
}
