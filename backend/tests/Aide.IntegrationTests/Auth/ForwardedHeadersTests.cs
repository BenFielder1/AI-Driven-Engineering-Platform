using System.Net;
using System.Net.Http.Json;
using Aide.Domain.Common;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Auth;

/// <summary>
/// FR-105: client IP, scheme and host behind one or two proxies. The forwarded-headers middleware is switched on the
/// way <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> does it (a startup filter, first in the pipeline); the options it
/// runs with are the app's own (<c>ForwardedHeaders:ForwardLimit</c>, <c>ForwardedHeaders:KnownNetworks</c>).
/// The client IP is observed through the login and register rate limits (1 per minute per IP here).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ForwardedHeadersTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR105_ForwardLimit_2_rate_limits_login_by_the_client_IP_left_of_the_proxy()
    {
        await using var app = Behind(forwardLimit: 2);
        using var client = app.CreateDefaultClient(AideApiFactory.BaseUri);

        var first = await LoginAsync(client, "203.0.113.9, 10.0.0.5");
        var sameClientOtherProxy = await LoginAsync(client, "203.0.113.9, 10.0.0.6");
        var otherClient = await LoginAsync(client, "203.0.113.10, 10.0.0.5");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        await Problems.AssertAsync(sameClientOtherProxy, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    [Fact]
    public async Task FR105_ForwardLimit_2_rate_limits_register_by_the_client_IP_left_of_the_proxy()
    {
        await using var app = Behind(forwardLimit: 2);
        using var client = app.CreateDefaultClient(AideApiFactory.BaseUri);

        var first = await RegisterAsync(client, "203.0.113.9, 10.0.0.5");
        var sameClientOtherProxy = await RegisterAsync(client, "203.0.113.9, 10.0.0.6");
        var otherClient = await RegisterAsync(client, "203.0.113.10, 10.0.0.5");

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        await Problems.AssertAsync(sameClientOtherProxy, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal(HttpStatusCode.BadRequest, otherClient.StatusCode);
    }

    [Fact]
    public async Task FR105_Default_ForwardLimit_1_uses_the_right_most_entry_as_in_v2()
    {
        await using var app = Behind(forwardLimit: null);
        using var client = app.CreateDefaultClient(AideApiFactory.BaseUri);

        var first = await LoginAsync(client, "203.0.113.9, 10.0.0.5");
        var otherLeftSameRight = await LoginAsync(client, "198.51.100.1, 10.0.0.5");
        var sameLeftOtherRight = await LoginAsync(client, "203.0.113.9, 10.0.0.6");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        await Problems.AssertAsync(otherLeftSameRight, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal(HttpStatusCode.Unauthorized, sameLeftOtherRight.StatusCode);
    }

    [Fact]
    public async Task FR105_A_client_supplied_entry_beyond_the_limit_is_not_trusted()
    {
        await using var app = Behind(forwardLimit: 2);
        using var client = app.CreateDefaultClient(AideApiFactory.BaseUri);

        // The client sends X-Forwarded-For: 1.2.3.4; the edge appends the real address, the web proxy its own.
        var first = await LoginAsync(client, "1.2.3.4, 203.0.113.9, 10.0.0.5");
        var otherSpoof = await LoginAsync(client, "5.6.7.8, 203.0.113.9, 10.0.0.5");
        var sameSpoofOtherClient = await LoginAsync(client, "1.2.3.4, 203.0.113.10, 10.0.0.5");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        await Problems.AssertAsync(otherSpoof, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal(HttpStatusCode.Unauthorized, sameSpoofOtherClient.StatusCode);
    }

    [Fact]
    public async Task FR105_KnownNetworks_stop_at_the_first_hop_outside_them()
    {
        // Loopback too, whatever address the test server reports for the connection.
        await using var app = Behind(forwardLimit: 2, knownNetworks: ["10.0.0.0/8", "127.0.0.0/8", "::1/128"]);
        using var client = app.CreateDefaultClient(AideApiFactory.BaseUri);

        // Proxy inside the known network: the client entry left of it is trusted.
        var trusted = await LoginAsync(client, "203.0.113.9, 10.0.0.5");
        var trustedOtherClient = await LoginAsync(client, "198.51.100.1, 10.0.0.5");
        // Hop outside it: resolution stops there, so 192.0.2.1 is the client whatever is to its left.
        var untrusted = await LoginAsync(client, "203.0.113.20, 192.0.2.1");
        var untrustedOtherLeft = await LoginAsync(client, "198.51.100.77, 192.0.2.1");

        Assert.Equal(HttpStatusCode.Unauthorized, trusted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, trustedOtherClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, untrusted.StatusCode);
        await Problems.AssertAsync(untrustedOtherLeft, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task FR105_X_Forwarded_Proto_https_makes_SameAsRequest_cookies_Secure()
    {
        await using var app = Behind(forwardLimit: 1);
        using var client = app.CreateDefaultClient(new Uri("http://localhost"));

        using var plain = new HttpRequestMessage(HttpMethod.Get, "/api/auth/antiforgery");
        plain.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        var overHttp = await client.SendAsync(plain, Ct);
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/api/auth/antiforgery");
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        var overHttps = await client.SendAsync(forwarded, Ct);

        Assert.Equal(HttpStatusCode.NoContent, overHttp.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, overHttps.StatusCode);
        Assert.DoesNotContain("secure", AntiforgeryCookie(overHttp), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", AntiforgeryCookie(overHttps), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR105_FR46_The_forwarded_host_is_the_apps_own_origin_for_live_updates()
    {
        await using var app = Behind(forwardLimit: 2);
        using var session = await ApiSession.RegisterAsync(app);

        // Two hops: the right-most two X-Forwarded-Host entries are applied, so the browser's host wins. Only the
        // middleware resolves a list (the origin check's raw-header fallback refuses one), so this proves it ran.
        var own = await NegotiateAsync(session.Client, "https://aide.example.com", "aide.example.com, web.internal:8080");
        var foreign = await NegotiateAsync(session.Client, "https://evil.example.com", "aide.example.com, web.internal:8080");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        await Problems.AssertAsync(foreign, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    /// <summary>The app behind proxies: forwarded headers on, <paramref name="forwardLimit"/> (null: the default),
    /// and auth rate limits of 1 per minute per client IP.</summary>
    private WebApplicationFactory<Program> Behind(int? forwardLimit, IReadOnlyList<string>? knownNetworks = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (forwardLimit is { } limit)
            {
                b.UseSetting("ForwardedHeaders:ForwardLimit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            for (var i = 0; i < (knownNetworks?.Count ?? 0); i++)
            {
                b.UseSetting($"ForwardedHeaders:KnownNetworks:{i}", knownNetworks![i]);
            }

            b.UseSetting("Auth:RateLimits:LoginPerMinute", "1");
            b.UseSetting("Auth:RateLimits:RegisterPerMinute", "1");
            b.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ForwardedHeadersSwitch>());
        });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@example.com", password = "Wrong-pass1" }),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { email = "bad", name = "x", password = "x" }),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client, string origin, string forwardedHost)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/hubs/projects/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9, 10.0.0.5");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
        return client.SendAsync(request, Ct);
    }

    private static string AntiforgeryCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("aide.af=", StringComparison.Ordinal));

    /// <summary>What <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> adds: the middleware, first in the pipeline.</summary>
    private sealed class ForwardedHeadersSwitch : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseForwardedHeaders();
            next(app);
        };
    }
}
