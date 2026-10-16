using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Api.Auth;

/// <summary>Whether GitHub sign-in is configured (both <c>GitHub:OAuth:ClientId</c> and <c>ClientSecret</c>).
/// Missing credentials never block: the routes return 404 and the frontend hides the button.</summary>
public sealed record GitHubAuthState(bool Enabled);

/// <summary>
/// FR-2, D-V2-7: GitHub sign-in with the built-in OAuth handler (no extra package), through
/// <see cref="GitHubOAuthHandler"/>: the AIDE GitHub App's user authorization once an App exists, else the v1 OAuth App.
/// The scheme is always registered; with neither configured the routes answer 404.
/// </summary>
public static partial class GitHubAuth
{
    public const string ClientIdKey = "GitHub:OAuth:ClientId";
    public const string ClientSecretKey = "GitHub:OAuth:ClientSecret";

    public const string CallbackPath = "/api/auth/github/callback";
    public const string AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    public const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    public const string UserEndpoint = "https://api.github.com/user";
    public const string EmailsEndpoint = "https://api.github.com/user/emails";

    public const string LoginClaim = "urn:github:login";
    public const string AvatarClaim = "urn:github:avatar";

    /// <summary>Authentication-property items (stored in the protected OAuth state).</summary>
    public const string ModeItem = "aide.mode";
    public const string LinkUserItem = "aide.link_user";
    public const string LinkMode = "link";

    /// <summary>FR-81: "Connect GitHub" from the account page: links like <see cref="LinkMode"/> and returns to the page.</summary>
    public const string ConnectMode = "connect";

    /// <summary>The App's client id, bound into the protected state when the flow uses the App (D-V2-7).</summary>
    public const string AppClientItem = "aide.github_app";

    /// <summary><c>HttpContext.Items</c> key of the App user token pair between ticket creation and sign-in (never persisted
    /// outside the encrypted connection).</summary>
    public const string TokensItem = "aide.github_tokens";

    /// <summary>Placeholder OAuth client when only an App is configured (the handler then always uses the App's).</summary>
    private const string UnconfiguredClient = "aide-oauth-app-not-configured";

    public const string LoginPath = "/login";

