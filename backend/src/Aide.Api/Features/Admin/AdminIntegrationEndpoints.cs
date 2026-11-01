using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Llm;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Admin;

/// <summary>
/// §2 Admin: the system LLM key policy (D-V2-5). <c>allowSystemKeys</c> is <c>Llm:AllowSystemKeys</c> (configuration,
/// read-only here: set <c>Llm__AllowSystemKeys</c> in <c>secrets.env</c> / user-secrets and restart). When it is on,
/// projects with no bound connection use the system key of their provider; <c>systemKeyProviders</c> lists the
/// providers that have one configured (allowed or not). Keys are never returned. Providers disabled on this deployment
/// (FR-157) are never listed.
/// </summary>
public sealed record AdminLlmPolicyDto(bool AllowSystemKeys, IReadOnlyList<LlmProvider> SystemKeyProviders);

/// <summary>
/// Admin → Integrations (§6 <c>/api/admin/integrations/…</c>): the system LLM key policy and the AIDE GitHub App (FR-80).
/// </summary>
public static class AdminIntegrationEndpoints
{
    public static IEndpointRouteBuilder MapAdminIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/integrations")
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/llm", GetLlmPolicy)
            .WithName("GetAdminLlmPolicy")
            .WithSummary("Whether system LLM keys may be used (Llm:AllowSystemKeys, read-only configuration) and which providers have one configured. Admins only.");

        group.MapGet("/github", GetGitHubApp)
            .WithName("GetAdminGitHubApp")
            .WithSummary("FR-80: the AIDE GitHub App's status (not_configured or configured, from the database or GitHub__App__* configuration), slug, owner, install URL, granted permissions and whether repositories can be created. Admins only; secrets are never returned.")
            .Produces<AdminGitHubAppDto>();

        group.MapPost("/github/manifest", CreateGitHubAppManifest)
            .WithName("CreateGitHubAppManifest")
            .WithSummary("FR-80: start the manifest flow: returns the App manifest (§4.14) and the GitHub URL to post it to, with a single-use state. 409 CONFLICT when an App is already configured. Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<GitHubAppManifestResponseDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions((StatusCodes.Status409Conflict, "CONFLICT (an App is already configured)"));

        group.MapGet("/github/callback", CompleteGitHubAppManifest)
            .WithName("CompleteGitHubAppManifest")
            .WithSummary("FR-80: GitHub's redirect after the Admin creates the App: checks state, exchanges the code (POST /app-manifests/{code}/conversions) and stores the App with its secrets encrypted, then redirects to /admin/integrations?github=configured, or to /admin/integrations?error=GITHUB_AUTH_FAILED on failure. Admins with a browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status302Found)
            .ProducesValidationProblem();

        return app;
    }

    private static Ok<AdminLlmPolicyDto> GetLlmPolicy(ILlmProviderCatalog catalog) =>
        TypedResults.Ok(new AdminLlmPolicyDto(
            catalog.AllowSystemKeys,
            [.. catalog.EnabledProviders.Where(p => ProjectSettingsRules.RequiresApiKey(p) && catalog.IsSystemKeySet(p))]));

    private static async Task<Ok<AdminGitHubAppDto>> GetGitHubApp(GitHubAppAdmin admin, CancellationToken cancellationToken) =>
        TypedResults.Ok(await admin.StatusAsync(cancellationToken));

    private static async Task<Results<Ok<GitHubAppManifestResponseDto>, ValidationProblem, ProblemHttpResult>> CreateGitHubAppManifest(
        CreateGitHubAppManifestRequest request, GitHubAppAdmin admin, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifest = await admin.ManifestAsync(currentUser.RequiredUserId, request, cancellationToken);
        if (manifest.IsFailure)
        {
            return manifest.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(manifest.Error) : ApiProblems.FromError(manifest.Error);
        }

        return TypedResults.Ok(manifest.Value);
    }

    private static async Task<RedirectHttpResult> CompleteGitHubAppManifest(
        [MaxLength(AdminGitHubLimits.CodeMaxLength)] string? code,
        [MaxLength(AdminGitHubLimits.StateMaxLength)] string? state,
        GitHubAppAdmin admin,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        TypedResults.Redirect(await admin.CompleteAsync(currentUser.RequiredUserId, code, state, cancellationToken));
}
