using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Connections;
using Aide.Api.Features.Llm;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Llm;

/// <summary>
/// FR-157, FR-158, FR-159 (§8: "a backend integration test with Llm__Providers__Fake__Enabled=false asserts that Fake is
/// absent from /api/llm/providers and refused on create"): provider enablement, <c>GET /api/llm/providers</c> and the LLM
/// choice on <c>POST /api/projects</c>. Anthropic and OpenAI key checks are answered by stubs (no real provider).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LlmProviderTests(AideApiFactory factory) : IDisposable
{
    private const string AnthropicKey = "sk-ant-api03-wizardChoiceKey-m3n4";
    private const string OpenAiKey = "sk-proj-wizardChoiceOpenAi-o5p6";

    private readonly LlmStubHandler _anthropic = new();
    private readonly LlmStubHandler _openai = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ GET /api/llm/providers (FR-158)

    [Fact]
    public async Task FR158_CI_defaults_list_all_four_providers_with_catalogs_and_Fake_usable()
    {
        using var user = await ApiSession.RegisterAsync(App());

        var providers = await ProvidersAsync(user);

        Assert.Equal([LlmProvider.Anthropic, LlmProvider.Openai, LlmProvider.Ollama, LlmProvider.Fake], providers.Select(p => p.Provider));
        var fake = providers.Single(p => p.Provider == LlmProvider.Fake);
        Assert.Equal((true, LlmProviderReason.Keyless, "fake-1"), (fake.Usable, fake.Reason, fake.DefaultModel));
        var anthropic = providers.Single(p => p.Provider == LlmProvider.Anthropic);
        Assert.Equal((false, LlmProviderReason.NeedsConnection, "claude-opus-5-5"), (anthropic.Usable, anthropic.Reason, anthropic.DefaultModel));
        Assert.Empty(anthropic.Connections);
        Assert.Equal(new LlmModelDto("claude-opus-5-5", "Claude Opus 5.5", 128_000, 1_000_000, true), anthropic.Catalog[0]);
        Assert.Equal(
            ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1", "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5"],
            anthropic.Catalog.Select(m => m.Id));
        Assert.Equal((64_000, 200_000), (anthropic.Catalog[^1].MaxOutputTokens, anthropic.Catalog[^1].ContextTokens));
        Assert.Equal(["gpt-6-astra", "gpt-6.1-sol"], providers.Single(p => p.Provider == LlmProvider.Openai).Catalog.Select(m => m.Id));
        Assert.Equal(new LlmModelDto("qwen2.5-coder", "Qwen2.5 Coder", 16_000, 32_768, false), Assert.Single(providers.Single(p => p.Provider == LlmProvider.Ollama).Catalog));
    }

    [Fact]
    public async Task FR158_Anthropic_with_no_system_key_and_one_connection_is_own_connection_and_lists_it_without_the_key()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey, "Ana's key");

        var response = await user.Client.GetAsync(new Uri("/api/llm/providers", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var providers = await ProjectApi.ReadAsync<List<LlmProviderDto>>(response);

        var anthropic = providers.Single(p => p.Provider == LlmProvider.Anthropic);
        Assert.Equal((true, LlmProviderReason.OwnConnection), (anthropic.Usable, anthropic.Reason));
        Assert.Equal(new LlmProviderConnectionDto(connection.Id, "Ana's key", "m3n4"), Assert.Single(anthropic.Connections));
        Assert.Equal(("claude-opus-5-5", 128_000, true), (anthropic.Catalog[0].Id, anthropic.Catalog[0].MaxOutputTokens, anthropic.Catalog[0].Recommended));
        Assert.Empty(providers.Single(p => p.Provider == LlmProvider.Openai).Connections);
        Assert.DoesNotContain(AnthropicKey, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR158_Another_users_connections_are_not_listed()
    {
        var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        await LlmStubs.CreateAsync(owner.Client, "openai", OpenAiKey);

        var openai = (await ProvidersAsync(other)).Single(p => p.Provider == LlmProvider.Openai);

        Assert.Equal((false, LlmProviderReason.NeedsConnection), (openai.Usable, openai.Reason));
        Assert.Empty(openai.Connections);
    }

    [Fact]
    public async Task FR158_An_allowed_system_key_makes_the_provider_usable_as_system_key()
    {
        using var user = await ApiSession.RegisterAsync(App(("Llm:Providers:OpenAI:ApiKey", "sk-proj-systemKey-q7r8"), ("Llm:AllowSystemKeys", "true")));

        var openai = (await ProvidersAsync(user)).Single(p => p.Provider == LlmProvider.Openai);

        Assert.Equal((true, LlmProviderReason.SystemKey), (openai.Usable, openai.Reason));
    }

    [Fact]
    public async Task FR159_providers_mark_the_default_for_new_project()
    {
        // Test config: Llm:DefaultProvider=fake, Fake enabled and keyless → Fake is the default.
        using var user = await ApiSession.RegisterAsync(App());
        var defaults = await ProvidersAsync(user);
        Assert.Equal([LlmProvider.Fake], defaults.Where(p => p.IsDefault).Select(p => p.Provider));

        // Fake disabled, configured default Anthropic with no connection or system key → first usable in preference
        // order: Ollama (keyless).
        using var noFake = await ApiSession.RegisterAsync(App(("Llm:Providers:Fake:Enabled", "false"), ("Llm:DefaultProvider", "anthropic")));
        var ollamaDefault = await ProvidersAsync(noFake);
        Assert.Equal([LlmProvider.Ollama], ollamaDefault.Where(p => p.IsDefault).Select(p => p.Provider));

        // Fake and Ollama disabled, no usable keyed provider → LLM_PROVIDER_UNAVAILABLE, so none is marked.
        using var none = await ApiSession.RegisterAsync(RailwayApp());
        var unavailable = await ProvidersAsync(none);
        Assert.NotEmpty(unavailable);
        Assert.DoesNotContain(unavailable, p => p.IsDefault);

        // The same Railway config with an Anthropic connection → Anthropic is the default.
        var railway = RailwayApp();
        using var connected = await ApiSession.RegisterAsync(railway);
        await LlmStubs.CreateAsync(connected.Client, "anthropic", AnthropicKey);
        Assert.Equal([LlmProvider.Anthropic], (await ProvidersAsync(connected)).Where(p => p.IsDefault).Select(p => p.Provider));
    }

    [Fact]
    public async Task FR158_Anonymous_is_401()
    {
        using var anonymous = new ApiSession(App());

        await Problems.AssertAsync(await anonymous.Client.GetAsync(new Uri("/api/llm/providers", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    // ------------------------------------------------------------------ Enablement (FR-157)

    [Fact]
    public async Task FR157_Disabled_Fake_is_absent_from_every_list_and_refused_on_create_and_settings()
    {
        var app = App(("Llm:Providers:Fake:Enabled", "false"), ("Llm:DefaultProvider", "ollama"));
        using var user = await ApiSession.RegisterAsync(app);

        var providers = await ProvidersAsync(user);
        var refused = await CreateAsync(user, new { provider = "fake", defaultModel = "fake-1" });
        var project = await ProjectApi.CreateAsync(user.Client);
        var settings = await ProjectApi.ReadAsync<LlmSettingsDto>(await user.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings/llm", UriKind.Relative), Ct));
        var patch = await ProjectApi.PatchAsync(user.Client, $"/api/projects/{project.Id}/settings/llm", new { provider = "fake" });
        var connections = await ProjectApi.ReadAsync<LlmConnectionsDto>(await user.Client.GetAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct));

        Assert.DoesNotContain(providers, p => p.Provider == LlmProvider.Fake);
        Assert.Equal(3, providers.Count);
        await Problems.AssertAsync(refused, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
        Assert.Equal(LlmProvider.Ollama, settings.Provider);
        Assert.DoesNotContain(settings.Providers, p => p.Provider == LlmProvider.Fake);
        await Problems.AssertAsync(patch, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
        Assert.Equal([LlmProvider.Ollama], connections.KeylessProviders);
    }

    [Fact]
    public async Task FR157_A_project_still_set_to_a_disabled_provider_cannot_keep_it_through_settings()
    {
        var enabled = App();
        using var fixture = await ProjectApi.FixtureAsync(enabled);
        var disabled = App(("Llm:Providers:Fake:Enabled", "false"), ("Llm:DefaultProvider", "ollama"));
        using var owner = await ApiSession.LoginAsync(disabled, fixture.Owner.User!.Email);

        var patch = await ProjectApi.PatchAsync(owner.Client, $"{fixture.Path}/settings/llm", new { defaultModel = "fake-2" });
        var moved = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(owner.Client, $"{fixture.Path}/settings/llm", new { provider = "ollama" }));

        await Problems.AssertAsync(patch, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
        Assert.Equal((LlmProvider.Ollama, "qwen2.5-coder"), (moved.Provider, moved.DefaultModel));
    }

    [Fact]
    public async Task FR157_On_Railway_with_no_connection_create_without_llm_is_LLM_PROVIDER_UNAVAILABLE_never_Fake()
    {
        using var user = await ApiSession.RegisterAsync(RailwayApp());

        var response = await user.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("rw"), idea = "A todo app.", git = new { provider = "local" } }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
    }

    [Fact]
    public async Task FR157_On_Railway_only_Anthropic_and_OpenAI_are_listed()
    {
        using var user = await ApiSession.RegisterAsync(RailwayApp());

        Assert.Equal([LlmProvider.Anthropic, LlmProvider.Openai], (await ProvidersAsync(user)).Select(p => p.Provider));
    }

    // ------------------------------------------------------------------ Create with llm (FR-159)

    [Fact]
    public async Task FR159_Create_with_Anthropic_a_connection_and_claude_opus_5_5_binds_the_connection()
    {
        var app = RailwayApp();
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey);

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await CreateAsync(user, new { provider = "anthropic", credentialId = connection.Id, defaultModel = "claude-opus-5-5" }),
            HttpStatusCode.Created);

        var settings = await ProjectApi.ReadAsync<LlmSettingsDto>(await user.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings/llm", UriKind.Relative), Ct));
        Assert.Equal((LlmProvider.Anthropic, "claude-opus-5-5", true), (settings.Provider, settings.DefaultModel, settings.HasApiKey));
        Assert.Equal((connection.Id, user.User!.Id), (settings.Binding!.CredentialId, settings.Binding.BoundBy.Id));
        Assert.Equal("claude-sonnet-5-5", settings.ModelsByAgent.IdeaRefinement); // omitted modelsByAgent: configured defaults
        var stored = await StoredAsync(app, project.Id);
        Assert.Equal((connection.Id, user.User.Id), (stored.CredentialId, stored.BoundByUserId));
    }

    [Fact]
    public async Task FR159_Create_without_llm_on_Railway_with_an_Anthropic_connection_defaults_to_Anthropic_bound_to_it()
    {
        var app = RailwayApp();
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey);

        var project = await ProjectApi.CreateAsync(user.Client);

        var stored = await StoredAsync(app, project.Id);
        Assert.Equal((LlmProvider.Anthropic, "claude-opus-5-5", (Guid?)connection.Id), (stored.Provider, stored.DefaultModel, stored.CredentialId));
    }

    [Fact]
    public async Task FR159_Create_without_a_credentialId_binds_the_callers_newest_connection_for_the_provider()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        await LlmStubs.CreateAsync(user.Client, "openai", OpenAiKey, "Older");
        var newest = await LlmStubs.CreateAsync(user.Client, "openai", OpenAiKey, "Newer");

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(user, new { provider = "openai", defaultModel = "gpt-6-astra" }), HttpStatusCode.Created);

        Assert.Equal(newest.Id, (await StoredAsync(app, project.Id)).CredentialId);
    }

    [Fact]
    public async Task FR159_Create_with_Ollama_and_per_agent_models_stores_them_without_a_binding()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await CreateAsync(user, new { provider = "ollama", defaultModel = " llama3.3 ", modelsByAgent = new { codeReview = "qwen2.5-coder" } }),
            HttpStatusCode.Created);

        var stored = await StoredAsync(app, project.Id);
        Assert.Equal((LlmProvider.Ollama, "llama3.3", (Guid?)null), (stored.Provider, stored.DefaultModel, stored.CredentialId));
        Assert.Equal(new Dictionary<string, string> { ["code_review"] = "qwen2.5-coder" }, stored.ModelsByAgent);
    }

    [Fact]
    public async Task FR159_Create_with_another_users_connection_is_403_CREDENTIAL_NOT_OWNER()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        var theirs = await LlmStubs.CreateAsync(other.Client, "anthropic", AnthropicKey);

        var response = await CreateAsync(user, new { provider = "anthropic", credentialId = theirs.Id, defaultModel = "claude-opus-5-5" });

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
    }

    [Fact]
    public async Task FR159_Create_with_an_unknown_connection_is_404()
    {
        using var user = await ApiSession.RegisterAsync(App());

        var response = await CreateAsync(user, new { provider = "anthropic", credentialId = Guid.CreateVersion7(), defaultModel = "claude-opus-5-5" });

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR159_Create_with_a_connection_of_another_provider_is_400_VALIDATION()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var openai = await LlmStubs.CreateAsync(user.Client, "openai", OpenAiKey);

        var response = await CreateAsync(user, new { provider = "anthropic", credentialId = openai.Id, defaultModel = "claude-opus-5-5" });

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Contains("credentialId", body.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FR159_Create_with_an_empty_model_is_400_VALIDATION(string model)
    {
        using var user = await ApiSession.RegisterAsync(App());

        var response = await CreateAsync(user, new { provider = "ollama", defaultModel = model });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR159_Create_with_Anthropic_and_no_connection_or_system_key_is_LLM_PROVIDER_UNAVAILABLE()
    {
        using var user = await ApiSession.RegisterAsync(App());

        var response = await CreateAsync(user, new { provider = "anthropic", defaultModel = "claude-opus-5-5" });

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
    }

    // ------------------------------------------------------------------ Invalid connections (FR-159)

    [Fact]
    public async Task FR159_An_only_invalid_Anthropic_connection_is_needs_connection_not_default_and_not_bound()
    {
        var app = RailwayApp();
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey);
        await MarkInvalidAsync(app, connection.Id);

        var providers = await ProvidersAsync(user);
        var withoutLlm = await user.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("inv"), idea = "A todo app.", git = new { provider = "local" } }, Ct);
        var withAnthropic = await CreateAsync(user, new { provider = "anthropic", defaultModel = "claude-opus-5-5" });

        var anthropic = providers.Single(p => p.Provider == LlmProvider.Anthropic);
        Assert.Equal((false, LlmProviderReason.NeedsConnection, false), (anthropic.Usable, anthropic.Reason, anthropic.IsDefault));
        Assert.Equal(connection.Id, Assert.Single(anthropic.Connections).Id); // still listed, but counts for nothing
        Assert.DoesNotContain(providers, p => p.IsDefault);
        await Problems.AssertAsync(withoutLlm, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
        await Problems.AssertAsync(withAnthropic, HttpStatusCode.BadRequest, ErrorCodes.LlmProviderUnavailable);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.ProjectLlmSettings.AnyAsync(s => s.CredentialId == connection.Id, Ct)));
    }

    [Fact]
    public async Task FR159_The_fallback_binding_is_the_newest_valid_connection_never_an_invalid_one()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var valid = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey, "Older, valid");
        var invalid = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey, "Newer, invalid");
        await MarkInvalidAsync(app, invalid.Id);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(user, new { provider = "anthropic", defaultModel = "claude-opus-5-5" }), HttpStatusCode.Created);

        Assert.Equal(valid.Id, (await StoredAsync(app, project.Id)).CredentialId);
    }

    public void Dispose()
    {
        _anthropic.Dispose();
        _openai.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private WebApplicationFactory<Program> App(params (string Key, string Value)[] settings) => LlmStubs.App(factory, _anthropic, _openai, settings);

    /// <summary>§9 Railway: Ollama and Fake disabled, the shipped default provider (anthropic), no system keys.</summary>
    private WebApplicationFactory<Program> RailwayApp() => App(
        ("Llm:Providers:Ollama:Enabled", "false"),
        ("Llm:Providers:Fake:Enabled", "false"),
        ("Llm:DefaultProvider", "anthropic"));

    private static async Task<List<LlmProviderDto>> ProvidersAsync(ApiSession session) =>
        await ProjectApi.ReadAsync<List<LlmProviderDto>>(await session.Client.GetAsync(new Uri("/api/llm/providers", UriKind.Relative), Ct));

    private static Task<HttpResponseMessage> CreateAsync(ApiSession session, object llm) =>
        session.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("llm"), idea = "A todo app.", git = new { provider = "local" }, llm }, Ct);

    private static Task<int> MarkInvalidAsync(WebApplicationFactory<Program> app, Guid credentialId) =>
        ProjectApi.WithDbAsync(app, db => db.LlmCredentials.Where(c => c.Id == credentialId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, LlmCredentialStatus.Invalid), Ct));

    private static Task<ProjectLlmSettings> StoredAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.ProjectLlmSettings.AsNoTracking().SingleAsync(s => s.ProjectId == projectId, Ct));
}
