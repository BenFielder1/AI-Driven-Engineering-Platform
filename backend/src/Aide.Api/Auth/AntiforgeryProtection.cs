using Aide.Api.Errors;
using Aide.Api.Logging;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Aide.Api.Auth;

/// <summary>Marks an endpoint as exempt from the antiforgery check (anonymous login/register, which are rate limited).</summary>
public sealed class AntiforgeryExemptMetadata
{
    public static readonly AntiforgeryExemptMetadata Instance = new();
}

/// <summary>
/// §8 NFR-SEC: cookie-authenticated mutations (POST/PUT/PATCH/DELETE) must send <c>X-XSRF-TOKEN</c> with the
/// request token from the readable <c>XSRF-TOKEN</c> cookie (validated by ASP.NET Core antiforgery against its
/// HttpOnly <c>aide.af</c> cookie and the signed-in user). Bearer-token requests are exempt: a cross-site page can't
/// set an Authorization header. Failures are 403 <c>ANTIFORGERY</c>.
/// </summary>
public static partial class AntiforgeryProtection
{
    public static TBuilder SkipAntiforgery<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AntiforgeryExemptMetadata.Instance);

    /// <summary>Issues (or refreshes) the antiforgery cookie pair for the current <see cref="HttpContext.User"/>.
    /// Call after changing the user (sign-in/out): request tokens are bound to the user.</summary>
    public static void IssueTokens(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var services = httpContext.RequestServices;
        var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(httpContext);
        var securePolicy = services.GetRequiredService<IOptions<AideAuthOptions>>().Value.Cookie.SecurePolicy;

        httpContext.Response.Cookies.Append(AuthConstants.XsrfCookieName, tokens.RequestToken ?? string.Empty, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Secure = securePolicy switch
            {
                CookieSecurePolicy.Always => true,
                CookieSecurePolicy.None => false,
                _ => httpContext.Request.IsHttps,
            },
        });
    }

    public static IApplicationBuilder UseAideAntiforgery(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (RequiresCheck(context))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AntiforgeryProtection));
                    // The approval route carries its token in the path (NFR-SEC): never log it.
                    LogRejected(logger, context.Request.Method, LogPaths.Redact(context.Request.Path));
                    await ApiProblems.WriteAsync(context, ErrorCodes.Antiforgery,
                        "The request is missing a valid antiforgery token. Reload the page and try again.");
                    return;
                }
            }

            await next(context);
        });

    private static bool RequiresCheck(HttpContext context)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return false;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<AntiforgeryExemptMetadata>() is not null)
        {
            return false;
        }

        // Only cookie sessions are exposed to cross-site requests.
        return context.User.Identity is { IsAuthenticated: true, AuthenticationType: var type }
            && string.Equals(type, IdentityConstants.ApplicationScheme, StringComparison.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Antiforgery validation failed for {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, string method, string path);
}
