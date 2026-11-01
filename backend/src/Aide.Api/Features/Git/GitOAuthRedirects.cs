using Aide.Api.Auth;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;

namespace Aide.Api.Features.Git;

/// <summary>
/// FR-131, FR-134: where a Git host OAuth flow sends the browser, and its cookies. Failures carry
/// <c>error=GIT_AUTH_FAILED&amp;reason=…&amp;provider=gitlab|bitbucket</c>: connect flows return to their page, sign-in (or an
/// unknown flow) to the login page. A verified email that matches an unlinked account goes to
/// <c>/login?link=&lt;provider&gt;</c>.
/// </summary>
public static class GitOAuthRedirects
{
    public const string LoginPath = "/login";

    /// <summary>The failure redirect for a flow started in <paramref name="mode"/> returning to <paramref name="returnUrl"/>.</summary>
    public static string FailureUrl(string provider, GitOAuthMode mode, string returnUrl, string reason)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        var query = $"error={ErrorCodes.GitAuthFailed}&reason={Uri.EscapeDataString(reason)}&provider={provider}";
        if (mode == GitOAuthMode.Connect)
        {
            var separator = returnUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{returnUrl}{separator}{query}";
        }

        return $"{LoginPath}?{query}";
    }

    /// <summary>D-V4-18: the confirm step for a sign-in whose verified email belongs to an unlinked account.</summary>
    public static string LinkUrl(string provider, string returnUrl) => $"{LoginPath}?link={provider}&returnUrl={Uri.EscapeDataString(returnUrl)}";

    /// <summary>An HttpOnly, Lax cookie on <paramref name="path"/> (the host → callback hop is a cross-site top-level GET).</summary>
    public static CookieOptions Cookie(HttpContext http, AideAuthOptions authOptions, string path, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(authOptions);
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = path,
            IsEssential = true,
            MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
            Secure = authOptions.Cookie.SecurePolicy switch
            {
                CookieSecurePolicy.Always => true,
                CookieSecurePolicy.None => false,
                _ => http.Request.IsHttps,
            },
        };
    }
}
