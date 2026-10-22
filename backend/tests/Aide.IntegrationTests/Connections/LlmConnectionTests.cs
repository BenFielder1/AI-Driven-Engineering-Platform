using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Connections;
using Aide.Api.Features.Tokens;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Connections;

/// <summary>FR-78, NFR-SEC2: personal LLM connections, with the providers' list-models endpoints stubbed (rule 11).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class LlmConnectionTests(AideApiFactory factory) : IDisposable
{
    private const string AnthropicKey = "sk-ant-api03-connectionSecretKey-a1b2";
    private const string OpenAiKey = "sk-proj-openaiConnectionSecret-c3d4";

    private readonly LlmStubHandler _anthropic = new();
    private readonly LlmStubHandler _openai = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR78_Adding_a_key_checks_it_with_list_models_and_returns_only_provider_label_last4_and_status()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/llm", new { provider = "anthropic", label = "  Work  ", apiKey = AnthropicKey }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var created = await ProjectApi.ReadAsync<LlmCredentialDto>(response, HttpStatusCode.Created);

        var sent = Assert.Single(_anthropic.Requests);
        Assert.Equal((HttpMethod.Get, "https://llm-api.invalid/v1/models?limit=1", AnthropicKey), (sent.Method, sent.Uri, sent.Headers["x-api-key"]));
        Assert.Equal((LlmProvider.Anthropic, "Work", "a1b2", LlmCredentialStatus.Valid), (created.Provider, created.Label, created.Last4, created.Status));
        Assert.NotNull(created.LastValidatedAt);
        Assert.Empty(created.BoundProjects);
        Assert.Equal($"/api/me/connections/llm/{created.Id}", response.Headers.Location?.ToString());
        Assert.DoesNotContain(AnthropicKey, text, StringComparison.Ordinal);

        var listText = await user.Client.GetStringAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct);
        var list = System.Text.Json.JsonSerializer.Deserialize<LlmConnectionsDto>(listText, ApiSession.Json)!;
        Assert.Equal(created.Id, Assert.Single(list.Credentials).Id);
        Assert.DoesNotContain(AnthropicKey, listText, StringComparison.Ordinal);

        // NFR-SEC2: encrypted at rest (Data Protection, LLM purpose), never logged.
        await AssertStoredEncryptedAsync(app, created.Id, AnthropicKey);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.ToString().Contains(AnthropicKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR78_A_key_the_provider_rejects_is_CREDENTIAL_INVALID_400_and_nothing_is_saved()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        _anthropic.Enqueue(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/llm", new { provider = "anthropic", label = "Work", apiKey = AnthropicKey }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        Assert.Equal(0, await CountAsync(app, user.User!.Id));
    }

    [Fact]
    public async Task FR78_A_provider_that_cannot_be_reached_is_UPSTREAM_ERROR_502_and_nothing_is_saved()
    {
        // The base factory's handlers refuse to send (a connection error), like an unreachable provider.
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/llm", new { provider = "openai", label = "Work", apiKey = OpenAiKey }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.Equal(0, await CountAsync(factory, user.User!.Id));
    }

    [Fact]
    public async Task FR78_A_key_check_that_times_out_is_UPSTREAM_ERROR_502_and_nothing_is_saved()
    {
        _openai.Delay = TimeSpan.FromSeconds(30);
        var app = LlmStubs.App(factory, _anthropic, _openai, ("Llm:KeyCheckTimeout", "00:00:00.200"));
        using var user = await ApiSession.RegisterAsync(app);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var response = await user.Client.PostAsJsonAsync("/api/me/connections/llm", new { provider = "openai", label = "Work", apiKey = OpenAiKey }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), started.Elapsed.ToString());
        Assert.Equal(0, await CountAsync(app, user.User!.Id));
    }

    [Theory]
    [InlineData("""{"provider":"ollama","label":"Local","apiKey":"x"}""", "provider")]
    [InlineData("""{"provider":"fake","label":"Fake","apiKey":"x"}""", "provider")]
    [InlineData("""{"provider":"anthropic","label":"   ","apiKey":"sk-ant-x"}""", "label")]
    [InlineData("""{"provider":"anthropic","label":"Work","apiKey":"sk-ant-first\nsecond"}""", "apiKey")]
    [InlineData("""{"provider":"anthropic","label":"Work","apiKey":"sk-ant with-space"}""", "apiKey")]
    [InlineData("""{"provider":"anthropic","label":"Work"}""", null)]
    [InlineData("""{"provider":"copilot","label":"Work","apiKey":"x"}""", null)]
    public async Task FR78_Invalid_connections_are_VALIDATION_and_never_reach_the_provider(string json, string? field)
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        var response = await user.Client.PostAsync(
            new Uri("/api/me/connections/llm", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }

        Assert.Empty(_anthropic.Requests);
        Assert.Equal(0, await CountAsync(app, user.User!.Id));
    }

    [Fact]
    public async Task FR78_A_user_can_rename_retest_and_delete_a_key()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var created = await LlmStubs.CreateAsync(user.Client, "openai", OpenAiKey);

        var renamed = await ProjectApi.ReadAsync<LlmCredentialDto>(await ProjectApi.PatchAsync(user.Client, $"/api/me/connections/llm/{created.Id}", new { label = "Personal" }));
        _openai.Enqueue(HttpStatusCode.Unauthorized);
        var rejected = await ProjectApi.ReadAsync<LlmCredentialDto>(await user.Client.PostAsync(new Uri($"/api/me/connections/llm/{created.Id}/test", UriKind.Relative), null, Ct));
        var accepted = await ProjectApi.ReadAsync<LlmCredentialDto>(await user.Client.PostAsync(new Uri($"/api/me/connections/llm/{created.Id}/test", UriKind.Relative), null, Ct));
        var deleted = await user.Client.DeleteAsync(new Uri($"/api/me/connections/llm/{created.Id}", UriKind.Relative), Ct);

        Assert.Equal(("Personal", LlmCredentialStatus.Valid), (renamed.Label, renamed.Status));
        Assert.Equal(LlmCredentialStatus.Invalid, rejected.Status);
        Assert.Equal(LlmCredentialStatus.Valid, accepted.Status);
        Assert.True(accepted.LastValidatedAt >= rejected.LastValidatedAt);
        Assert.Equal(3, _openai.Requests.Count);
        Assert.All(_openai.Requests, r => Assert.Equal($"Bearer {OpenAiKey}", r.Headers["Authorization"]));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync(app, user.User!.Id));
    }

    [Fact]
    public async Task FR78_Retesting_while_the_provider_is_unreachable_is_UPSTREAM_ERROR_and_keeps_the_status()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var created = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey);
        _anthropic.Enqueue(HttpStatusCode.ServiceUnavailable);

        var response = await user.Client.PostAsync(new Uri($"/api/me/connections/llm/{created.Id}/test", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        var stored = await ProjectApi.WithDbAsync(app, db => db.LlmCredentials.AsNoTracking().SingleAsync(c => c.Id == created.Id, Ct));
        Assert.Equal((LlmCredentialStatus.Valid, created.LastValidatedAt), (stored.Status, stored.LastValidatedAt));
    }

    [Fact]
    public async Task FR78_Only_the_owner_sees_or_changes_a_connection_others_get_NOT_FOUND()
    {
        var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        using var admin = await ApiSession.AdminAsync(app);
        var created = await LlmStubs.CreateAsync(owner.Client, "anthropic", AnthropicKey);
        var path = $"/api/me/connections/llm/{created.Id}";

        await Problems.AssertAsync(await ProjectApi.PatchAsync(other.Client, path, new { label = "Mine now" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await other.Client.PostAsync(new Uri($"{path}/test", UriKind.Relative), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await other.Client.DeleteAsync(new Uri(path, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await admin.Client.DeleteAsync(new Uri(path, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty((await other.Client.GetFromJsonAsync<LlmConnectionsDto>("/api/me/connections/llm", ApiSession.Json, Ct))!.Credentials);
        Assert.Empty((await admin.Client.GetFromJsonAsync<LlmConnectionsDto>("/api/me/connections/llm", ApiSession.Json, Ct))!.Credentials);
        Assert.Equal("Work", (await ProjectApi.WithDbAsync(app, db => db.LlmCredentials.AsNoTracking().SingleAsync(c => c.Id == created.Id, Ct))).Label);
    }

    [Fact]
    public async Task FR78_Connection_mutations_need_a_browser_session_API_tokens_get_FORBIDDEN()
    {
        var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        var created = await LlmStubs.CreateAsync(user.Client, "anthropic", AnthropicKey);
        var tokenResponse = await user.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = await ProjectApi.ReadAsync<CreatedApiTokenDto>(tokenResponse, HttpStatusCode.Created);
        using var cli = ApiSession.BearerClient(app, token.Token);
        var path = $"/api/me/connections/llm/{created.Id}";

        await Problems.AssertAsync(await cli.PostAsJsonAsync("/api/me/connections/llm", new { provider = "openai", label = "x", apiKey = OpenAiKey }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.PatchAsJsonAsync(path, new { label = "x" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.PostAsync(new Uri($"{path}/test", UriKind.Relative), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await cli.DeleteAsync(new Uri(path, UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // Reading (no secret) is allowed with a token, e.g. for the CLI.
        var list = await ProjectApi.ReadAsync<LlmConnectionsDto>(await cli.GetAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct));
        Assert.Equal(("Work", 1), (Assert.Single(list.Credentials).Label, _anthropic.Requests.Count));
        Assert.Empty(_openai.Requests);
    }

    [Fact]
    public async Task FR78_Ollama_and_Fake_are_always_listed_as_available_without_a_key()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var list = await ProjectApi.ReadAsync<LlmConnectionsDto>(await user.Client.GetAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct));

        Assert.Empty(list.Credentials);
        Assert.Equal([LlmProvider.Ollama, LlmProvider.Fake], list.KeylessProviders);
    }

    [Fact]
    public async Task FR78_Anonymous_callers_are_401()
    {
        using var anonymous = factory.CreateClient();

        await Problems.AssertAsync(await anonymous.GetAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task FR78_Admins_see_the_system_key_policy_read_only_and_never_a_key()
    {
        await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Llm:Providers:OpenAI:ApiKey", "sk-system-openai-policy"));
        using var admin = await ApiSession.AdminAsync(app);
        using var user = await ApiSession.RegisterAsync(app);

        var response = await admin.Client.GetAsync(new Uri("/api/admin/integrations/llm", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var policy = await ProjectApi.ReadAsync<AdminLlmPolicyDto>(response);

        Assert.Equal((false, LlmProvider.Openai), (policy.AllowSystemKeys, Assert.Single(policy.SystemKeyProviders)));
        Assert.DoesNotContain("sk-system-openai-policy", text, StringComparison.Ordinal);
        await Problems.AssertAsync(await user.Client.GetAsync(new Uri("/api/admin/integrations/llm", UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    public void Dispose()
    {
        _anthropic.Dispose();
        _openai.Dispose();
    }

    private WebApplicationFactory<Program> App() => LlmStubs.App(factory, _anthropic, _openai);

    private static Task<int> CountAsync(WebApplicationFactory<Program> app, Guid userId) =>
        ProjectApi.WithDbAsync(app, db => db.LlmCredentials.CountAsync(c => c.UserId == userId, Ct));

    private static async Task AssertStoredEncryptedAsync(WebApplicationFactory<Program> app, Guid id, string key)
    {
        var stored = await ProjectApi.WithDbAsync(app, db => db.LlmCredentials.AsNoTracking().SingleAsync(c => c.Id == id, Ct));
        Assert.DoesNotContain(key, stored.SecretEncrypted, StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal(key, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(stored.SecretEncrypted));
    }
}