    public static bool IsConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[ClientIdKey]) && !string.IsNullOrWhiteSpace(configuration[ClientSecretKey]);
    }

    public static AuthenticationBuilder AddAideGitHub(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var oauthConfigured = IsConfigured(configuration);
        builder.AddOAuth<OAuthOptions, GitHubOAuthHandler>(AuthConstants.GitHubScheme, "GitHub", options =>
        {
            options.ClientId = oauthConfigured ? configuration[ClientIdKey]! : UnconfiguredClient;
            options.ClientSecret = oauthConfigured ? configuration[ClientSecretKey]! : UnconfiguredClient;
            options.CallbackPath = CallbackPath;
            options.AuthorizationEndpoint = AuthorizationEndpoint;
            options.TokenEndpoint = TokenEndpoint;
            options.UserInformationEndpoint = UserEndpoint;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");

            // The callback signs into the application cookie itself (OnTicketReceived); the external scheme is
            // only the handler's required sign-in target and is never written.
            options.SignInScheme = IdentityConstants.ExternalScheme;

            // The GitHub → callback hop is a cross-site top-level GET, which Lax cookies survive.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;

            options.Events = new OAuthEvents
            {
                OnCreatingTicket = LoadProfileAsync,
                OnTicketReceived = context =>
                    context.HttpContext.RequestServices.GetRequiredService<GitHubSignIn>().CompleteAsync(context),
                OnAccessDenied = context =>
                {
                    // The user declined on GitHub. Connect (FR-81) returns to its page; sign-in keeps the v1 redirect.
                    context.Response.Redirect(RemoteFailureUrl(context.Properties, "access_denied"));
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GitHubAuth));
                    LogRemoteFailure(logger, context.Failure?.GetType().Name ?? "unknown");
                    context.Response.Redirect(RemoteFailureUrl(context.Properties, "oauth_failed"));
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });

        builder.Services.AddOptions<OAuthOptions>(AuthConstants.GitHubScheme)
            .Configure<IOptions<AideAuthOptions>>((o, auth) => o.CorrelationCookie.SecurePolicy = auth.Value.Cookie.SecurePolicy);
        builder.Services.AddScoped<GitHubSignIn>();
        builder.Services.AddScoped<GitHubSignInResolver>();
        return builder;
    }

    /// <summary>The SPA login page with <c>error=GITHUB_AUTH_FAILED</c> and a machine-readable reason.</summary>
    public static string FailureUrl(string reason) =>
        $"{LoginPath}?error={ErrorCodes.GitHubAuthFailed}&reason={Uri.EscapeDataString(reason)}";

    /// <summary>FR-81 connect: back to the (local) page that started it, with <c>error=GITHUB_AUTH_FAILED&amp;reason=…</c>.</summary>
    public static string FailureUrl(string returnUrl, string reason)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        var separator = returnUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{returnUrl}{separator}error={ErrorCodes.GitHubAuthFailed}&reason={Uri.EscapeDataString(reason)}";
    }

    /// <summary>Reads the GitHub profile (id, login, name, avatar) and a verified email into claims; with the App, also
    /// keeps the user token pair for the connection (FR-81).</summary>
    private static async Task LoadProfileAsync(OAuthCreatingTicketContext context)
    {
        if (context.Properties.Items.ContainsKey(AppClientItem) && context.TokenResponse.Response is { } tokenResponse)
        {
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (Infrastructure.GitHub.GitHubUserTokens.FromTokenResponse(tokenResponse.RootElement, now) is { } tokens)
            {
                context.HttpContext.Items[TokensItem] = tokens;
            }
        }

        using var user = await GetJsonAsync(context, context.Options.UserInformationEndpoint);
        var root = user.RootElement;
        var identity = context.Identity!;

        var id = root.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var login = StringOrNull(root, "login");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
        identity.AddClaim(new Claim(ClaimTypes.Name, StringOrNull(root, "name") ?? login ?? "GitHub user"));
        if (login is not null)
        {
            identity.AddClaim(new Claim(LoginClaim, login));
        }

        if (StringOrNull(root, "avatar_url") is { } avatar)
        {
            identity.AddClaim(new Claim(AvatarClaim, avatar));
        }

        var email = StringOrNull(root, "email");
        if (email is null)
        {
            // The public profile email is optional; fall back to the primary verified address. With the App, a refused
            // email list (403/404: the email permission wasn't granted) just means "no email": linked users need none.
            using var emails = await GetJsonAsync(context, EmailsEndpoint, optional: context.Properties.Items.ContainsKey(AppClientItem));
            email = emails is null || emails.RootElement.ValueKind != JsonValueKind.Array ? null : emails.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("primary", out var p) && p.GetBoolean()
                    && e.TryGetProperty("verified", out var v) && v.GetBoolean())
                .Select(e => StringOrNull(e, "email"))
                .FirstOrDefault(e => e is not null);
        }

        if (email is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
        }
    }

    /// <summary>The failure redirect: back to the connect page (FR-81 connect mode), else the login page.</summary>
    private static string RemoteFailureUrl(AuthenticationProperties? properties, string reason) =>
        properties is not null && properties.Items.TryGetValue(ModeItem, out var mode) && mode == ConnectMode
            ? FailureUrl(ReturnUrls.Sanitize(properties.RedirectUri), reason)
            : FailureUrl(reason == "access_denied" ? "oauth_failed" : reason);

    private static async Task<JsonDocument> GetJsonAsync(OAuthCreatingTicketContext context, string url) =>
        (await GetJsonAsync(context, url, optional: false))!;

    /// <summary>GET with the user's token; with <paramref name="optional"/>, 403/404 is <c>null</c> instead of a failure.</summary>
    private static async Task<JsonDocument?> GetJsonAsync(OAuthCreatingTicketContext context, string url, bool optional)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        if (optional && response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted);
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sign-in failed ({FailureType})")]
    private static partial void LogRemoteFailure(ILogger logger, string failureType);
}

