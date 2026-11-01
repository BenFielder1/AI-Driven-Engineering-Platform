using Aide.Domain.Agents;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Agents;

/// <summary>FR-160, §4.24: the effective output token limit per provider and model.</summary>
public class LlmOutputLimitsTests
{
    private const int Fallback = 16_000;

    private static readonly LlmCatalogModel[] Anthropic =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", 128_000, 1_000_000, true),
        new("claude-haiku-4-5", "Claude Haiku 4.5", 64_000, 200_000, false),
    ];

    private static readonly LlmCatalogModel[] Ollama = [new("qwen2.5-coder", "Qwen2.5 Coder", 16_000, 32_768, false)];

    // The §4.24 worked-example table, row by row.

    [Fact]
    public void FR160_Listed_claude_opus_5_5_without_a_ceiling_is_128000() =>
        Assert.Equal(128_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-opus-5-5", Anthropic, discovered: null, ceiling: null, Fallback));

    [Fact]
    public void FR160_Listed_claude_opus_5_5_with_a_32000_ceiling_is_32000() =>
        Assert.Equal(32_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-opus-5-5", Anthropic, discovered: null, ceiling: 32_000, Fallback));

    [Fact]
    public void FR160_Listed_claude_haiku_4_5_is_64000() =>
        Assert.Equal(64_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-haiku-4-5", Anthropic, discovered: null, ceiling: null, Fallback));

    [Fact]
    public void FR160_Unlisted_Anthropic_model_uses_the_discovered_limit() =>
        Assert.Equal(96_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-new-x", Anthropic, discovered: 96_000, ceiling: null, Fallback));

    [Fact]
    public void FR160_Unlisted_Anthropic_model_whose_lookup_failed_uses_the_fallback() =>
        Assert.Equal(16_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-new-x", Anthropic, discovered: null, ceiling: null, Fallback));

    [Fact]
    public void FR160_Unlisted_Ollama_model_uses_the_fallback_and_ignores_discovery() =>
        Assert.Equal(16_000, LlmOutputLimits.Resolve(LlmProvider.Ollama, "llama3.3", Ollama, discovered: 96_000, ceiling: null, Fallback));

    // Edges of the rule.

    [Fact]
    public void FR160_The_catalog_wins_over_a_discovered_limit() =>
        Assert.Equal(128_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-opus-5-5", Anthropic, discovered: 8_000, ceiling: null, Fallback));

    [Fact]
    public void FR160_Model_ids_match_case_insensitively_after_trimming() =>
        Assert.Equal(64_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "  Claude-Haiku-4-5 ", Anthropic, discovered: null, ceiling: null, Fallback));

    [Fact]
    public void FR160_A_ceiling_above_the_limit_changes_nothing() =>
        Assert.Equal(64_000, LlmOutputLimits.Resolve(LlmProvider.Anthropic, "claude-haiku-4-5", Anthropic, discovered: null, ceiling: 100_000, Fallback));

    [Fact]
    public void FR160_The_ceiling_also_caps_the_fallback() =>
        Assert.Equal(8_000, LlmOutputLimits.Resolve(LlmProvider.Ollama, "llama3.3", Ollama, discovered: null, ceiling: 8_000, Fallback));

    [Theory]
    [InlineData(LlmProvider.Anthropic, "claude-new-x", true)]
    [InlineData(LlmProvider.Anthropic, "claude-opus-5-5", false)]
    [InlineData(LlmProvider.Openai, "gpt-new", false)]
    [InlineData(LlmProvider.Ollama, "llama3.3", false)]
    [InlineData(LlmProvider.Fake, "fake-2", false)]
    public void FR160_Only_unlisted_Anthropic_models_are_looked_up(LlmProvider provider, string model, bool lookup) =>
        Assert.Equal(lookup, LlmOutputLimits.NeedsDiscovery(provider, model, provider == LlmProvider.Anthropic ? Anthropic : Ollama));
}
