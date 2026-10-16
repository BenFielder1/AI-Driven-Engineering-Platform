using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.GitHub;
using Aide.Infrastructure.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.GitHub;

/// <summary>
/// FR-81, FR-86: the caller's GitHub connection (§6 <c>/api/me/connections/github</c>) and the repo picker
/// (<c>/api/me/github/…</c>), which use the caller's GitHub App user token only (§4.14). Connections are personal; tokens
/// are never returned. Mutating the connection needs a browser session (§6 Notes). Rate limits answer 429
/// <c>GITHUB_RATE_LIMITED</c> (<see cref="GitHubRateLimitedException"/>, mapped by the exception handler).
/// <para>
/// Errors: <c>GITHUB_NOT_CONNECTED</c> 409 (no valid connection), <c>GITHUB_APP_NOT_INSTALLED</c> 409 (with the
/// <c>installUrl</c> extension), <c>REPO_NAME_TAKEN</c> 409, <c>FORBIDDEN</c> 403 (GitHub refused, e.g. no permission to
/// create in an organisation, or no Administration permission), <c>UPSTREAM_ERROR</c> 502 (GitHub failed or unreachable),
/// <c>GITHUB_RATE_LIMITED</c> 429 with <c>Retry-After</c> (seconds until GitHub's rate-limit reset).
/// </para>
/// </summary>
public static class GitHubEndpoints
{
    private const string GitHubErrors409 = "GITHUB_NOT_CONNECTED or GITHUB_APP_NOT_INSTALLED (extension installUrl)";
    private const string RateLimited429 = "GITHUB_RATE_LIMITED (Retry-After: seconds until GitHub's rate-limit reset)";
    private const string Upstream502 = "UPSTREAM_ERROR (GitHub failed or couldn't be reached)";

    public static IEndpointRouteBuilder MapGitHubEndpoints(this IEndpointRouteBuilder app)
    {
        var connection = app.MapGroup("/api/me/connections/github")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        connection.MapGet("/", GetGitHubConnection)
            .WithName("GetGitHubConnection")
            .WithSummary("FR-81: the caller's GitHub connection (login, GitHub user id, status, token expiry; never a token), the App installations they can access and the App's install URL.")
            .Produces<GitHubConnectionDto>();

        connection.MapDelete("/", DisconnectGitHub)
            .WithName("DisconnectGitHub")
            .WithSummary("FR-81: remove the caller's GitHub connection (the stored tokens). Sign-in with GitHub keeps working and reconnects. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        connection.MapGet("/connect", ConnectGitHub)
            .WithName("ConnectGitHub")
            .WithSummary("FR-81: redirects to the AIDE GitHub App's user authorization (PKCE) to connect GitHub to the signed-in account without signing in again, then back to returnUrl (a same-origin path). 404 when no App is registered. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var github = app.MapGroup("/api/me/github")
            .WithTags("GitHub")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        github.MapGet("/repos", ListGitHubRepos)
            .WithName("ListGitHubRepos")
            .WithSummary("FR-81: the repositories the AIDE GitHub App is installed on that the caller can access, searchable by name, 30 per page.")
            .Produces<GitHubRepoPageDto>()
            .ProducesValidationProblem()
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status409Conflict, GitHubErrors409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        github.MapGet("/owners", ListGitHubOwners)
            .WithName("ListGitHubOwners")
            .WithSummary("FR-81: where the caller can create a repository: themselves and their organisations, with whether the App is installed there and whether creating is possible.")
            .Produces<IReadOnlyList<GitHubOwnerDto>>()
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status409Conflict, "GITHUB_NOT_CONNECTED"),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        github.MapGet("/repos/{owner}/{repo}/refs", GetGitHubRepoRefs)
            .WithName("GetGitHubRepoRefs")
            .WithSummary("FR-73, FR-86: a picked repository's default branch, whether it is empty, its releases and its tags (with their semantic versions).")
            .Produces<GitHubRepoRefsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status404NotFound, "NOT_FOUND (no such repository, or the caller can't access it)"),
                (StatusCodes.Status409Conflict, GitHubErrors409),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        github.MapPost("/repos", CreateGitHubRepo)
            .WithName("CreateGitHubRepo")
            .WithSummary("FR-81: create a repository for the caller or an organisation where the App is installed, initialised with a README (auto_init) so the default branch exists, and check the App can access it. Needs the App's Administration permission (403 FORBIDDEN otherwise).")
            .Produces<GitHubRepoDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN (GitHub refused: no Administration permission, or the caller can't create repositories for that owner)"),
                (StatusCodes.Status409Conflict, "REPO_NAME_TAKEN, GITHUB_APP_NOT_INSTALLED (extension installUrl) or GITHUB_NOT_CONNECTED"),
                (StatusCodes.Status429TooManyRequests, RateLimited429),
                (StatusCodes.Status502BadGateway, Upstream502));

        return app;
    }

