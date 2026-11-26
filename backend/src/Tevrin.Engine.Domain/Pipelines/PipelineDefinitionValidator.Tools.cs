using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    /// <summary>FR-358 (0.8.0): tool tiers.</summary>
    private sealed partial class Run
    {
        /// <summary>FR-358: a stage's <c>tool_policy</c> belongs to its agent, so it needs an agent stage
        /// (<see cref="DefinitionErrorCodes.StageKind"/>); an <c>agents:</c> binding sets its own.</summary>
        private void ValidateToolPolicy(StageDefinition stage, string path)
        {
            if (stage.ToolPolicy is not null && stage.Agent is null)
            {
                Add(DefinitionErrorCodes.StageKind, path + ".tool_policy",
                    "'tool_policy' needs an agent stage; an 'agents:' binding sets its own 'tool_policy'.");
            }
        }

        /// <summary>
        /// FR-358: a registered <paramref name="tool"/> whose tier is above <paramref name="policy"/>'s <c>max_tier</c>
        /// (default <c>write</c>) gives <see cref="DefinitionErrorCodes.ToolTier"/> at <paramref name="at"/>. Unregistered
        /// and empty names are reported elsewhere (<c>DEF_UNKNOWN_TOOL</c>, <c>DEF_PARSE</c>).
        /// </summary>
        private void CheckToolTier(string tool, ToolPolicyDefinition? policy, string at)
        {
            if (string.IsNullOrEmpty(tool) || !_tools.Contains(tool))
            {
                return;
            }

            var tier = ToolTierRules.TierOf(catalog, tool);
            var maxTier = ToolTierRules.MaxTierOf(policy);
            if (!ToolTierRules.Allows(maxTier, tier))
            {
                Add(DefinitionErrorCodes.ToolTier, at,
                    $"Tool '{tool}' is '{tier.ToWire()}', above this agent's 'tool_policy.max_tier' '{maxTier.ToWire()}'.");
            }
        }
    }
}
