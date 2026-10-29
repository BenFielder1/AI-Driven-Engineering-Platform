using System.Security.Claims;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Common;
using Aide.Domain.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Auth;

/// <summary>FR-1 (register, sign in/out, me), FR-2 (GitHub), antiforgery token and provider discovery (§6 auth rows).</summary>
public static partial class AuthEndpoints
{
    private const string LoggerCategory = "Aide.Api.Features.Auth";

    private static string? _dummyPasswordHash;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", Register)
            .WithName("Register")
            .WithSummary("Create an account (the first account becomes Admin) and sign in. Rate limited per IP.")
            .AllowAnonymous()
            .SkipAntiforgery()
            .RequireRateLimiting(AuthConstants.RegisterRateLimit)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/login", Login)
            .WithName("Login")
            .WithSummary("Sign in with email and password (sets the auth cookie and refreshes XSRF-TOKEN). Rate limited per IP.")
            .AllowAnonymous()
            .SkipAntiforgery()
            .RequireRateLimiting(AuthConstants.LoginRateLimit)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", Logout)
            .WithName("Logout")
            .WithSummary("Sign out (clears the auth cookie). Requires X-XSRF-TOKEN when signed in.")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/me", GetCurrentUser)
            .WithName("GetCurrentUser")
            .WithSummary("The signed-in user with their system role.")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/providers", GetProviders)
            .WithName("GetAuthProviders")
            .WithSummary("Which sign-in providers are configured, how GitHub sign-in works (githubMode: app, oauth or none; D-V2-7), and the GitLab instances offering \"Continue with GitLab\" (FR-131).")
            .AllowAnonymous();

        group.MapGet("/antiforgery", GetAntiforgeryToken)
            .WithName("GetAntiforgeryToken")
            .WithSummary("Issues the XSRF-TOKEN cookie; send its value as X-XSRF-TOKEN on cookie-authenticated mutations.")
            .AllowAnonymous();

        group.MapGet("/github", StartGitHubSignIn)
            .WithName("StartGitHubSignIn")
            .WithSummary("Redirects to GitHub to sign in (FR-2). Once an AIDE GitHub App exists this uses its user authorization flow with PKCE, which also connects GitHub (FR-80, FR-81, D-V2-7). 404 when GitHub sign-in isn't configured.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/github/link", StartGitHubLink)
            .WithName("StartGitHubLink")
            .WithSummary("Redirects to GitHub to link it to the signed-in account (FR-2). 404 when not configured.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/github/callback", GitHubCallback)
            .WithName("GitHubCallback")
            .WithSummary("GitHub OAuth callback, handled by the GitHub handler: redirects to returnUrl, or to " +
                "/login?error=GITHUB_AUTH_FAILED&reason=… on failure. 404 when GitHub sign-in isn't configured.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Created<UserDto>, ValidationProblem, ProblemHttpResult>> Register(
        RegisterRequest request, AccountService accounts, HttpContext httpContext, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var user = new AppUser { Email = email, UserName = email, Name = request.Name.Trim() };
        var result = await accounts.CreateAsync(user, request.Password, cancellationToken);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                return ApiProblems.Of(ErrorCodes.EmailTaken, "An account with this email already exists.");
            }

            return ApiProblems.Validation(result.Errors
                .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        var logger = loggers.CreateLogger(LoggerCategory);
        LogRegistered(logger, user.Id, user.SystemRole);
        await accounts.SignInAsync(httpContext, user);
        return TypedResults.Created("/api/auth/me", UserDto.From(user));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Login(
        LoginRequest request,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AccountService accounts,
        HttpContext httpContext,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(LoggerCategory);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Same work as a real check, so response timing doesn't reveal whether the email exists.
            _dummyPasswordHash ??= users.PasswordHasher.HashPassword(new AppUser(), Guid.NewGuid().ToString());
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), _dummyPasswordHash, request.Password);
            LogLoginFailed(logger, "unknown_email");
            return InvalidCredentials();
        }

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            LogLoginRejected(logger, user.Id, result.IsLockedOut ? "locked_out" : "wrong_password");
            return InvalidCredentials();
        }

        await accounts.SignInAsync(httpContext, user);
        LogSignedIn(logger, user.Id);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Logout(AccountService accounts, HttpContext httpContext)
    {
        await accounts.SignOutAsync(httpContext);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> GetCurrentUser(
        ClaimsPrincipal principal, AideDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null
            ? ApiProblems.Of(ErrorCodes.Unauthenticated, "Sign in to continue.")
            : TypedResults.Ok(UserDto.From(user));
    }

    private static async Task<Ok<AuthProvidersDto>> GetProviders(
        GitHubSignInResolver github,
        Infrastructure.GitLab.IGitLabInstances gitlab,
        Infrastructure.Bitbucket.IBitbucketConsumer bitbucket,
        CancellationToken cancellationToken)
    {
        var mode = await github.ModeAsync(cancellationToken);
        var gitlabInstances = GitLab.GitLabEndpoints.SignInInstances(await gitlab.ListAsync(cancellationToken));
        var bitbucketConsumer = await bitbucket.GetAsync(cancellationToken);
        return TypedResults.Ok(new AuthProvidersDto(mode != GitHubSignInMode.None, mode, gitlabInstances, bitbucketConsumer.OAuthConfigured));
    }

    private static NoContent GetAntiforgeryToken(HttpContext httpContext)
    {
        AntiforgeryProtection.IssueTokens(httpContext);
        return TypedResults.NoContent();
    }

    private static async Task<Results<ChallengeHttpResult, ProblemHttpResult>> StartGitHubSignIn(
        string? returnUrl, GitHubSignInResolver github, CancellationToken cancellationToken)
    {
        if (await github.ModeAsync(cancellationToken) == GitHubSignInMode.None)
        {
            return GitHubNotConfigured();
        }

        return TypedResults.Challenge(
            new AuthenticationProperties { RedirectUri = ReturnUrls.Sanitize(returnUrl) },
            [AuthConstants.GitHubScheme]);
    }

    private static async Task<Results<ChallengeHttpResult, ProblemHttpResult>> StartGitHubLink(
        string? returnUrl, GitHubSignInResolver github, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (await github.ModeAsync(cancellationToken) == GitHubSignInMode.None)
        {
            return GitHubNotConfigured();
        }

        var properties = new AuthenticationProperties { RedirectUri = ReturnUrls.Sanitize(returnUrl) };
        properties.Items[GitHubAuth.ModeItem] = GitHubAuth.LinkMode;
        properties.Items[GitHubAuth.LinkUserItem] = principal.GetUserId()?.ToString();
        return TypedResults.Challenge(properties, [AuthConstants.GitHubScheme]);
    }

    /// <summary>Only reached when GitHub isn't configured: otherwise the OAuth handler answers the callback path.</summary>
    private static ProblemHttpResult GitHubCallback() => GitHubNotConfigured();

    private static ProblemHttpResult GitHubNotConfigured() => ApiProblems.NotFound("GitHub sign-in is not configured.");

    private static ProblemHttpResult InvalidCredentials() =>
        ApiProblems.Of(ErrorCodes.InvalidCredentials, "The email or password is incorrect.");

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} registered with role {SystemRole}")]
    private static partial void LogRegistered(ILogger logger, Guid userId, Domain.Access.SystemRole systemRole);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in")]
    private static partial void LogSignedIn(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in failed ({Reason})")]
    private static partial void LogLoginFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in failed for user {UserId} ({Reason})")]
    private static partial void LogLoginRejected(ILogger logger, Guid userId, string reason);
}
