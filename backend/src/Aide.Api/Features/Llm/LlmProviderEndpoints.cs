using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Llm;

/// <summary>One of the caller's LLM connections for a provider (FR-78, FR-158): never the key, only its last 4 characters.</summary>
public sealed record LlmProviderConnectionDto(Guid Id, string Label, string Last4);

/// <summary>A model of a provider's catalog (FR-158, §4.24): id, display name, max output tokens per call, context window and
/// whether it's recommended.</summary>
public sealed record LlmModelDto(string Id, string Name, int MaxOutputTokens, int ContextTokens, bool Recommended);

/// <summary>
/// An LLM provider enabled on this deployment, as seen by the caller (FR-158). <c>usable</c> and <c>reason</c>
/// (<c>system_key</c>, <c>own_connection</c>, <c>keyless</c>, <c>needs_connection</c>) say whether the caller can choose it
/// now; <c>connections</c> are the caller's own connections for it (newest first); <c>catalog</c> is its model catalog and
/// <c>defaultModel</c> the system default model (<c>Llm:Providers:&lt;P&gt;:DefaultModel</c>). <c>isDefault</c> (FR-159) is true
/// on the one provider a project created now by the caller without an LLM choice would get (§4.24
/// <c>DefaultForNewProject</c>), false on all others (all false when that rule yields <c>LLM_PROVIDER_UNAVAILABLE</c>).
/// </summary>
public sealed record LlmProviderDto(
    LlmProvider Provider,
    bool Usable,
    LlmProviderReason Reason,
    string DefaultModel,
    IReadOnlyList<LlmProviderConnectionDto> Connections,
    IReadOnlyList<LlmModelDto> Catalog,
    bool IsDefault);

/// <summary>FR-158: <c>GET /api/llm/providers</c>, the wizard's source for the AI model section (FR-159).</summary>
public static class LlmProviderEndpoints
{
    public static IEndpointRouteBuilder MapLlmProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/llm")
            .WithTags("LLM")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/providers", ListLlmProviders)
            .WithName("ListLlmProviders")
            .WithSummary("FR-158: the LLM providers enabled on this server (disabled ones are never listed, FR-157), in the order anthropic, openai, ollama, fake, each with whether the caller can use it and why, the caller's connections for it (no keys), its model catalog (max output tokens, context window, recommended), its default model, and whether it is the provider a new project would default to for the caller (isDefault, FR-159).")
            .Produces<IReadOnlyList<LlmProviderDto>>();

        return app;
    }

    private static async Task<Ok<IReadOnlyList<LlmProviderDto>>> ListLlmProviders(
        ICurrentUser currentUser,
        AideDbContext db,
        ILlmProviderCatalog catalog,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        var connections = await db.LlmCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => new { c.Id, c.Provider, c.Label, c.Last4, c.Status })
            .ToListAsync(cancellationToken);

        // FR-159: a connection whose last check failed (invalid) is still listed, but makes nothing usable or default.
        var valid = connections.Where(c => c.Status != LlmCredentialStatus.Invalid).ToList();
        var chosen = catalog.DefaultForNewProject([.. valid.Select(c => c.Provider)]);
        LlmProvider? defaultProvider = chosen.IsSuccess ? chosen.Value : null;

        var providers = new List<LlmProviderDto>();
        foreach (var provider in catalog.EnabledProviders)
        {
            var own = connections.Where(c => c.Provider == provider).Select(c => new LlmProviderConnectionDto(c.Id, c.Label, c.Last4)).ToList();
            if (catalog.Availability(provider, valid.Count(c => c.Provider == provider)) is not { } availability)
            {
                continue;
            }

            providers.Add(new LlmProviderDto(
                provider,
                availability.Usable,
                availability.Reason,
                catalog.DefaultModel(provider),
                own,
                [.. catalog.Catalog(provider).Select(m => new LlmModelDto(m.Id, m.Name, m.MaxOutputTokens, m.ContextTokens, m.Recommended))],
                provider == defaultProvider));
        }

        return TypedResults.Ok<IReadOnlyList<LlmProviderDto>>(providers);
    }
}
