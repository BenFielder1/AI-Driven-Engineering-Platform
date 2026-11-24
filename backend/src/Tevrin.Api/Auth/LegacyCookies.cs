using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Tevrin.Api.Features.Bitbucket;
using Tevrin.Api.Features.GitLab;

namespace Tevrin.Api.Auth;

/// <summary>How a cookie read under its pre-0.8.0 name is moved onto its new name (FR-326).</summary>
public enum LegacyCookieReissue
{
    /// <summary>An authentication cookie: re-issued by signing in again with the same principal and properties, so its
    /// expiry and persistence carry over. The legacy cookie is expired.</summary>
    SignIn,

    /// <summary>A session cookie with fixed options (antiforgery): its value is copied to the new name. The legacy
    /// cookie is expired.</summary>
    Copy,

    /// <summary>A short-lived, path-scoped flow cookie (Git host OAuth state, pending link): read under the new name for
    /// this request only. Its own endpoints write and delete the new name, and the legacy one lapses on its own.</summary>
    RequestOnly,
}

/// <summary>One renamed cookie: the pre-0.8.0 name, the new name, and how it is moved.</summary>
public sealed record LegacyCookieRename(string Legacy, string Current, LegacyCookieReissue Reissue, string? Scheme = null);

/// <summary>A rename that applies to a request: the legacy cookie names it carries (chunks included) and the copies
/// added to the request under the new name.</summary>
public sealed record LegacyCookieMove(LegacyCookieRename Rename, IReadOnlyList<string> LegacyNames, IReadOnlyList<KeyValuePair<string, string>> Copies);

/// <summary>
/// FR-326 (§4.77): the cookies renamed from <c>aide.*</c> to <c>tevrin.*</c> in 0.8.0, read under their old names
/// until 0.9.0 so nobody is signed out. ASP.NET Core's handlers read one cookie name, so this middleware runs before
/// authentication: when a request carries only the legacy cookie (or its chunks <c>aide.authC1…</c>), it adds the same
/// values to the request under the new name, then moves the browser over (<see cref="LegacyCookieReissue"/>).
/// Ticket and antiforgery protection don't depend on the cookie name (the data-protection application name and the
/// scheme names are unchanged, D-0.8.0-16), so the old values decrypt under the new name.
/// </summary>
public static class LegacyCookies
{
    public static IReadOnlyList<LegacyCookieRename> Renames { get; } =
    [
        new(AuthConstants.LegacyAuthCookieName, AuthConstants.AuthCookieName, LegacyCookieReissue.SignIn, IdentityConstants.ApplicationScheme),
        new(AuthConstants.LegacyExternalCookieName, AuthConstants.ExternalCookieName, LegacyCookieReissue.SignIn, IdentityConstants.ExternalScheme),

        // An in-flight form keeps working: its X-XSRF-TOKEN still matches the copied cookie token.
        new(AuthConstants.LegacyAntiforgeryCookieName, AuthConstants.AntiforgeryCookieName, LegacyCookieReissue.Copy),
        new(BitbucketOAuthStates.LegacyCookieName, BitbucketOAuthStates.CookieName, LegacyCookieReissue.RequestOnly),
        new(BitbucketPendingLinks.LegacyCookieName, BitbucketPendingLinks.CookieName, LegacyCookieReissue.RequestOnly),
        new(GitLabOAuthStates.LegacyCookieName, GitLabOAuthStates.CookieName, LegacyCookieReissue.RequestOnly),
        new(GitLabPendingLinks.LegacyCookieName, GitLabPendingLinks.CookieName, LegacyCookieReissue.RequestOnly),
    ];

