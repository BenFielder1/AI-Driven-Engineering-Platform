using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Tokens;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Auth;

/// <summary>FR-4: personal API tokens and Bearer authentication.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ApiTokenTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR4_Create_returns_the_plain_token_once_and_stores_only_its_hash()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        var response = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = " laptop ", expiresAt }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedApiTokenDto>(ApiSession.Json, Ct);
        Assert.NotNull(created);
        Assert.Equal("laptop", created.Name);
        Assert.Matches("^aide_pat_[A-Za-z0-9_-]{43}$", created.Token);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), created.ExpiresAt?.ToUnixTimeMilliseconds());

        var stored = await WithDbAsync(db => db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == created.Id, Ct));
        Assert.Equal(ApiTokenRules.Hash(created.Token), stored.TokenHash);
        Assert.NotEqual(created.Token, stored.TokenHash);

        var listJson = await session.Client.GetStringAsync(new Uri("/api/me/tokens", UriKind.Relative), Ct);
        Assert.DoesNotContain(created.Token, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.TokenHash, listJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR4_List_returns_only_the_callers_tokens_newest_first()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        using var other = await ApiSession.RegisterAsync(factory);
        await CreateTokenAsync(owner, "first");
        await CreateTokenAsync(owner, "second");
        await CreateTokenAsync(other, "not-mine");

        var tokens = await owner.Client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens", ApiSession.Json, Ct);

        Assert.Equal(["second", "first"], tokens!.Select(t => t.Name));
        Assert.All(tokens!, t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task FR4_Bearer_token_authenticates_as_its_owner_and_records_use()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var me = await cli.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);

        Assert.Equal(owner.User!.Id, me?.Id);
        var stored = await WithDbAsync(db => db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id, Ct));
        Assert.NotNull(stored.LastUsedAt);
    }

    [Fact]
    public async Task FR4_Last_used_is_written_at_most_once_a_minute()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);
        await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        var first = (await WithDbAsync(db => db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id, Ct))).LastUsedAt;

        await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        var second = (await WithDbAsync(db => db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id, Ct))).LastUsedAt;
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task FR4_Revoked_token_returns_401()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");

        var revoke = await owner.Client.DeleteAsync(new Uri($"/api/me/tokens/{token.Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var cli = ApiSession.BearerClient(factory, token.Token);
        var response = await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);

        var listed = await owner.Client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens", ApiSession.Json, Ct);
        Assert.NotNull(listed!.Single(t => t.Id == token.Id).RevokedAt);
    }

    [Fact]
    public async Task FR4_Revoking_twice_is_idempotent()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");

        await owner.Client.DeleteAsync(new Uri($"/api/me/tokens/{token.Id}", UriKind.Relative), Ct);
        var again = await owner.Client.DeleteAsync(new Uri($"/api/me/tokens/{token.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task FR4_Expired_token_returns_401()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var plain = ApiTokenRules.Format(RandomNumberGenerator.GetBytes(ApiTokenRules.SecretBytes));
        await WithDbAsync(async db =>
        {
            db.ApiTokens.Add(new ApiToken
            {
                UserId = owner.User!.Id,
                Name = "old",
                TokenHash = ApiTokenRules.Hash(plain),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            return await db.SaveChangesAsync(Ct);
        });
        using var cli = ApiSession.BearerClient(factory, plain);

        var response = await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData("aide_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task FR4_Unknown_or_malformed_token_returns_401(string token)
    {
        using var cli = factory.CreateDefaultClient(AideApiFactory.BaseUri);
        cli.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var response = await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task FR4_Bearer_requests_need_no_XSRF_header()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task FR4_Api_token_cannot_create_tokens()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.PostAsJsonAsync("/api/me/tokens", new { name = "minted" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        var tokens = await owner.Client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens", ApiSession.Json, Ct);
        Assert.DoesNotContain(tokens!, t => t.Name == "minted");
    }

    [Fact]
    public async Task FR4_Api_token_cannot_revoke_tokens()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        var other = await CreateTokenAsync(owner, "other");
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.DeleteAsync(new Uri($"/api/me/tokens/{other.Id}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        var tokens = await owner.Client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens", ApiSession.Json, Ct);
        Assert.Null(tokens!.Single(t => t.Id == other.Id).RevokedAt);
    }

    [Fact]
    public async Task FR4_Api_token_cannot_list_tokens()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.GetAsync(new Uri("/api/me/tokens", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task FR4_Bearer_header_wins_over_a_cookie_session()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer aide_pat_invalid");

        var response = await session.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FR4_Revoking_another_users_token_returns_404_NOT_FOUND()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        using var other = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "mine");

        var response = await other.Client.DeleteAsync(new Uri($"/api/me/tokens/{token.Id}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        using var cli = ApiSession.BearerClient(factory, token.Token);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR4_Revoking_an_unknown_token_returns_404_NOT_FOUND()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.DeleteAsync(new Uri($"/api/me/tokens/{Guid.NewGuid()}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task FR4_Create_with_a_past_expiry_returns_400_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/me/tokens", new { name = "old", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
        Assert.True(body.GetProperty("errors").TryGetProperty("expiresAt", out _), body.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FR4_Create_without_a_name_returns_400_VALIDATION(string name)
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsJsonAsync("/api/me/tokens", new { name }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    [Fact]
    public async Task FR4_Create_with_a_name_over_100_characters_returns_400_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsJsonAsync("/api/me/tokens", new { name = new string('n', 101) }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    [Theory]
    [InlineData("GET", "/api/me/tokens")]
    [InlineData("POST", "/api/me/tokens")]
    [InlineData("DELETE", "/api/me/tokens/0198e0a4-0000-7000-8000-000000000000")]
    public async Task FR4_Token_routes_require_sign_in(string method, string path)
    {
        using var session = new ApiSession(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { name = "x" });
        }

        var response = await session.Client.SendAsync(request, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task NFR_SEC_Plain_tokens_are_never_logged()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var token = await CreateTokenAsync(owner, "cli");
        using var cli = ApiSession.BearerClient(factory, token.Token);
        await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        await owner.Client.DeleteAsync(new Uri($"/api/me/tokens/{token.Id}", UriKind.Relative), Ct);
        await cli.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(token.Token, StringComparison.Ordinal));
    }

    private static async Task<CreatedApiTokenDto> CreateTokenAsync(ApiSession session, string name)
    {
        var response = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedApiTokenDto>(ApiSession.Json, Ct))!;
    }

    private async Task<T> WithDbAsync<T>(Func<AideDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AideDbContext>());
    }
}