/// <summary>
/// Completes the GitHub callback (FR-2):
/// <list type="bullet">
/// <item>link flow (started by a signed-in user): link GitHub to that user, unless it belongs to someone else;</item>
/// <item>GitHub id already linked: sign in as the linked user;</item>
/// <item>signed in: link to the current user;</item>
/// <item>unknown email: create an account (User role, or Admin if first) and sign in;</item>
/// <item>email of an existing, unlinked account: refuse (<c>link_required</c>) — the owner must sign in and link,
/// so a GitHub account can't take over an AIDE account by claiming its email.</item>
/// </list>
/// </summary>
public sealed partial class GitHubSignIn(
    UserManager<AppUser> users,
    AccountService accounts,
    Features.GitHub.GitHubConnections connections,
    ILogger<GitHubSignIn> logger)
{
    public async Task CompleteAsync(TicketReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        var github = context.Principal!;
        var githubId = github.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var returnUrl = ReturnUrls.Sanitize(context.ReturnUri);
        var properties = context.Properties!;

        var current = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var currentUserId = current.Succeeded ? current.Principal.GetUserId() : null;
        var linked = await users.Users.FirstOrDefaultAsync(u => u.GithubId == githubId, http.RequestAborted);

        properties.Items.TryGetValue(GitHubAuth.ModeItem, out var mode);
        var linking = mode is GitHubAuth.LinkMode or GitHubAuth.ConnectMode;
        var (failure, userId) = linking
            ? await LinkAsync(properties, currentUserId, linked, github)
            : await SignInAsync(http, currentUserId, linked, github);

        // FR-81: signing in (or linking) through the App connects GitHub: the token pair is stored encrypted.
        if (failure is null && userId is { } user && http.Items[GitHubAuth.TokensItem] is Infrastructure.GitHub.GitHubUserTokens tokens
            && long.TryParse(githubId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numericId))
        {
            var saved = await connections.SaveAsync(user, numericId, github.FindFirstValue(GitHubAuth.LoginClaim) ?? string.Empty, tokens, http.RequestAborted);
            if (!saved)
            {
                LogConnectionTaken(logger, user);
                failure = "already_linked";
            }
        }

        context.HandleResponse();
        http.Response.Redirect(failure is null
            ? returnUrl
            : mode == GitHubAuth.ConnectMode ? GitHubAuth.FailureUrl(returnUrl, failure) : GitHubAuth.FailureUrl(failure));
    }

    private async Task<(string? Failure, Guid? UserId)> LinkAsync(AuthenticationProperties properties, Guid? currentUserId, AppUser? linked, ClaimsPrincipal github)
    {
        properties.Items.TryGetValue(GitHubAuth.LinkUserItem, out var expected);
        if (currentUserId is null || !string.Equals(currentUserId.ToString(), expected, StringComparison.Ordinal))
        {
            return ("link_session", null);
        }

        if (linked is not null)
        {
            return linked.Id == currentUserId ? (null, linked.Id) : ("already_linked", null);
        }

        var user = await users.FindByIdAsync(currentUserId.Value.ToString());
        return user is null ? ("link_session", null) : (await LinkToAsync(user, github), user.Id);
    }

    private async Task<(string? Failure, Guid? UserId)> SignInAsync(HttpContext http, Guid? currentUserId, AppUser? linked, ClaimsPrincipal github)
    {
        if (linked is not null)
        {
            LogSignedIn(logger, linked.Id);
            await accounts.SignInAsync(http, linked);
            return (null, linked.Id);
        }

        if (currentUserId is not null && await users.FindByIdAsync(currentUserId.Value.ToString()) is { } current)
        {
            return (await LinkToAsync(current, github), current.Id);
        }

        if (github.FindFirstValue(ClaimTypes.Email) is not { } email)
        {
            return ("email_required", null);
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            LogLinkRequired(logger);
            return ("link_required", null);
        }

        var user = new AppUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            Name = Truncate(github.FindFirstValue(ClaimTypes.Name) ?? email, 200),
            GithubId = github.FindFirstValue(ClaimTypes.NameIdentifier),
            AvatarUrl = AvatarOf(github),
        };
        IdentityResult result;
        try
        {
            result = await accounts.CreateAsync(user, password: null, http.RequestAborted);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out var constraint))
        {
            // Lost a race: the GitHub account was linked, or the email registered, by a concurrent request.
            LogUniqueViolation(logger, constraint);
            return (constraint == GithubIdIndex ? "already_linked" : "link_required", null);
        }

        if (!result.Succeeded)
        {
            LogCreateFailed(logger, string.Join(",", result.Errors.Select(e => e.Code)));
            return ("account_create_failed", null);
        }

        LogCreated(logger, user.Id, user.SystemRole);
        await accounts.SignInAsync(http, user);
        return (null, user.Id);
    }

    private async Task<string?> LinkToAsync(AppUser user, ClaimsPrincipal github)
    {
        user.GithubId = github.FindFirstValue(ClaimTypes.NameIdentifier);
        user.AvatarUrl ??= AvatarOf(github);
        IdentityResult result;
        try
        {
            result = await users.UpdateAsync(user);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, out var constraint) && constraint == GithubIdIndex)
        {
            // Lost a race: the same GitHub account was linked to another user concurrently.
            LogUniqueViolation(logger, constraint);
            return "already_linked";
        }

        if (!result.Succeeded)
        {
            return "link_failed";
        }

        LogLinked(logger, user.Id);
        return null;
    }

    /// <summary>Unique index on <c>users.github_id</c> (one AIDE account per GitHub account).</summary>
    public const string GithubIdIndex = "ix_users_github_id";

    private static bool IsUniqueViolation(DbUpdateException exception, out string? constraint)
    {
        if (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            constraint = pg.ConstraintName;
            return true;
        }

        constraint = null;
        return false;
    }

    private static string? AvatarOf(ClaimsPrincipal github) =>
        github.FindFirstValue(GitHubAuth.AvatarClaim) is { Length: <= 500 } avatar ? avatar : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with GitHub")]
    private static partial void LogSignedIn(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub account linked to user {UserId}")]
    private static partial void LogLinked(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} created from GitHub sign-in with role {SystemRole}")]
    private static partial void LogCreated(ILogger logger, Guid userId, Aide.Domain.Access.SystemRole systemRole);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub sign-in refused: the email belongs to an existing unlinked account")]
    private static partial void LogLinkRequired(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sign-in hit unique constraint {Constraint}")]
    private static partial void LogUniqueViolation(ILogger logger, string? constraint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sign-in for user {UserId} refused: the GitHub account is connected to another user")]
    private static partial void LogConnectionTaken(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating an account from GitHub failed: {ErrorCodes}")]
    private static partial void LogCreateFailed(ILogger logger, string errorCodes);
}
