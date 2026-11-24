using System.Net;
using Tevrin.Api.Auth;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Auth;

/// <summary>
/// FR-326 (§4.77): a browser signed in before 0.8.0 holds <c>aide.auth</c> (and <c>aide.af</c>). It stays signed in, and
/// is moved onto <c>tevrin.auth</c> on its first request.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr326LegacyCookieTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR326_A_browser_holding_only_aide_auth_is_signed_in_and_moved_to_tevrin_auth()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var ticket = CookieValue(session, AuthConstants.AuthCookieName);
        using var browser = factory.CreateDefaultClient(TevrinApiFactory.BaseUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation("Cookie", $"{AuthConstants.LegacyAuthCookieName}={ticket}");

        var response = await browser.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith("tevrin.auth=", StringComparison.Ordinal) && !c.StartsWith("tevrin.auth=;", StringComparison.Ordinal));
        Assert.Contains(setCookies, c => c.StartsWith("aide.auth=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FR326_The_reissued_tevrin_auth_cookie_signs_in_on_its_own()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var ticket = CookieValue(session, AuthConstants.AuthCookieName);
        using var browser = factory.CreateDefaultClient(TevrinApiFactory.BaseUri);
        using var first = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        first.Headers.TryAddWithoutValidation("Cookie", $"{AuthConstants.LegacyAuthCookieName}={ticket}");
        var moved = (await browser.SendAsync(first, Ct)).Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("tevrin.auth=", StringComparison.Ordinal)).Split(';')[0];
        using var second = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        second.Headers.TryAddWithoutValidation("Cookie", moved);

        var response = await browser.SendAsync(second, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FR326_An_in_flight_form_with_the_aide_af_cookie_passes_antiforgery()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var ticket = CookieValue(session, AuthConstants.AuthCookieName);
        var cookieToken = CookieValue(session, AuthConstants.AntiforgeryCookieName);
        using var browser = factory.CreateDefaultClient(TevrinApiFactory.BaseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.TryAddWithoutValidation(
            "Cookie", $"{AuthConstants.LegacyAuthCookieName}={ticket}; {AuthConstants.LegacyAntiforgeryCookieName}={cookieToken}");
        request.Headers.TryAddWithoutValidation(AuthConstants.XsrfHeaderName, session.XsrfToken);

        var response = await browser.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task FR326_An_unreadable_aide_auth_cookie_is_expired_and_the_request_is_anonymous()
    {
        using var browser = factory.CreateDefaultClient(TevrinApiFactory.BaseUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation("Cookie", $"{AuthConstants.LegacyAuthCookieName}=not-a-ticket");

        var response = await browser.SendAsync(request, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("aide.auth=;", StringComparison.Ordinal));
    }

    private static string CookieValue(ApiSession session, string name) =>
        session.Cookies.Container.GetCookies(TevrinApiFactory.BaseUri)[name]?.Value
            ?? throw new InvalidOperationException($"The session has no {name} cookie.");
}
