using System.Security.Claims;
using System.Text.Encodings.Web;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Aide.Api.Auth;

/// <summary>
/// FR-4: <c>Authorization: Bearer aide_pat_…</c>. A usable token authenticates as its owner and records its use
/// (at most once a minute); revoked, expired or unknown tokens fail with 401. The token itself is never logged.
/// </summary>
public sealed partial class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AideDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string BearerPrefix = "Bearer ";

    /// <summary>True when the request carries a Bearer credential (used by the policy scheme).</summary>
    public static bool HasBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearer(Request))
        {
            return AuthenticateResult.NoResult();
        }

        var token = Request.Headers.Authorization.ToString()[BearerPrefix.Length..].Trim();
        if (!ApiTokenRules.IsWellFormed(token))
        {
            LogRejected(Logger, "malformed");
            return AuthenticateResult.Fail("Invalid API token.");
        }

        var hash = ApiTokenRules.Hash(token);
        var ct = Context.RequestAborted;
        var row = await db.ApiTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.UserId, t.RevokedAt, t.ExpiresAt, t.LastUsedAt })
            .FirstOrDefaultAsync(ct);

        var now = time.GetUtcNow();
        if (row is null)
        {
            LogRejected(Logger, "unknown");
            return AuthenticateResult.Fail("Invalid API token.");
        }

        if (!ApiTokenRules.IsUsable(row.RevokedAt, row.ExpiresAt, now))
        {
            LogTokenRejected(Logger, row.Id, row.RevokedAt is null ? "expired" : "revoked");
            return AuthenticateResult.Fail("Invalid API token.");
        }

        if (ApiTokenRules.ShouldRecordUse(row.LastUsedAt, now))
        {
            var threshold = now - ApiTokenRules.LastUsedResolution;
            await db.ApiTokens
                .Where(t => t.Id == row.Id && (t.LastUsedAt == null || t.LastUsedAt <= threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, row.UserId.ToString()),
                new Claim(AuthConstants.TokenIdClaim, row.Id.ToString()),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        await ApiProblems.WriteAsync(Context, ErrorCodes.Unauthenticated, "A valid API token is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.WriteAsync(Context, ErrorCodes.Forbidden, "You don't have permission to do that.");

    [LoggerMessage(Level = LogLevel.Information, Message = "API token rejected ({Reason})")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "API token {TokenId} rejected ({Reason})")]
    private static partial void LogTokenRejected(ILogger logger, Guid tokenId, string reason);
}
