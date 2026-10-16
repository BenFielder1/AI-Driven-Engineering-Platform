using System.Globalization;
using System.Threading.RateLimiting;
using Aide.Api.Errors;
using Aide.Api.Logging;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Aide.Api.Auth;

/// <summary>
/// Authentication and authorization (§2, D11, §8 NFR-SEC): Identity + cookie for the browser, Bearer API tokens for
/// the CLI, optional GitHub OAuth, the Admin policy, project access checks, antiforgery and auth rate limits.
/// </summary>
public static partial class AuthSetup
{
    public static WebApplicationBuilder AddAideAuth(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.AddOptions<AideAuthOptions>()
            .Bind(builder.Configuration.GetSection(AideAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty; // the user name is the email
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequiredUniqueChars = 1;
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AideDbContext>()
            .AddSignInManager()
            .AddPasswordValidator<LetterPasswordValidator>();

        var githubEnabled = GitHubAuth.IsConfigured(builder.Configuration);
        services.AddSingleton(new GitHubAuthState(githubEnabled));

        var authentication = services.AddAuthentication(options =>
            {
                options.DefaultScheme = AuthConstants.SmartScheme;
                options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
            })
            .AddPolicyScheme(AuthConstants.SmartScheme, "Cookie or API token", options =>
                options.ForwardDefaultSelector = context => ApiTokenAuthenticationHandler.HasBearer(context.Request)
                    ? AuthConstants.ApiTokenScheme
                    : IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, ConfigureAuthCookie)
            .AddCookie(IdentityConstants.ExternalScheme, options =>
            {
                options.Cookie.Name = AuthConstants.ExternalCookieName;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                AuthConstants.ApiTokenScheme, null);
        // D-V2-7: always registered; the handler uses the AIDE GitHub App once one exists, else the v1 OAuth App (if any).
        authentication.AddAideGitHub(builder.Configuration);

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<AideAuthOptions>>((o, auth) => o.Cookie.SecurePolicy = auth.Value.Cookie.SecurePolicy);
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme)
            .Configure<IOptions<AideAuthOptions>>((o, auth) => o.Cookie.SecurePolicy = auth.Value.Cookie.SecurePolicy);

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.AdminPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()))
            .AddPolicy(AuthConstants.BrowserSessionPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.HasClaim(c => c.Type == AuthConstants.TokenIdClaim)));
        services.AddScoped<IAuthorizationHandler, AdminRequirementHandler>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AuthConstants.XsrfHeaderName;
            options.Cookie.Name = AuthConstants.AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>()
            .Configure<IOptions<AideAuthOptions>>((o, auth) => o.Cookie.SecurePolicy = auth.Value.Cookie.SecurePolicy);

        services.AddRateLimiter(ConfigureRateLimits);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<SystemRoleLookup>();
        services.AddScoped<IProjectAccess, ProjectAccess>();
        services.AddScoped<AccountService>();
        return builder;
    }

    /// <summary>Pipeline order: authenticate → user log scope → rate limits → authorize → antiforgery.</summary>
    public static WebApplication UseAideAuth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            // NFR-OBS: every log line written while handling an authenticated request carries the user id.
            if (context.User.GetUserId() is not { } userId)
            {
                await next(context);
                return;
            }

            System.Diagnostics.Activity.Current?.SetTag(AideTelemetry.UserIdTag, userId.ToString());
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aide.Api.Request");
            using (logger.BeginScope(new Dictionary<string, object> { ["UserId"] = userId }))
            {
                await next(context);
            }
        });
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseAideAntiforgery();
        return app;
    }

    private static void ConfigureAuthCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = AuthConstants.AuthCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            // An API answers 401/403 with ProblemDetails; it never redirects to a login page.
            OnRedirectToLogin = context => ApiProblems.WriteAsync(context.HttpContext, ErrorCodes.Unauthenticated, "Sign in to continue."),
            OnRedirectToAccessDenied = context => ApiProblems.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, "You don't have permission to do that."),
            OnRedirectToLogout = context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,
        };
    }

    private static void ConfigureRateLimits(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(AuthConstants.LoginRateLimit, context =>
            PerIpPerMinute(context, o => o.LoginPerMinute, AuthConstants.LoginRateLimit));
        options.AddPolicy(AuthConstants.RegisterRateLimit, context =>
            PerIpPerMinute(context, o => o.RegisterPerMinute, AuthConstants.RegisterRateLimit));
        options.OnRejected = async (context, _) =>
        {
            var http = context.HttpContext;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                http.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthSetup));
            LogRateLimited(logger, LogPaths.Redact(http.Request.Path));
            await ApiProblems.WriteAsync(http, ErrorCodes.RateLimited, "Too many attempts. Try again shortly.");
        };
    }

    /// <summary>Fixed 1-minute window per client IP. Behind the web proxy the IP comes from X-Forwarded-For
    /// (ASPNETCORE_FORWARDEDHEADERS_ENABLED in Kubernetes).</summary>
    private static RateLimitPartition<string> PerIpPerMinute(
        HttpContext context, Func<AideAuthOptions.RateLimitSettings, int> limit, string policy)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<AideAuthOptions>>().Value.RateLimits;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{ip}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit(settings),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for {Path}")]
    private static partial void LogRateLimited(ILogger logger, string path);
}
