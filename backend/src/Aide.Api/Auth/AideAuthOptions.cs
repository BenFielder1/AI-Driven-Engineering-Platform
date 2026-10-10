using System.ComponentModel.DataAnnotations;

namespace Aide.Api.Auth;

/// <summary>Authentication settings (section <c>Auth</c>). No secrets here.</summary>
public sealed class AideAuthOptions
{
    public const string SectionName = "Auth";

    public CookieSettings Cookie { get; set; } = new();

    public RateLimitSettings RateLimits { get; set; } = new();

    public sealed class CookieSettings
    {
        /// <summary><c>Auth:Cookie:SecurePolicy</c>. Default <c>SameAsRequest</c>, because local Kubernetes serves
        /// plain http://localhost:8080; set <c>Always</c> wherever the app is behind TLS.</summary>
        public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;
    }

    public sealed class RateLimitSettings
    {
        /// <summary>Login attempts per minute per client IP (§8: 5).</summary>
        [Range(1, 100_000)]
        public int LoginPerMinute { get; set; } = 5;

        /// <summary>Registrations per minute per client IP.</summary>
        [Range(1, 100_000)]
        public int RegisterPerMinute { get; set; } = 5;
    }
}

/// <summary>Scheme, cookie, claim and policy names used across the auth setup.</summary>
public static class AuthConstants
{
    /// <summary>Default policy scheme: Bearer API token when an <c>Authorization: Bearer</c> header is present,
    /// otherwise the Identity cookie.</summary>
    public const string SmartScheme = "Aide";

    public const string ApiTokenScheme = "ApiToken";

    public const string GitHubScheme = "GitHub";

    public const string AuthCookieName = "aide.auth";

    public const string ExternalCookieName = "aide.external";

    public const string AntiforgeryCookieName = "aide.af";

    /// <summary>Readable (non-HttpOnly) cookie carrying the antiforgery request token for the SPA.</summary>
    public const string XsrfCookieName = "XSRF-TOKEN";

    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    public const string TokenIdClaim = "aide:token_id";

    public const string AdminPolicy = "Admin";

    /// <summary>Signed in with the browser cookie, not an API token: token management and role changes can't be
    /// done with a (possibly leaked) API token.</summary>
    public const string BrowserSessionPolicy = "BrowserSession";

    public const string LoginRateLimit = "login";

    public const string RegisterRateLimit = "register";
}
