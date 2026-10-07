using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Tokens;

/// <summary>A personal API token as listed (never includes the token or its hash).</summary>
public sealed record ApiTokenDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed record CreateApiTokenRequest
{
    [Required]
    [MaxLength(ApiTokenRules.MaxNameLength)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional expiry; must be in the future.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>A newly created token. <see cref="Token"/> is shown once and can't be retrieved again.</summary>
public sealed record CreatedApiTokenDto(Guid Id, string Name, string Token, DateTimeOffset? ExpiresAt);

/// <summary>FR-4: personal API tokens (§6 <c>/api/me/tokens</c>). Users only see and revoke their own tokens.</summary>
public static partial class ApiTokenEndpoints
{
    public static IEndpointRouteBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/tokens")
            .WithTags("Account")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListApiTokens)
            .WithName("ListApiTokens")
            .WithSummary("The caller's API tokens, newest first (including revoked and expired ones). Browser session only.");

        group.MapPost("/", CreateApiToken)
            .WithName("CreateApiToken")
            .WithSummary("Create a token. The plain token is returned once; only its SHA-256 hash is stored. Browser session only.");

        group.MapDelete("/{id:guid}", RevokeApiToken)
            .WithName("RevokeApiToken")
            .WithSummary("Revoke one of the caller's tokens (idempotent). Browser session only.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<List<ApiTokenDto>>> ListApiTokens(ICurrentUser currentUser, AideDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        var tokens = await db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new ApiTokenDto(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.RevokedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(tokens);
    }

    private static async Task<Results<Created<CreatedApiTokenDto>, ValidationProblem>> CreateApiToken(
        CreateApiTokenRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var validation = ApiTokenRules.ValidateNew(request.Name, request.ExpiresAt, now);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var plain = ApiTokenRules.Format(RandomNumberGenerator.GetBytes(ApiTokenRules.SecretBytes));
        var token = new ApiToken
        {
            UserId = currentUser.RequiredUserId,
            Name = request.Name.Trim(),
            TokenHash = ApiTokenRules.Hash(plain),
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        var logger = loggers.CreateLogger(typeof(ApiTokenEndpoints));
        LogCreated(logger, token.Id);
        return TypedResults.Created($"/api/me/tokens/{token.Id}", new CreatedApiTokenDto(token.Id, token.Name, plain, token.ExpiresAt));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeApiToken(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, cancellationToken);
        if (token is null)
        {
            return ApiProblems.NotFound("The token was not found.");
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var logger = loggers.CreateLogger(typeof(ApiTokenEndpoints));
            LogRevoked(logger, token.Id);
        }

        return TypedResults.NoContent();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API token {TokenId} created")]
    private static partial void LogCreated(ILogger logger, Guid tokenId);

    [LoggerMessage(Level = LogLevel.Information, Message = "API token {TokenId} revoked")]
    private static partial void LogRevoked(ILogger logger, Guid tokenId);
}
