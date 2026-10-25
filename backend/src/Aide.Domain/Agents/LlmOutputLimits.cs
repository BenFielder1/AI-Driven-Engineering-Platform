using Aide.Domain.Entities;

namespace Aide.Domain.Agents;

/// <summary>
/// One model of a provider's catalog (FR-158, §4.24, <c>Llm:Providers:&lt;P&gt;:Catalog:&lt;n&gt;</c>): its id, display name,
/// maximum output tokens per call, context window and whether it's the recommended choice.
/// </summary>
public sealed record LlmCatalogModel(string Id, string Name, int MaxOutputTokens, int ContextTokens, bool Recommended);

/// <summary>FR-160, §4.24: the effective output token limit of one LLM call.</summary>
public static class LlmOutputLimits
{
    /// <summary>
    /// §4.24 <c>LlmOutputLimits.Resolve</c>:
    /// <list type="number">
    /// <item><c>limit</c> = the catalog entry's <c>MaxOutputTokens</c>; else, for Anthropic only, the
    /// <paramref name="discovered"/> <c>max_tokens</c> of <c>GET /v1/models/{id}</c>; else <paramref name="fallback"/>
    /// (<c>Llm:MaxOutputTokens</c>).</item>
    /// <item>With a <paramref name="ceiling"/> (<c>Llm:MaxOutputTokensCeiling</c>, ≥ 1), <c>limit = min(limit, ceiling)</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="provider">The run's provider.</param>
    /// <param name="model">The model id of the call (matched case-insensitively, trimmed).</param>
    /// <param name="catalog">The provider's model catalog.</param>
    /// <param name="discovered">The provider-reported limit of an unlisted model (used for Anthropic only; <c>null</c>: not
    /// looked up or the lookup failed).</param>
    /// <param name="ceiling">The optional operator ceiling.</param>
    /// <param name="fallback">The limit for unlisted models.</param>
    public static int Resolve(LlmProvider provider, string model, IReadOnlyList<LlmCatalogModel> catalog, int? discovered, int? ceiling, int fallback)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var limit = Find(catalog, model)?.MaxOutputTokens
            ?? (provider == LlmProvider.Anthropic && discovered is > 0 ? discovered.Value : fallback);
        return ceiling is >= 1 ? Math.Min(limit, ceiling.Value) : limit;
    }

    /// <summary>Whether the limit of <paramref name="model"/> should be asked of the provider: an Anthropic model that
    /// isn't in the catalog (§4.24 step 1).</summary>
    public static bool NeedsDiscovery(LlmProvider provider, string model, IReadOnlyList<LlmCatalogModel> catalog) =>
        provider == LlmProvider.Anthropic && Find(catalog, model) is null;

    /// <summary>The catalog entry of <paramref name="model"/> (case-insensitive, trimmed), or <c>null</c>.</summary>
    public static LlmCatalogModel? Find(IReadOnlyList<LlmCatalogModel> catalog, string? model)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var id = model?.Trim();
        return string.IsNullOrEmpty(id) ? null : catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
