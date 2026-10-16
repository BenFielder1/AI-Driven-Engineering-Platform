using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.UnitTests.Projects;

/// <summary>FR-78, FR-79, D-V2-5: personal LLM connections, binding and key choice.</summary>
public class LlmConnectionRulesTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Ben = Guid.CreateVersion7();

    // ------------------------------------------------------------------ FR-78 connections

    [Fact]
    public void FR78_A_connection_needs_a_keyed_provider_a_label_and_a_key()
    {
        Assert.True(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, "Work", "sk-ant-api03_AbC-123").IsSuccess);
        Assert.True(LlmConnectionRules.ValidateNew(LlmProvider.Openai, "  Personal  ", "sk-proj-abc").IsSuccess);
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Ollama, "Local", "x"), "provider");
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Fake, "Fake", "x"), "provider");
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, "   ", "sk-ant-x"), "label");
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, new string('a', LlmConnectionRules.MaxLabelLength + 1), "sk-ant-x"), "label");
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, "Work", "  "), "apiKey");
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, "Work", new string('k', LlmConnectionRules.MaxApiKeyLength + 1)), "apiKey");
    }

    [Theory]
    [InlineData("sk-ant-abc\ndef")]
    [InlineData("sk-ant-abcdef\n")]
    [InlineData(" sk-ant-abcdef")]
    [InlineData("sk-ant abcdef")]
    [InlineData("sk-ant-abc\u0001def")]
    [InlineData("sk-ant-abc\tdef")]
    public void FR78_Keys_with_whitespace_or_control_characters_are_refused(string key) =>
        AssertProblem(LlmConnectionRules.ValidateNew(LlmProvider.Anthropic, "Work", key), "apiKey");

    [Fact]
    public void FR78_Renaming_needs_a_label() =>
        AssertProblem(LlmConnectionRules.ValidateLabel(""), "label");

    [Fact]
    public void FR78_Ollama_and_Fake_are_always_available_without_a_key() =>
        Assert.Equal([LlmProvider.Ollama, LlmProvider.Fake], LlmConnectionRules.KeylessProviders);

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(null, false)]
    public void FR78_Only_a_401_during_a_run_invalidates_the_key(int? status, bool invalidates) =>
        Assert.Equal(invalidates, LlmConnectionRules.RunFailureInvalidatesKey(status));

    [Fact]
    public void FR79_Members_see_label_and_last4_only() =>
        Assert.Equal("Work (••••a1b2)", LlmConnectionRules.DisplayName("Work", "a1b2"));

    // ------------------------------------------------------------------ FR-79 binding

    [Fact]
    public void FR79_An_Owner_binds_their_own_connection_for_the_provider() =>
        Assert.True(LlmConnectionRules.CanBind(Ana, ProjectRole.Owner, new LlmConnectionRef(Guid.CreateVersion7(), Ana, LlmProvider.Openai), LlmProvider.Openai).IsSuccess);

    [Theory]
    [InlineData(ProjectRole.Contributor)]
    [InlineData(ProjectRole.Viewer)]
    [InlineData(null)]
    public void FR79_Only_Owners_bind(ProjectRole? role) =>
        Assert.Equal(ErrorCodes.Forbidden, LlmConnectionRules.CanBind(Ana, role, new LlmConnectionRef(Guid.CreateVersion7(), Ana, LlmProvider.Openai), LlmProvider.Openai).Error!.Code);

    [Fact]
    public void FR79_Binding_another_users_connection_is_CREDENTIAL_NOT_OWNER() =>
        Assert.Equal(ErrorCodes.CredentialNotOwner, LlmConnectionRules.CanBind(Ana, ProjectRole.Owner, new LlmConnectionRef(Guid.CreateVersion7(), Ben, LlmProvider.Openai), LlmProvider.Openai).Error!.Code);

    [Fact]
    public void FR79_Binding_an_unknown_connection_is_NOT_FOUND() =>
        Assert.Equal(ErrorCodes.NotFound, LlmConnectionRules.CanBind(Ana, ProjectRole.Owner, null, LlmProvider.Openai).Error!.Code);

    [Fact]
    public void FR79_A_connection_for_another_provider_is_VALIDATION_on_credentialId() =>
        AssertProblem(LlmConnectionRules.CanBind(Ana, ProjectRole.Owner, new LlmConnectionRef(Guid.CreateVersion7(), Ana, LlmProvider.Anthropic), LlmProvider.Openai), "credentialId");

    [Theory]
    [InlineData(LlmProvider.Anthropic, LlmProvider.Anthropic, true)]
    [InlineData(LlmProvider.Anthropic, LlmProvider.Openai, false)]
    [InlineData(LlmProvider.Anthropic, LlmProvider.Ollama, false)]
    [InlineData(null, LlmProvider.Openai, false)]
    public void FR79_Changing_the_provider_drops_a_binding_of_another(LlmProvider? bound, LlmProvider next, bool keeps) =>
        Assert.Equal(keeps, LlmConnectionRules.KeepsBinding(bound, next));

    [Theory]
    [InlineData(ProjectRole.Contributor, true)]
    [InlineData(ProjectRole.Viewer, true)]
    [InlineData(ProjectRole.Owner, false)]
    public void FR79_The_binding_Owner_losing_the_Owner_role_clears_the_binding(ProjectRole newRole, bool clears)
    {
        Assert.Equal(clears, LlmConnectionRules.RoleChangeClearsBinding(Ana, Ana, newRole));
        Assert.False(LlmConnectionRules.RoleChangeClearsBinding(Ana, Ben, newRole));
        Assert.False(LlmConnectionRules.RoleChangeClearsBinding(null, Ana, newRole));
    }

    [Fact]
    public void FR79_The_binding_Owner_leaving_clears_the_binding()
    {
        Assert.True(LlmConnectionRules.RemovalClearsBinding(Ana, Ana));
        Assert.False(LlmConnectionRules.RemovalClearsBinding(Ana, Ben));
        Assert.False(LlmConnectionRules.RemovalClearsBinding(null, Ana));
    }

    // ------------------------------------------------------------------ D-V2-5 key choice

    [Theory]
    [InlineData(LlmProvider.Openai, LlmProvider.Openai, false, false, LlmKeySource.Connection)]
    [InlineData(LlmProvider.Openai, LlmProvider.Openai, true, true, LlmKeySource.Connection)]
    [InlineData(LlmProvider.Openai, null, true, true, LlmKeySource.System)]
    [InlineData(LlmProvider.Openai, null, false, true, LlmKeySource.None)]
    [InlineData(LlmProvider.Openai, null, true, false, LlmKeySource.None)]
    [InlineData(LlmProvider.Openai, LlmProvider.Anthropic, false, false, LlmKeySource.None)]
    [InlineData(LlmProvider.Ollama, null, false, false, LlmKeySource.NotNeeded)]
    [InlineData(LlmProvider.Fake, LlmProvider.Openai, true, true, LlmKeySource.NotNeeded)]
    public void FR79_Runs_use_the_bound_key_and_system_keys_only_when_allowed_and_nothing_is_bound(
        LlmProvider provider, LlmProvider? bound, bool allowSystemKeys, bool hasSystemKey, LlmKeySource expected) =>
        Assert.Equal(expected, LlmConnectionRules.ChooseKey(provider, bound, allowSystemKeys, hasSystemKey));

    [Fact]
    public void FR79_Cleared_reason_wire_values() =>
        Assert.Equal(["owner_role_lost", "member_removed", "connection_deleted"], EnumStrings.Values<LlmBindingClearedReason>());

    [Fact]
    public void FR78_Credential_status_wire_values() =>
        Assert.Equal(["valid", "invalid", "unchecked"], EnumStrings.Values<LlmCredentialStatus>());

    private static void AssertProblem(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith(field + ":", StringComparison.Ordinal));
    }
}
