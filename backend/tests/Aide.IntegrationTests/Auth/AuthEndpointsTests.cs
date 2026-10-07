using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Auth;
using Aide.Domain.Access;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Auth;

/// <summary>FR-1 register / sign in / sign out / me, §6 auth rows, §8 NFR-SEC cookie settings and logging.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuthEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR1_Register_creates_the_account_signs_in_and_me_returns_it()
    {
        using var session = new ApiSession(factory);
        var email = ApiSession.NewEmail("a");

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email, name = "Ada", password = AideApiFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/auth/me", response.Headers.Location?.OriginalString);
        var created = await response.Content.ReadFromJsonAsync<UserDto>(ApiSession.Json, Ct);
        Assert.NotNull(created);
        Assert.Equal(email, created.Email);
        Assert.Equal("Ada", created.Name);
        Assert.Equal(SystemRole.User, created.SystemRole); // the bootstrap admin already exists
        Assert.False(created.GithubLinked);

        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(created, me);
    }

    [Fact]
    public async Task FR1_Register_returns_enum_values_in_snake_case()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = ApiSession.NewEmail(), name = "Enum", password = AideApiFactory.Password }, Ct);

        Assert.Contains("\"systemRole\":\"user\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR1_Register_with_an_existing_email_returns_409_EMAIL_TAKEN_case_insensitively()
    {
        var email = ApiSession.NewEmail("taken");
        using var first = await ApiSession.RegisterAsync(factory, email);
        using var second = new ApiSession(factory);

        var response = await second.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = email.ToUpperInvariant(), name = "Dup", password = AideApiFactory.Password }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, "EMAIL_TAKEN");
    }

    [Theory]
    [InlineData("not-an-email", "Name", "Passw0rd-ok", "Email")]
    [InlineData("valid@example.com", "", "Passw0rd-ok", "Name")]
    [InlineData("valid@example.com", "Name", "short1", "Password")]
    public async Task FR1_Register_with_invalid_fields_returns_400_VALIDATION_with_errors(string email, string name, string password, string field)
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync("/api/auth/register", new { email, name, password }, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
        Assert.True(body.GetProperty("errors").EnumerateObject().Any(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase)),
            body.ToString());
    }

    [Theory]
    [InlineData("onlyletters")]
    [InlineData("12345678")]
    public async Task FR1_Register_requires_a_letter_and_a_digit(string password)
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = ApiSession.NewEmail(), name = "Weak", password }, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out _), body.ToString());
    }

    [Fact]
    public async Task FR1_Register_with_malformed_json_returns_400_VALIDATION()
    {
        using var session = new ApiSession(factory);
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await session.Client.PostAsync(new Uri("/api/auth/register", UriKind.Relative), content, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    [Fact]
    public async Task FR1_Login_with_valid_credentials_returns_204_and_signs_in()
    {
        var email = ApiSession.NewEmail("login");
        using (await ApiSession.RegisterAsync(factory, email))
        {
        }

        using var session = new ApiSession(factory);
        var response = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = AideApiFactory.Password }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        Assert.Equal(email, me?.Email);
    }

    [Fact]
    public async Task FR1_Login_wrong_password_and_unknown_email_both_return_401_INVALID_CREDENTIALS()
    {
        var email = ApiSession.NewEmail("wrong");
        using (await ApiSession.RegisterAsync(factory, email))
        {
        }

        using var session = new ApiSession(factory);
        var wrongPassword = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong-pass1" }, Ct);
        var unknownEmail = await session.Client.PostAsJsonAsync(
            "/api/auth/login", new { email = ApiSession.NewEmail("nobody"), password = "Wrong-pass1" }, Ct);

        var a = await Problems.AssertAsync(wrongPassword, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        var b = await Problems.AssertAsync(unknownEmail, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task NFR_SEC_Login_locks_out_after_10_failures_for_5_minutes()
    {
        var email = ApiSession.NewEmail("lockout");
        using (await ApiSession.RegisterAsync(factory, email))
        {
        }

        using var session = new ApiSession(factory);
        for (var i = 0; i < 10; i++)
        {
            var failed = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong-pass1" }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var correct = await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = AideApiFactory.Password }, Ct);

        await Problems.AssertAsync(correct, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task FR1_Logout_ends_the_session()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var response = await session.Client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var me = await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        await Problems.AssertAsync(me, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task FR1_Logout_when_anonymous_returns_204()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task FR1_Me_when_anonymous_returns_401_UNAUTHENTICATED_without_redirect()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task NFR_SEC_Auth_cookie_is_HttpOnly_Secure_SameSite_Lax_and_persistent()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = ApiSession.NewEmail(), name = "Cookie", password = AideApiFactory.Password }, Ct);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("aide.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Providers_reports_github_disabled_when_not_configured()
    {
        using var session = new ApiSession(factory);

        var providers = await session.Client.GetFromJsonAsync<AuthProvidersDto>("/api/auth/providers", ApiSession.Json, Ct);

        Assert.False(providers?.Github);
    }

    [Fact]
    public async Task NFR_SEC_Passwords_are_never_logged()
    {
        const string password = "Unl0ggable-secret-pw";
        var email = ApiSession.NewEmail("nolog");
        using (await ApiSession.RegisterAsync(factory, email, password))
        {
        }

        using var session = new ApiSession(factory);
        await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);
        await session.Client.PostAsJsonAsync("/api/auth/login", new { email, password = password + "x" }, Ct);

        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NFR_OBS_Logs_written_for_a_signed_in_user_carry_the_user_id()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "scope-check" }, Ct);

        var userId = session.User!.Id;
        Assert.Contains(factory.Logs.Entries, e =>
            e.Message.StartsWith("API token", StringComparison.Ordinal)
            && e.Scopes.Any(s => s.Key == "UserId" && Equals(s.Value, userId)));
    }

    [Fact]
    public async Task S6_Wrong_method_returns_405_with_VALIDATION()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PutAsync(new Uri("/api/auth/login", UriKind.Relative), null, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.MethodNotAllowed, "VALIDATION");
    }

    [Fact]
    public async Task S6_Non_json_body_returns_415_with_VALIDATION()
    {
        using var session = new ApiSession(factory);
        using var content = new StringContent("email=a&password=b", System.Text.Encoding.UTF8, "text/plain");

        var response = await session.Client.PostAsync(new Uri("/api/auth/login", UriKind.Relative), content, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.UnsupportedMediaType, "VALIDATION");
    }

    [Fact]
    public async Task S6_Unknown_route_returns_404_NOT_FOUND_problem()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }
}
