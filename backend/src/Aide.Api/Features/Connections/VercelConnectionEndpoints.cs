using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Deployments;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Connections;

/// <summary>
/// FR-87: the caller's Vercel connections (§6 <c>/api/me/connections/vercel</c>). Connections are personal: every route sees
/// only the caller's own (another user's id is <c>NOT_FOUND</c>), and nobody can read a token back. Tokens are checked
/// with Vercel (<c>GET /v2/user</c>, the token's scope via <c>GET /v5/user/tokens/current</c>, and the team for team
/// tokens) before they are saved, encrypted with Data Protection (<c>SecretPurposes.VercelToken</c>) and never logged.
/// Mutations need a browser session: API tokens get <c>FORBIDDEN</c> (§6 Notes).
/// </summary>
public static partial class VercelConnectionEndpoints
{
    public static IEndpointRouteBuilder MapVercelConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/connections/vercel")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListVercelConnections)
            .WithName("ListVercelConnections")
            .WithSummary("The caller's Vercel connections, newest first (no tokens: label, last 4, account, team, scope, expiry, status, bound projects).")
            .Produces<IReadOnlyList<VercelConnectionDto>>();

        group.MapPost("/", CreateVercelConnection)
            .WithName("CreateVercelConnection")
            .WithSummary("Add a Vercel access token. It is checked with Vercel first (GET /v2/user, and the team for a team-scoped token): 400 CREDENTIAL_INVALID if rejected (nothing saved), 502 UPSTREAM_ERROR if Vercel can't be reached. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<VercelConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION, or CREDENTIAL_INVALID (Vercel rejected the token; nothing is saved)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (Vercel failed or couldn't be reached; nothing is saved)"));

        group.MapPatch("/{id:guid}", RenameVercelConnection)
            .WithName("RenameVercelConnection")
            .WithSummary("Rename one of the caller's Vercel connections. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<VercelConnectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteVercelConnection)
            .WithName("DeleteVercelConnection")
            .WithSummary("Delete one of the caller's Vercel connections. Projects it was bound to lose their binding (their deployment settings and history stay); new deployments then fail with DEPLOYMENT_NOT_CONFIGURED until an Owner binds another. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", TestVercelConnection)
            .WithName("TestVercelConnection")
            .WithSummary("Check the token with Vercel again and record the result (status valid or invalid, lastValidatedAt; account and team refreshed). 502 UPSTREAM_ERROR if Vercel can't be reached (status unchanged). Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<VercelConnectionDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<VercelConnectionDto>>> ListVercelConnections(ICurrentUser currentUser, AideDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        var connections = await db.VercelConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        var bound = await BoundProjectsAsync(db, [.. connections.Select(c => c.Id)], cancellationToken);
        return TypedResults.Ok<IReadOnlyList<VercelConnectionDto>>([.. connections.Select(c => ToDto(c, bound.GetValueOrDefault(c.Id) ?? []))]);
    }

    private static async Task<Results<Created<VercelConnectionDto>, ValidationProblem, ProblemHttpResult>> CreateVercelConnection(
        CreateVercelConnectionRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var validation = VercelConnectionRules.ValidateNew(request.Label, request.Token);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var logger = loggers.CreateLogger(typeof(VercelConnectionEndpoints));
        DeploymentAccount account;
        try
        {
            account = await deployments.Provider.ValidateTokenAsync(request.Token, cancellationToken);
        }
        catch (DeploymentProviderException ex)
        {
            // FR-87: a rejected token (or Vercel unreachable) saves nothing.
            LogChecked(logger, ex.Failure, ex.Status);
            return CheckProblem(ex);
        }

        LogChecked(logger, null, 200);
        var now = Now(time);
        var connection = new VercelConnection
        {
            UserId = currentUser.RequiredUserId,
            Label = request.Label.Trim(),
            TokenEncrypted = deployments.Tokens.Protect(request.Token),
            Last4 = LlmCredential.Last4Of(request.Token),
            Status = VercelConnectionStatus.Valid,
            LastValidatedAt = now,
            CreatedAt = now,
        };
        Apply(connection, account);
        db.VercelConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, connection.Id, connection.Scope);
        return TypedResults.Created($"/api/me/connections/vercel/{connection.Id}", ToDto(connection, []));
    }

    private static async Task<Results<Ok<VercelConnectionDto>, ValidationProblem, ProblemHttpResult>> RenameVercelConnection(
        Guid id,
        UpdateVercelConnectionRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var validation = VercelConnectionRules.ValidateLabel(request.Label);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var connection = await OwnAsync(db, currentUser, id, cancellationToken);
        if (connection is null)
        {
            return NotFound();
        }

        connection.Label = request.Label.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDto(connection, (await BoundProjectsAsync(db, [connection.Id], cancellationToken)).GetValueOrDefault(connection.Id) ?? []));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteVercelConnection(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var connection = await OwnAsync(db, currentUser, id, cancellationToken);
        if (connection is null)
        {
            return NotFound();
        }

        // FR-88: every project bound to the connection loses its binding in the same transaction, with an event each.
        var now = Now(time);
        var bindings = await db.ProjectDeploymentSettings.Where(s => s.ConnectionId == id).ToListAsync(cancellationToken);
        foreach (var settings in bindings)
        {
            DeploymentBindings.Clear(db, settings, DeploymentBindings.ConnectionDeleted, currentUser.Actor, now);
        }

        db.VercelConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
        var logger = loggers.CreateLogger(typeof(VercelConnectionEndpoints));
        LogDeleted(logger, id, bindings.Count);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<VercelConnectionDto>, ProblemHttpResult>> TestVercelConnection(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        DeploymentAccess deployments,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var connection = await OwnAsync(db, currentUser, id, cancellationToken);
        if (connection is null)
        {
            return NotFound();
        }

        var logger = loggers.CreateLogger(typeof(VercelConnectionEndpoints));
        var now = Now(time);
        if (!deployments.Tokens.TryUnprotect(connection.TokenEncrypted, out var token) || token.Length == 0)
        {
            // A token that can't be decrypted (lost key ring) can never work again: it has to be added anew.
            connection.Status = VercelConnectionStatus.Invalid;
        }
        else
        {
            try
            {
                Apply(connection, await deployments.Provider.ValidateTokenAsync(token, cancellationToken));
                connection.Status = VercelConnectionStatus.Valid;
                LogChecked(logger, null, 200);
            }
            catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.CredentialRejected)
            {
                connection.Status = VercelConnectionStatus.Invalid;
                LogChecked(logger, ex.Failure, ex.Status);
            }
            catch (DeploymentProviderException ex)
            {
                LogChecked(logger, ex.Failure, ex.Status);
                return ApiProblems.Of(ErrorCodes.UpstreamError, $"The token couldn't be checked: {ex.Message}");
            }
        }

        connection.LastValidatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDto(connection, (await BoundProjectsAsync(db, [connection.Id], cancellationToken)).GetValueOrDefault(connection.Id) ?? []));
    }

    private static void Apply(VercelConnection connection, DeploymentAccount account)
    {
        connection.VercelUserId = account.UserId;
        connection.VercelUsername = account.Username;
        connection.TeamId = account.TeamId;
        connection.TeamSlug = account.TeamSlug;
        connection.Scope = account.Scope;
        connection.ExpiresAt = account.ExpiresAt;
    }

    private static ProblemHttpResult CheckProblem(DeploymentProviderException ex) => ex.Failure switch
    {
        VercelFailure.CredentialRejected or VercelFailure.Forbidden => ApiProblems.Of(ErrorCodes.CredentialInvalid, "Vercel rejected the token. Check it was copied completely and hasn't expired."),
        _ => ApiProblems.Of(ErrorCodes.UpstreamError, $"The token couldn't be checked: {ex.Message}"),
    };

    /// <summary>The caller's own connection with that id (tracked), or <c>null</c>: other users' connections don't exist
    /// for the caller.</summary>
    private static Task<VercelConnection?> OwnAsync(AideDbContext db, ICurrentUser currentUser, Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        return db.VercelConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
    }

    private static async Task<Dictionary<Guid, List<VercelBoundProjectDto>>> BoundProjectsAsync(
        AideDbContext db, IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0)
        {
            return [];
        }

        var rows = await (
                from s in db.ProjectDeploymentSettings.AsNoTracking()
                join p in db.Projects.AsNoTracking() on s.ProjectId equals p.Id
                where s.ConnectionId != null && connectionIds.Contains(s.ConnectionId.Value)
                orderby p.Name
                select new { ConnectionId = s.ConnectionId!.Value, p.Id, p.Name })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ConnectionId).ToDictionary(g => g.Key, g => g.Select(r => new VercelBoundProjectDto(r.Id, r.Name)).ToList());
    }

    private static VercelConnectionDto ToDto(VercelConnection c, IReadOnlyList<VercelBoundProjectDto> boundProjects) =>
        new(c.Id, c.Label, c.Last4, c.VercelUsername, c.TeamId, c.TeamSlug, c.Scope, c.ExpiresAt, c.Status, c.LastValidatedAt, c.CreatedAt, boundProjects);

    /// <summary>The current time at PostgreSQL's <c>timestamptz</c> precision (microseconds).</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static ProblemHttpResult NotFound() => ApiProblems.NotFound("The connection was not found.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Vercel token check: failure {Failure} (none: valid), status {StatusCode}")]
    private static partial void LogChecked(ILogger logger, VercelFailure? failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vercel connection {ConnectionId} added ({Scope} scope)")]
    private static partial void LogCreated(ILogger logger, Guid connectionId, VercelTokenScope scope);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vercel connection {ConnectionId} deleted; {Bindings} project bindings cleared")]
    private static partial void LogDeleted(ILogger logger, Guid connectionId, int bindings);
}
