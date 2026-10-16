using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aide.Infrastructure.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Aide.Api.Auth;

/// <summary>
/// D-V2-7, FR-80, FR-81: the GitHub sign-in handler. Once an AIDE GitHub App exists (configured or registered) the flow
/// uses the App's user authorization — the App's client id and secret, no scopes, PKCE (S256) and the protected,
/// correlated <c>state</c> of the built-in OAuth handler — and the token pair is kept for the GitHub connection. Without
/// an App it is the v1 OAuth App flow, unchanged. With neither, the callback path isn't handled (the endpoint answers 404).
/// The App's credentials are read per request, so registering an App needs no restart.
/// </summary>
public sealed class GitHubOAuthHandler(IOptionsMonitor<OAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<OAuthOptions>(options, logger, encoder)
{
    private GitHubAppCredentials? _app;

    public override async Task<bool> ShouldHandleRequestAsync()
    {
        if (!await base.ShouldHandleRequestAsync())
        {
            return false;
        }

        return await Resolver.ModeAsync(Context.RequestAborted) != Domain.GitHub.GitHubSignInMode.None;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _app = await Resolver.AppAsync(Context.RequestAborted);
        if (_app is not null)
        {
            // Bound into the protected state: the callback exchanges the code with the same App.
            properties.Items[GitHubAuth.AppClientItem] = _app.ClientId;
        }

        await base.HandleChallengeAsync(properties);
    }

    protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
    {
        var url = base.BuildChallengeUrl(properties, redirectUri);
        if (_app is null)
        {
            return url;
        }

        // The App's user authorization: its own client id; GitHub Apps ignore scopes (permissions come from the App).
        var uri = new Uri(url);
        var query = QueryHelpers.ParseQuery(uri.Query)
            .Where(p => p.Key is not ("scope" or "client_id"))
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value.ToString()))
            .Prepend(new KeyValuePair<string, string?>("client_id", _app.ClientId));
        return QueryHelpers.AddQueryString(uri.GetLeftPart(UriPartial.Path), query);
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Properties.Items.TryGetValue(GitHubAuth.AppClientItem, out var clientId) || clientId is null)
        {
            return await base.ExchangeCodeAsync(context);
        }

        var app = await Resolver.AppAsync(Context.RequestAborted);
        if (app is null || app.ClientId != clientId)
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("The AIDE GitHub App changed during sign-in."));
        }

        var fields = new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret,
            ["code"] = context.Code,
            ["redirect_uri"] = context.RedirectUri,
            ["grant_type"] = "authorization_code",
        };
        if (context.Properties.Items.TryGetValue(OAuthConstants.CodeVerifierKey, out var verifier) && verifier is not null)
        {
            fields[OAuthConstants.CodeVerifierKey] = verifier;
            context.Properties.Items.Remove(OAuthConstants.CodeVerifierKey);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
        var body = await response.Content.ReadAsStringAsync(Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException($"GitHub's token endpoint answered HTTP {(int)response.StatusCode}."));
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("GitHub's token endpoint returned an unreadable reply."));
        }

        // GitHub reports a bad code as 200 with an "error" field.
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("error", out _))
        {
            json.Dispose();
            return OAuthTokenResponse.Failed(new AuthenticationFailureException("GitHub refused the authorization code."));
        }

        return OAuthTokenResponse.Success(json);
    }

    private GitHubSignInResolver Resolver => Context.RequestServices.GetRequiredService<GitHubSignInResolver>();
}

/// <summary>D-V2-7: how GitHub sign-in works now (App, v1 OAuth App, or none), per request.</summary>
public sealed class GitHubSignInResolver(IGitHubAppStore apps, GitHubAuthState oauth)
{
    public Task<GitHubAppCredentials?> AppAsync(CancellationToken cancellationToken) => apps.GetAsync(cancellationToken);

    public async Task<Domain.GitHub.GitHubSignInMode> ModeAsync(CancellationToken cancellationToken) =>
        Domain.GitHub.GitHubRules.SignInMode(await apps.GetAsync(cancellationToken) is not null, oauth.Enabled);
}
