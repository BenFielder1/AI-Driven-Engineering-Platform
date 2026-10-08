using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Live;

/// <summary>Configuration of the live-update hub (<c>Live</c> section).</summary>
public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary>
    /// Extra browser origins (<c>scheme://host[:port]</c>) allowed to open the hub besides the API's own origin. Empty
    /// by default: the SPA is served same-origin (Vite proxy in the dev loop, nginx in Kubernetes).
    /// </summary>
    public IList<string> AllowedOrigins { get; init; } = [];
}

/// <summary>
/// §8 NFR-SEC, FR-46: cross-site WebSocket hijacking protection for <c>/api/hubs</c>. Antiforgery covers the negotiate
/// POST, but a browser can open the WebSocket directly (skip negotiation) and the <c>SameSite=Lax</c> auth cookie is
/// sent for any same-site origin (another <c>localhost</c> port is same-site). So every hub request:
/// <list type="bullet">
/// <item>with an <c>Origin</c> header must come from the API's own origin (scheme + host, or the
/// <c>X-Forwarded-Host</c> nginx sets, which a page can't forge on a WebSocket) or <see cref="LiveOptions.AllowedOrigins"/>;</item>
/// <item>that is a WebSocket upgrade without a bearer token (so authenticated by the cookie) must have an <c>Origin</c>
/// — browsers always send one on a WebSocket handshake.</item>
/// </list>
/// Bearer-token clients (CLI) send no Origin and are unaffected: a cross-site page can't set an Authorization header.
/// Rejections are 403 <c>FORBIDDEN</c> ProblemDetails, before authentication.
/// </summary>
public static partial class LiveOriginCheck
{
    /// <summary>The hub route prefix.</summary>
    public static readonly PathString HubsPath = "/api/hubs";

    public static IServiceCollection AddLiveOriginCheck(this IServiceCollection services)
    {
        services.AddOptions<LiveOptions>()
            .BindConfiguration(LiveOptions.SectionName)
            .Validate(o => o.AllowedOrigins.All(IsOrigin), "Live:AllowedOrigins entries must be absolute http(s) origins without a path.")
            .ValidateOnStart();
        return services;
    }

    public static IApplicationBuilder UseLiveOriginCheck(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(HubsPath, StringComparison.OrdinalIgnoreCase))
            {
                var allowedOrigins = context.RequestServices.GetRequiredService<IOptions<LiveOptions>>().Value.AllowedOrigins;
                if (Rejection(context.Request, IsWebSocketUpgrade(context), allowedOrigins) is { } reason)
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LiveOriginCheck));
                    LogRejected(logger, context.Request.Path, reason);
                    await ApiProblems.WriteAsync(context, ErrorCodes.Forbidden, "This origin may not open live updates.");
                    return;
                }
            }

            await next(context);
        });

    /// <summary>Why the request is refused, or <c>null</c> if it may proceed.</summary>
    private static string? Rejection(HttpRequest request, bool isWebSocketUpgrade, IEnumerable<string> allowedOrigins)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return isWebSocketUpgrade && !ApiTokenAuthenticationHandler.HasBearer(request)
                ? "cookie WebSocket without Origin"
                : null;
        }

        if (request.Headers.Origin.Count != 1)
        {
            return "several Origin headers";
        }

        var own = $"{request.Scheme}://{request.Host.Value}";
        if (Same(origin, own))
        {
            return null;
        }

        var forwardedHost = request.Headers["X-Forwarded-Host"].ToString();
        if (!string.IsNullOrEmpty(forwardedHost) && !forwardedHost.Contains(',', StringComparison.Ordinal)
            && Same(origin, $"{request.Scheme}://{forwardedHost}"))
        {
            return null;
        }

        return allowedOrigins.Any(allowed => Same(origin, allowed.TrimEnd('/'))) ? null : "Origin not allowed";
    }

    private static bool Same(string origin, string expected) => string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// HTTP/1.1 <c>Upgrade: websocket</c> or HTTP/2+ extended CONNECT. Checked on the raw request: the WebSockets
    /// middleware SignalR adds runs later, inside the hub's endpoint.
    /// </summary>
    private static bool IsWebSocketUpgrade(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            return true;
        }

        if (context.Request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true, Protocol: var protocol }
            && string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrigin(string value) =>
        Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live hub request to {Path} refused: {Reason}")]
    private static partial void LogRejected(ILogger logger, PathString path, string reason);
}
