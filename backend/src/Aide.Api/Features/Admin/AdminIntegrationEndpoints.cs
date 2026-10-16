using Aide.Api.Auth;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Llm;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aide.Api.Features.Admin;

/// <summary>
/// §2 Admin: the system LLM key policy (D-V2-5). <c>allowSystemKeys</c> is <c>Llm:AllowSystemKeys</c> (configuration,
/// read-only here: set <c>Llm__AllowSystemKeys</c> in <c>secrets.env</c> / user-secrets and restart). When it is on,
/// projects with no bound connection use the system key of their provider; <c>systemKeyProviders</c> lists the
/// providers that have one configured (allowed or not). Keys are never returned.
/// </summary>
public sealed record AdminLlmPolicyDto(bool AllowSystemKeys, IReadOnlyList<LlmProvider> SystemKeyProviders);

/// <summary>Admin → Integrations (§6 <c>/api/admin/integrations/…</c>). The GitHub App part arrives with FR-80.</summary>
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

        return app;
    }

    private static Ok<AdminLlmPolicyDto> GetLlmPolicy(ILlmProviderCatalog catalog) =>
        TypedResults.Ok(new AdminLlmPolicyDto(
            catalog.AllowSystemKeys,
            [.. Enum.GetValues<LlmProvider>().Where(p => ProjectSettingsRules.RequiresApiKey(p) && catalog.IsSystemKeySet(p))]));
}
