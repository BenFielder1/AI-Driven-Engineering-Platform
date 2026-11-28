using System.ComponentModel.DataAnnotations;

namespace Tevrin.Api.Auth;

/// <summary>Authentication settings (section <c>Auth</c>). No secrets here.</summary>
public sealed class TevrinAuthOptions
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

        /// <summary>FR-337: pipeline webhook calls per minute per client IP (the anonymous hook route).</summary>
        [Range(1, 100_000)]
        public int WebhookPerMinute { get; set; } = 120;
    }
}

/// <summary>Scheme, cookie, claim and policy names used across the auth setup.</summary>
public static class AuthConstants
{
    /// <summary>Default policy scheme: Bearer API token when an <c>Authorization: Bearer</c> header is present,
    /// otherwise the Identity cookie.</summary>
    public const string SmartScheme = "Tevrin";

    public const string ApiTokenScheme = "ApiToken";

    public const string GitHubScheme = "GitHub";

    /// <summary>FR-326: the session cookie. A browser holding only <see cref="LegacyAuthCookieName"/> is signed in and
    /// moved onto this name (<see cref="LegacyCookies"/>).</summary>
    public const string AuthCookieName = "tevrin.auth";

    public const string ExternalCookieName = "tevrin.external";

    public const string AntiforgeryCookieName = "tevrin.af";

    /// <summary>FR-326 (§4.77): the pre-0.8.0 cookie names, read until 0.9.0 and expired when read.</summary>
    public const string LegacyAuthCookieName = "aide.auth";

    public const string LegacyExternalCookieName = "aide.external";

    public const string LegacyAntiforgeryCookieName = "aide.af";

    /// <summary>Readable (non-HttpOnly) cookie carrying the antiforgery request token for the SPA.</summary>
    public const string XsrfCookieName = "XSRF-TOKEN";

    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>Marks an API-token principal. Built per request by <see cref="ApiTokenAuthenticationHandler"/> and never
    /// stored in a cookie, so the 0.8.0 rename (FR-326) needs no fallback for the old <c>aide:token_id</c>.</summary>
    public const string TokenIdClaim = "tevrin:token_id";

    public const string AdminPolicy = "Admin";

    /// <summary>Signed in with the browser cookie, not an API token: token management and role changes can't be
    /// done with a (possibly leaked) API token.</summary>
    public const string BrowserSessionPolicy = "BrowserSession";

    public const string LoginRateLimit = "login";

    public const string RegisterRateLimit = "register";

    /// <summary>FR-337: the anonymous pipeline webhook route.</summary>
    public const string WebhookRateLimit = "pipeline-webhook";
}
