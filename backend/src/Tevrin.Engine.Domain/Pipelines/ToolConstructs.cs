namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-358 (0.8.0): a tool's permission tier, registered by its pack (<see cref="IPackRegistry.AddTool(string, ToolTier, bool)"/>).
/// Ordered: a stage whose <c>tool_policy.max_tier</c> is a tier may list tools of that tier or below.
/// </summary>
public enum ToolTier
{
    /// <summary>Reads only (the SDLC's repo tools).</summary>
    Read = 0,

    /// <summary>Changes something that can be undone.</summary>
    Write = 1,

    /// <summary>Changes something that can't be undone; every call waits on an approved <c>tool_approval</c> gate (FR-359).</summary>
    Destructive = 2,
}

/// <summary>FR-358: <c>tool_policy: { max_tier }</c> on an agent stage or an <c>agents:</c> binding.</summary>
public sealed record ToolPolicyDefinition(ToolTier MaxTier);

/// <summary>FR-358: the tier rules, pure.</summary>
public static class ToolTierRules
{
    /// <summary>FR-358: a stage or binding without <c>tool_policy</c> offers tools up to <c>write</c>.</summary>
    public const ToolTier DefaultMaxTier = ToolTier.Write;

    /// <summary>
    /// The tier of a tool registered without one (<see cref="IPackRegistry.AddTool(string)"/>, or bound only in the host's DI):
    /// <c>write</c>. That keeps every definition written before 0.8.0 valid (the default <c>max_tier</c> is <c>write</c>),
    /// while a stage restricted to <c>read</c> refuses an unclassified tool. Unclassified tools are never treated as
    /// destructive; a pack whose tool is destructive must say so.
    /// </summary>
    public const ToolTier DefaultRegisteredTier = ToolTier.Write;

    /// <summary>The highest tier <paramref name="policy"/> allows (<see cref="DefaultMaxTier"/> when absent).</summary>
    public static ToolTier MaxTierOf(ToolPolicyDefinition? policy) => policy?.MaxTier ?? DefaultMaxTier;

    /// <summary>Whether a tool of <paramref name="tier"/> may be offered under <paramref name="maxTier"/>.</summary>
    public static bool Allows(ToolTier maxTier, ToolTier tier) => tier <= maxTier;

    /// <summary>The registered tier of <paramref name="tool"/> in <paramref name="catalog"/>
    /// (<see cref="DefaultRegisteredTier"/> when the catalog names none).</summary>
    public static ToolTier TierOf(PackCatalog catalog, string tool)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.ToolTiers.TryGetValue(tool, out var tier) ? tier : DefaultRegisteredTier;
    }

    /// <summary>Whether <paramref name="policy"/> is absent or the default, so the canonical JSON leaves it out.</summary>
    public static bool IsDefault(ToolPolicyDefinition? policy) => MaxTierOf(policy) == DefaultMaxTier;
}
