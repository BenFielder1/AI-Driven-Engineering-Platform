using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.UnitTests.Projects;

/// <summary>FR-157, FR-158, FR-159, §4.24: provider enablement, availability and the new-project default.</summary>
public class LlmProviderRulesTests
{
    private static readonly LlmProvider[] All = [LlmProvider.Anthropic, LlmProvider.Openai, LlmProvider.Ollama, LlmProvider.Fake];

    /// <summary>Each provider's availability for a user: <paramref name="enabled"/> providers, the providers the user has a
    /// connection for, and the providers with a configured system key (used only with <paramref name="allowSystemKeys"/>).</summary>
    private static Dictionary<LlmProvider, LlmProviderAvailability?> Availability(
        LlmProvider[] enabled, LlmProvider[]? connections = null, LlmProvider[]? systemKeys = null, bool allowSystemKeys = false) =>
        All.ToDictionary(
            p => p,
            p => LlmProviderRules.Available(
                p,
                enabled.Contains(p),
                (systemKeys ?? []).Contains(p),
                (connections ?? []).Count(c => c == p),
                allowSystemKeys));

    // ------------------------------------------------------------------ Available (FR-158)

    [Theory]
    [InlineData(LlmProvider.Ollama, false, 0, false, true, LlmProviderReason.Keyless)]
    [InlineData(LlmProvider.Fake, false, 0, false, true, LlmProviderReason.Keyless)]
    [InlineData(LlmProvider.Anthropic, true, 0, true, true, LlmProviderReason.SystemKey)]
    [InlineData(LlmProvider.Openai, true, 1, true, true, LlmProviderReason.SystemKey)]
    [InlineData(LlmProvider.Anthropic, true, 1, false, true, LlmProviderReason.OwnConnection)]
    [InlineData(LlmProvider.Anthropic, false, 2, true, true, LlmProviderReason.OwnConnection)]
    [InlineData(LlmProvider.Openai, true, 0, false, false, LlmProviderReason.NeedsConnection)]
    [InlineData(LlmProvider.Anthropic, false, 0, true, false, LlmProviderReason.NeedsConnection)]
    public void FR158_Availability_and_reason_of_an_enabled_provider(
        LlmProvider provider, bool systemKeySet, int connections, bool allowSystemKeys, bool usable, LlmProviderReason reason) =>
        Assert.Equal(
            new LlmProviderAvailability(usable, reason),
            LlmProviderRules.Available(provider, enabled: true, systemKeySet, connections, allowSystemKeys));

