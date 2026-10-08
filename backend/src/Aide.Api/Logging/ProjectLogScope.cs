namespace Aide.Api.Logging;

/// <summary>
/// NFR-OBS: every log entry written while handling a <c>/api/projects/{id}/…</c> request carries the project id as a
/// <c>ProjectId</c> scope value (background jobs add <c>JobId</c>/<c>ProjectId</c> in the job worker).
/// </summary>
public static class ProjectLogScope
{
    public const string ProjectIdKey = "ProjectId";

    public static IApplicationBuilder UseProjectLogScope(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/projects")
                && context.GetRouteValue("id") is string raw
                && Guid.TryParse(raw, out var projectId))
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProjectLogScope));
                using (logger.BeginScope(new Dictionary<string, object?> { [ProjectIdKey] = projectId }))
                {
                    await next(context);
                }

                return;
            }

            await next(context);
        });
}