    private static async Task<Results<Ok<GitHubConnectionDto>, ProblemHttpResult>> GetGitHubConnection(
        ICurrentUser currentUser, GitHubRepoService repos, IGitHubAppStore apps, CancellationToken cancellationToken)
    {
        var connection = await repos.ConnectionAsync(currentUser.RequiredUserId, cancellationToken);
        return connection.IsSuccess ? TypedResults.Ok(connection.Value) : await GitHubResults.ProblemAsync(connection.Error!, apps, cancellationToken);
    }

    private static async Task<NoContent> DisconnectGitHub(ICurrentUser currentUser, GitHubConnections connections, CancellationToken cancellationToken)
    {
        await connections.DisconnectAsync(currentUser.RequiredUserId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<ChallengeHttpResult, ProblemHttpResult>> ConnectGitHub(
        [MaxLength(ProjectLimits.UrlMaxLength)] string? returnUrl,
        ICurrentUser currentUser,
        IGitHubAppStore apps,
        CancellationToken cancellationToken)
    {
        if (await apps.GetAsync(cancellationToken) is null)
        {
            return ApiProblems.NotFound("No AIDE GitHub App is registered.");
        }

        // FR-81: the App's user authorization (PKCE) links GitHub to the signed-in user and stores the connection.
        var properties = new AuthenticationProperties { RedirectUri = ReturnUrls.Sanitize(returnUrl) };
        properties.Items[GitHubAuth.ModeItem] = GitHubAuth.ConnectMode;
        properties.Items[GitHubAuth.LinkUserItem] = currentUser.RequiredUserId.ToString();
        return TypedResults.Challenge(properties, [AuthConstants.GitHubScheme]);
    }

    private static async Task<Results<Ok<GitHubRepoPageDto>, ValidationProblem, ProblemHttpResult>> ListGitHubRepos(
        ICurrentUser currentUser,
        GitHubRepoService repos,
        IGitHubAppStore apps,
        CancellationToken cancellationToken,
        [MaxLength(ProjectLimits.SearchMaxLength)] string? search = null,
        [Range(1, 1000)] int page = 1)
    {
        var listed = await repos.ListReposAsync(currentUser.RequiredUserId, search, page, cancellationToken);
        return listed.IsSuccess ? TypedResults.Ok(listed.Value) : await GitHubResults.ProblemAsync(listed.Error!, apps, cancellationToken);
    }

    private static async Task<Results<Ok<IReadOnlyList<GitHubOwnerDto>>, ProblemHttpResult>> ListGitHubOwners(
        ICurrentUser currentUser, GitHubRepoService repos, IGitHubAppStore apps, CancellationToken cancellationToken)
    {
        var owners = await repos.ListOwnersAsync(currentUser.RequiredUserId, cancellationToken);
        return owners.IsSuccess ? TypedResults.Ok(owners.Value) : await GitHubResults.ProblemAsync(owners.Error!, apps, cancellationToken);
    }

    private static async Task<Results<Ok<GitHubRepoRefsDto>, ValidationProblem, ProblemHttpResult>> GetGitHubRepoRefs(
        [MaxLength(GitHubRules.MaxLoginLength)][RegularExpression(GitHubRules.LoginPattern)] string owner,
        [MaxLength(GitHubRules.MaxRepoNameLength)][RegularExpression(GitHubRules.RepoNamePattern)] string repo,
        ICurrentUser currentUser,
        GitHubRepoService repos,
        IGitHubAppStore apps,
        CancellationToken cancellationToken)
    {
        var refs = await repos.RefsAsync(currentUser.RequiredUserId, owner, repo, cancellationToken);
        return refs.IsSuccess ? TypedResults.Ok(refs.Value) : await GitHubResults.ProblemAsync(refs.Error!, apps, cancellationToken);
    }

    private static async Task<Results<Created<GitHubRepoDto>, ValidationProblem, ProblemHttpResult>> CreateGitHubRepo(
        CreateGitHubRepoRequest request,
        ICurrentUser currentUser,
        GitHubRepoService repos,
        IGitHubAppStore apps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = await repos.CreateRepoAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!.Code == ErrorCodes.Validation
                ? ApiProblems.Validation(created.Error)
                : await GitHubResults.ProblemAsync(created.Error, apps, cancellationToken);
        }

        return TypedResults.Created(created.Value.HtmlUrl, created.Value);
    }
}

/// <summary>ProblemDetails for GitHub errors: <c>GITHUB_APP_NOT_INSTALLED</c> carries the App's install link (FR-81).</summary>
public static class GitHubResults
{
    public static async Task<ProblemHttpResult> ProblemAsync(DomainError error, IGitHubAppStore apps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(apps);
        return error.Code == ErrorCodes.GitHubAppNotInstalled
            ? ApiProblems.GitHubAppNotInstalled((await apps.GetAsync(cancellationToken))?.InstallUrl, error.Message)
            : ApiProblems.FromError(error);
    }
}