    /// <summary>
    /// Pure: the renames that apply to a raw <c>Cookie</c> header, and the header with the copies appended. A rename
    /// applies when the header has the legacy cookie (or a chunk of it) and neither the new cookie nor a chunk of it.
    /// Values are copied raw, so encoding is unchanged.
    /// </summary>
    public static (string Header, IReadOnlyList<LegacyCookieMove> Moves) Plan(string? cookieHeader, IReadOnlyList<LegacyCookieRename> renames)
    {
        ArgumentNullException.ThrowIfNull(renames);
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return (cookieHeader ?? string.Empty, []);
        }

        var cookies = Parse(cookieHeader);
        var moves = new List<LegacyCookieMove>();
        foreach (var rename in renames)
        {
            if (cookies.Any(c => IsNameOrChunk(c.Key, rename.Current)))
            {
                continue;
            }

            var legacy = cookies.Where(c => IsNameOrChunk(c.Key, rename.Legacy)).ToList();
            if (legacy.Count == 0)
            {
                continue;
            }

            moves.Add(new LegacyCookieMove(
                rename,
                [.. legacy.Select(c => c.Key).Distinct(StringComparer.Ordinal)],
                [.. legacy.Select(c => KeyValuePair.Create(rename.Current + c.Key[rename.Legacy.Length..], c.Value))]));
        }

        if (moves.Count == 0)
        {
            return (cookieHeader, []);
        }

        var copies = moves.SelectMany(m => m.Copies).Select(c => $"{c.Key}={c.Value}");
        return (cookieHeader.TrimEnd().TrimEnd(';') + "; " + string.Join("; ", copies), moves);
    }

    /// <summary>
    /// Runs before <c>UseAuthentication</c>: rewrites the request's cookies for each applicable rename, re-issues
    /// authentication cookies under the new name, copies the antiforgery cookie, and expires the legacy cookies.
    /// </summary>
    public static IApplicationBuilder UseLegacyCookieNames(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(MoveAsync);
    }

    private static async Task MoveAsync(HttpContext context, RequestDelegate next)
    {
        var (header, moves) = Plan(context.Request.Headers.Cookie.ToString(), Renames);
        if (moves.Count == 0)
        {
            await next(context);
            return;
        }

        context.Request.Headers.Cookie = header;
        foreach (var move in moves)
        {
            await ReissueAsync(context, move);
        }

        await next(context);
    }

    private static async Task ReissueAsync(HttpContext context, LegacyCookieMove move)
    {
        var rename = move.Rename;
        switch (rename.Reissue)
        {
            case LegacyCookieReissue.SignIn:
                var result = await context.AuthenticateAsync(rename.Scheme);
                if (result.Succeeded)
                {
                    await context.SignInAsync(rename.Scheme, result.Principal, result.Properties);
                }

                break;
            case LegacyCookieReissue.Copy:
                if (context.Request.Cookies[rename.Current] is { } value)
                {
                    var options = context.RequestServices.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
                    context.Response.Cookies.Append(rename.Current, value, options.Cookie.Build(context));
                }

                break;
            default:
                return;
        }

        foreach (var name in move.LegacyNames)
        {
            context.Response.Cookies.Delete(name, new CookieOptions { Path = "/", Secure = context.Request.IsHttps, HttpOnly = true });
        }
    }

    private static List<KeyValuePair<string, string>> Parse(string header)
    {
        var cookies = new List<KeyValuePair<string, string>>();
        foreach (var segment in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = segment.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                cookies.Add(KeyValuePair.Create(segment[..equals].Trim(), segment[(equals + 1)..].Trim()));
            }
        }

        return cookies;
    }

    /// <summary>The cookie itself, or one of its chunks (<c>&lt;name&gt;C1</c>, <c>&lt;name&gt;C2</c>, …) written by
    /// ASP.NET Core's chunking cookie manager.</summary>
    private static bool IsNameOrChunk(string cookie, string name) =>
        string.Equals(cookie, name, StringComparison.Ordinal)
        || (cookie.Length > name.Length + 1
            && cookie.StartsWith(name, StringComparison.Ordinal)
            && cookie[name.Length] == 'C'
            && cookie.AsSpan(name.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0);
}