    [Theory]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.Openai)]
    [InlineData(LlmProvider.Ollama)]
    [InlineData(LlmProvider.Fake)]
    public void FR157_A_disabled_provider_has_no_availability_whatever_the_keys(LlmProvider provider) =>
        Assert.Null(LlmProviderRules.Available(provider, enabled: false, configured: true, userConnections: 3, allowSystemKeys: true));

    [Fact]
    public void FR158_Anthropic_with_no_system_key_and_one_connection_is_usable_as_own_connection() =>
        Assert.Equal(
            new LlmProviderAvailability(true, LlmProviderReason.OwnConnection),
            LlmProviderRules.Available(LlmProvider.Anthropic, enabled: true, configured: false, userConnections: 1, allowSystemKeys: false));

    // ------------------------------------------------------------------ CanChoose / IsEnabled (FR-157, FR-159)

    [Fact]
    public void FR157_A_disabled_provider_is_refused_with_LLM_PROVIDER_UNAVAILABLE()
    {
        var chosen = LlmProviderRules.CanChoose(LlmProvider.Fake, availability: null);
        var kept = LlmProviderRules.IsEnabled(LlmProvider.Fake, enabled: false);

        Assert.Equal(ErrorCodes.LlmProviderUnavailable, chosen.Error?.Code);
        Assert.Equal(ErrorCodes.LlmProviderUnavailable, kept.Error?.Code);
        Assert.Contains("turned off", chosen.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(400, ErrorCodes.StatusCodeFor(ErrorCodes.LlmProviderUnavailable));
    }

    [Fact]
    public void FR159_An_enabled_provider_the_user_cannot_use_yet_is_refused_with_LLM_PROVIDER_UNAVAILABLE()
    {
        var result = LlmProviderRules.CanChoose(LlmProvider.Anthropic, new LlmProviderAvailability(false, LlmProviderReason.NeedsConnection));

        Assert.Equal(ErrorCodes.LlmProviderUnavailable, result.Error?.Code);
        Assert.Contains("connection", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlmProviderReason.SystemKey)]
    [InlineData(LlmProviderReason.OwnConnection)]
    [InlineData(LlmProviderReason.Keyless)]
    public void FR159_A_usable_enabled_provider_can_be_chosen(LlmProviderReason reason)
    {
        Assert.True(LlmProviderRules.CanChoose(LlmProvider.Anthropic, new LlmProviderAvailability(true, reason)).IsSuccess);
        Assert.True(LlmProviderRules.IsEnabled(LlmProvider.Anthropic, enabled: true).IsSuccess);
    }

    // ------------------------------------------------------------------ DefaultForNewProject (§4.24 table)

    [Fact]
    public void FR159_All_four_enabled_and_no_key_providers_usable_with_default_anthropic_gives_Ollama()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(LlmProvider.Anthropic, Availability(All));

        Assert.Equal(LlmProvider.Ollama, result.Value);
    }

    [Fact]
    public void FR159_Railway_with_an_Anthropic_connection_and_default_anthropic_gives_Anthropic()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(
            LlmProvider.Anthropic,
            Availability([LlmProvider.Anthropic, LlmProvider.Openai], connections: [LlmProvider.Anthropic]));

        Assert.Equal(LlmProvider.Anthropic, result.Value);
    }

    [Fact]
    public void FR159_Railway_with_no_connection_and_default_anthropic_is_LLM_PROVIDER_UNAVAILABLE()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(LlmProvider.Anthropic, Availability([LlmProvider.Anthropic, LlmProvider.Openai]));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.LlmProviderUnavailable, result.Error!.Code);
    }

    [Fact]
    public void FR159_CI_with_all_four_enabled_and_default_fake_gives_Fake()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(LlmProvider.Fake, Availability(All));

        Assert.Equal(LlmProvider.Fake, result.Value);
    }

    [Fact]
    public void FR157_A_disabled_provider_is_never_the_default_even_when_configured()
    {
        // Fake is the configured default but disabled; Ollama (keyless) is the first enabled usable provider.
        var result = ProjectSettingsRules.DefaultForNewProject(
            LlmProvider.Fake,
            Availability([LlmProvider.Anthropic, LlmProvider.Openai, LlmProvider.Ollama]));

        Assert.Equal(LlmProvider.Ollama, result.Value);
    }

    [Fact]
    public void FR159_Default_prefers_anthropic_then_openai_when_both_are_usable_through_connections()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(
            LlmProvider.Ollama,
            Availability([LlmProvider.Anthropic, LlmProvider.Openai], connections: [LlmProvider.Openai, LlmProvider.Anthropic]));

        Assert.Equal(LlmProvider.Anthropic, result.Value);
    }

    [Fact]
    public void FR159_An_allowed_system_key_makes_the_configured_default_the_new_project_default()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(
            LlmProvider.Openai,
            Availability(All, systemKeys: [LlmProvider.Openai], allowSystemKeys: true));

        Assert.Equal(LlmProvider.Openai, result.Value);
    }

    [Fact]
    public void FR159_A_system_key_that_is_not_allowed_does_not_count()
    {
        var result = ProjectSettingsRules.DefaultForNewProject(
            LlmProvider.Openai,
            Availability([LlmProvider.Anthropic, LlmProvider.Openai], systemKeys: [LlmProvider.Openai], allowSystemKeys: false));

        Assert.Equal(ErrorCodes.LlmProviderUnavailable, result.Error?.Code);
    }

    [Fact]
    public void FR159_Preference_order_is_anthropic_openai_ollama_fake() =>
        Assert.Equal(All, LlmProviderRules.PreferenceOrder);
}
