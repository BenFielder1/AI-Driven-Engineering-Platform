using System.Net;
using System.Net.Http.Json;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Auth;

/// <summary>§8 NFR-SEC: antiforgery on cookie-authenticated mutations; login/register rate limits.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AntiforgeryAndRateLimitTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NFR_SEC_Antiforgery_endpoint_issues_a_readable_XSRF_TOKEN_cookie()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var xsrf = cookies.Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", xsrf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cookies, c => c.StartsWith("aide.af=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NFR_SEC_Cookie_mutation_without_the_XSRF_header_returns_403_ANTIFORGERY()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        session.SendXsrfHeader = false;

        var response = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "ANTIFORGERY");
    }

    [Fact]
    public async Task NFR_SEC_Cookie_mutation_with_a_wrong_XSRF_header_returns_403_ANTIFORGERY()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/tokens") { Content = JsonContent.Create(new { name = "cli" }) };
        request.Headers.Add("X-XSRF-TOKEN", "forged");

        var response = await session.Client.SendAsync(request, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "ANTIFORGERY");
    }

    [Fact]
    public async Task NFR_SEC_Token_issued_to_another_user_is_rejected()
    {
        using var victim = await ApiSession.RegisterAsync(factory);
        using var attacker = await ApiSession.RegisterAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/tokens") { Content = JsonContent.Create(new { name = "cli" }) };
        request.Headers.Add("X-XSRF-TOKEN", attacker.XsrfToken);

        var response = await victim.Client.SendAsync(request, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "ANTIFORGERY");
    }

    [Fact]
    public async Task NFR_SEC_Cookie_mutation_with_the_XSRF_header_succeeds()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_Token_fetched_from_the_antiforgery_endpoint_works_after_sign_in()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        await session.Client.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), Ct);

        var response = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_Login_and_register_need_no_XSRF_header()
    {
        using var session = new ApiSession(factory) { SendXsrfHeader = false };
        var email = ApiSession.NewEmail("noxsrf");

        var register = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email, name = "N", password = AideApiFactory.Password }, Ct);
        var login = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = AideApiFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_Login_is_limited_to_5_per_minute_per_IP_with_429_RATE_LIMITED()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimits:LoginPerMinute", "5"));
        using var session = new ApiSession(limited);

        for (var i = 0; i < 5; i++)
        {
            var allowed = await session.Client.PostAsJsonAsync(
                "/api/auth/login", new { email = "nobody@example.com", password = "Wrong-pass1" }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var blocked = await session.Client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "Wrong-pass1" }, Ct);

        await Problems.AssertAsync(blocked, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(int.Parse(blocked.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 60);
    }

    [Fact]
    public async Task NFR_SEC_Register_is_limited_to_5_per_minute_per_IP_with_429_RATE_LIMITED()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimits:RegisterPerMinute", "5"));
        using var session = new ApiSession(limited);

        for (var i = 0; i < 5; i++)
        {
            var allowed = await session.Client.PostAsJsonAsync("/api/auth/register", new { email = "bad", name = "x", password = "x" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var blocked = await session.Client.PostAsJsonAsync("/api/auth/register", new { email = "bad", name = "x", password = "x" }, Ct);

        await Problems.AssertAsync(blocked, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }
}
