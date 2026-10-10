using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Api.Features.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// A browser-like client: keeps cookies, and (like the SPA) echoes the <c>XSRF-TOKEN</c> cookie as
/// <c>X-XSRF-TOKEN</c> on unsafe requests unless <see cref="SendXsrfHeader"/> is off. Redirects aren't followed.
/// </summary>
public sealed class ApiSession : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly XsrfHandler _xsrf;

    public ApiSession(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _xsrf = new XsrfHandler();
        Cookies = new CookieContainerHandler();
        Client = factory.CreateDefaultClient(AideApiFactory.BaseUri, _xsrf, Cookies);
    }

    public HttpClient Client { get; }

    public CookieContainerHandler Cookies { get; }

    public bool SendXsrfHeader
    {
        get => _xsrf.Enabled;
        set => _xsrf.Enabled = value;
    }

    public string? XsrfToken => _xsrf.Token;

    public UserDto? User { get; private set; }

    public static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    public static async Task<ApiSession> RegisterAsync(
        WebApplicationFactory<Program> factory, string? email = null, string password = AideApiFactory.Password, string name = "Test User")
    {
        var session = new ApiSession(factory);
        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = email ?? NewEmail(), name, password }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        session.User = await response.Content.ReadFromJsonAsync<UserDto>(Json, Ct);
        return session;
    }

    public static async Task<ApiSession> LoginAsync(
        WebApplicationFactory<Program> factory, string email, string password = AideApiFactory.Password)
    {
        var session = new ApiSession(factory);
        var response = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        session.User = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", Json, Ct);
        return session;
    }

    public static Task<ApiSession> AdminAsync(WebApplicationFactory<Program> factory) => LoginAsync(factory, AideApiFactory.AdminEmail);

    /// <summary>A client with no cookies that sends <c>Authorization: Bearer {token}</c>.</summary>
    public static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateDefaultClient(AideApiFactory.BaseUri);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public void Dispose() => Client.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class XsrfHandler : DelegatingHandler
    {
        public bool Enabled { get; set; } = true;

        public string? Token { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Enabled && Token is not null && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
                && !request.Headers.Contains("X-XSRF-TOKEN"))
            {
                request.Headers.Add("X-XSRF-TOKEN", Token);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    if (cookie.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
                    {
                        Token = Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
                    }
                }
            }

            return response;
        }
    }
}
