using System.Diagnostics;

namespace Aide.Api.Logging;

/// <summary>
/// NFR-SEC: request paths as they may be logged or traced. The one-click approval route carries a bearer credential in
/// its path (<c>/api/approvals/{token}</c>, FR-48), so its token segment is replaced with <see cref="Redacted"/>.
/// </summary>
public static class LogPaths
{
    public const string Redacted = "[redacted]";

    private static readonly PathString ApprovalsPrefix = new("/api/approvals");

    /// <summary>Whether <paramref name="path"/> carries an approval token.</summary>
    public static bool IsSensitive(PathString path) =>
        path.StartsWithSegments(ApprovalsPrefix, StringComparison.OrdinalIgnoreCase, out var rest) && rest.HasValue && rest.Value != "/";

    /// <summary><paramref name="path"/> with an approval token replaced: <c>/api/approvals/[redacted]</c> (plus any sub-path).</summary>
    public static string Redact(PathString path)
    {
        if (!path.StartsWithSegments(ApprovalsPrefix, StringComparison.OrdinalIgnoreCase, out var matched, out var rest) || !rest.HasValue || rest.Value == "/")
        {
            return path.ToString();
        }

        var afterToken = rest.Value!.IndexOf('/', 1);
        return matched.ToString() + "/" + Redacted + (afterToken < 0 ? string.Empty : rest.Value[afterToken..]);
    }
}

/// <summary>
/// NFR-OBS / NFR-SEC: the per-request log scope (<c>RequestId</c>, <c>RequestPath</c>) every log entry of a request carries.
/// ASP.NET Core's own hosting scope records the raw path, which for <c>/api/approvals/{token}</c> is a credential, so it is
/// turned off (<see cref="AddRedactedRequestScope"/>) and replaced by this one, with the path from <see cref="LogPaths.Redact"/>.
/// </summary>
public static class RequestLogScope
{
    public const string RequestIdKey = "RequestId";
    public const string RequestPathKey = "RequestPath";

    /// <summary>The logger category whose scope (and request start/finish logs) carry the raw request path.</summary>
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>Turns off the hosting request scope (it is created only when its category is enabled).</summary>
    public static ILoggingBuilder AddRedactedRequestScope(this ILoggingBuilder logging) =>
        logging.AddFilter(HostingDiagnosticsCategory, LogLevel.None);

    /// <summary>Opens the redacted request scope; register first, so every later log entry of the request carries it.</summary>
    public static IApplicationBuilder UseRequestLogScope(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RequestLogScope));
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                [RequestIdKey] = context.TraceIdentifier,
                [RequestPathKey] = LogPaths.Redact(context.Request.PathBase.Add(context.Request.Path)),
            }))
            {
                await next(context);
            }
        });
}

/// <summary>NFR-SEC: request spans of the approval route record the redacted path, never the token (FR-48).</summary>
public static class TracePaths
{
    private static readonly string[] PathTags = ["url.path", "url.full", "http.target", "http.url"];

    public static void Redact(Activity activity, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);
        if (!LogPaths.IsSensitive(request.Path))
        {
            return;
        }

        var raw = request.Path.ToString();
        var redacted = LogPaths.Redact(request.Path);
        foreach (var tag in PathTags)
        {
            if (activity.GetTagItem(tag) is string value && value.Contains(raw, StringComparison.Ordinal))
            {
                activity.SetTag(tag, value.Replace(raw, redacted, StringComparison.Ordinal));
            }
        }

        if (activity.DisplayName.Contains(raw, StringComparison.Ordinal))
        {
            activity.DisplayName = activity.DisplayName.Replace(raw, redacted, StringComparison.Ordinal);
        }
    }
}
