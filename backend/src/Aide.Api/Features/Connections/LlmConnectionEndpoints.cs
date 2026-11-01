using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Connections;

/// <summary>
/// FR-78: the caller's LLM provider connections (§6 <c>/api/me/connections/llm</c>). Connections are personal: every
/// route sees only the caller's own (another user's id is <c>NOT_FOUND</c>), and nobody — Admins included — can read a
/// key back. Keys are checked with the provider's list-models endpoint before they are saved, encrypted with Data
/// Protection (<see cref="SecretPurposes.LlmApiKey"/>), and never logged. Mutations need a browser session: API tokens
/// get <c>FORBIDDEN</c>, as for token management (§6 Notes).
/// </summary>
public static partial class LlmConnectionEndpoints
{
    public static IEndpointRouteBuilder MapLlmConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/connections/llm")
            .WithTags("Connections")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListLlmConnections)
            .WithName("ListLlmConnections")
            .WithSummary("The caller's LLM connections (no keys: provider, label, last 4, status, bound projects) and the providers that need no key.")
            .Produces<LlmConnectionsDto>();

        group.MapPost("/", CreateLlmConnection)
            .WithName("CreateLlmConnection")
            .WithSummary("Add an Anthropic or OpenAI key. It is checked with the provider first: 400 CREDENTIAL_INVALID if rejected (nothing saved), 502 UPSTREAM_ERROR if the provider can't be reached. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<LlmCredentialDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPatch("/{id:guid}", RenameLlmConnection)
            .WithName("RenameLlmConnection")
            .WithSummary("Rename one of the caller's LLM connections. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<LlmCredentialDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteLlmConnection)
            .WithName("DeleteLlmConnection")
            .WithSummary("Delete one of the caller's LLM connections. Projects it was bound to lose their binding (LLM_BINDING_CLEARED); their runs then fail with PROVIDER_NOT_CONFIGURED. Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", TestLlmConnection)
            .WithName("TestLlmConnection")
            .WithSummary("Check the key with the provider again and record the result (status valid or invalid, lastValidatedAt). 502 UPSTREAM_ERROR if the provider can't be reached (status unchanged). Browser session only.")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .Produces<LlmCredentialDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<Ok<LlmConnectionsDto>> ListLlmConnections(
        ICurrentUser currentUser,
        AideDbContext db,
        Infrastructure.Llm.ILlmProviderCatalog catalog,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;

        // FR-157: a disabled provider is absent from every list, its stored connections included (they stay stored and
        // reappear if the provider is turned back on).
        var credentials = (await db.LlmCredentials.AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync(cancellationToken))
            .Where(c => catalog.IsEnabled(c.Provider))
            .ToList();
        var bound = await BoundProjectsAsync(db, [.. credentials.Select(c => c.Id)], cancellationToken);
        return TypedResults.Ok(new LlmConnectionsDto(
            [.. credentials.Select(c => ToDto(c, bound.GetValueOrDefault(c.Id) ?? []))],
            [.. LlmConnectionRules.KeylessProviders.Where(catalog.IsEnabled)]));
    }

    private static async Task<Results<Created<LlmCredentialDto>, ValidationProblem, ProblemHttpResult>> CreateLlmConnection(
        CreateLlmCredentialRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
        ILlmKeyValidator validator,
        Infrastructure.Llm.ILlmProviderCatalog catalog,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var validation = LlmConnectionRules.ValidateNew(request.Provider, request.Label, request.ApiKey);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        // FR-157: a disabled provider can't get a connection, and its key is never sent anywhere.
        if (LlmProviderRules.IsEnabled(request.Provider, catalog.IsEnabled(request.Provider)) is { IsFailure: true } disabled)
        {
            return ApiProblems.FromError(disabled.Error!);
        }

        var logger = loggers.CreateLogger(typeof(LlmConnectionEndpoints));
        var check = await validator.CheckAsync(request.Provider, request.ApiKey, cancellationToken);
        LogChecked(logger, request.Provider, check.Outcome, check.StatusCode);
        if (CheckProblem(check) is { } problem)
        {
            // FR-78: a rejected key (or an unreachable provider) saves nothing.
            return problem;
        }

        var now = Now(time);
        var credential = new LlmCredential
        {
            UserId = currentUser.RequiredUserId,
            Provider = request.Provider,
            Label = request.Label.Trim(),
            SecretEncrypted = secrets.Protect(request.ApiKey),
            Last4 = LlmCredential.Last4Of(request.ApiKey),
            Status = LlmCredentialStatus.Valid,
            LastValidatedAt = now,
            CreatedAt = now,
        };
        db.LlmCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, credential.Id, credential.Provider);
        return TypedResults.Created($"/api/me/connections/llm/{credential.Id}", ToDto(credential, []));
    }

    private static async Task<Results<Ok<LlmCredentialDto>, ValidationProblem, ProblemHttpResult>> RenameLlmConnection(
        Guid id,
        UpdateLlmCredentialRequest request,
        ICurrentUser currentUser,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var validation = LlmConnectionRules.ValidateLabel(request.Label);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var credential = await OwnAsync(db, currentUser, id, cancellationToken);
        if (credential is null)
        {
            return NotFound();
        }

        credential.Label = request.Label.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDto(credential, (await BoundProjectsAsync(db, [credential.Id], cancellationToken)).GetValueOrDefault(credential.Id) ?? []));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteLlmConnection(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var credential = await OwnAsync(db, currentUser, id, cancellationToken);
        if (credential is null)
        {
            return NotFound();
        }

        // FR-79: every project bound to the connection loses its binding in the same transaction (one SaveChanges), with
        // an LLM_BINDING_CLEARED event each. Runs keep their history (agent_runs.credential_id is set null by the FK).
        var now = Now(time);
        var bindings = await db.ProjectLlmSettings.Where(s => s.CredentialId == id).ToListAsync(cancellationToken);
        foreach (var settings in bindings)
        {
            LlmBindings.Clear(db, settings, LlmBindingClearedReason.ConnectionDeleted, currentUser.Actor, now);
        }

        db.LlmCredentials.Remove(credential);
        await db.SaveChangesAsync(cancellationToken);
        var logger = loggers.CreateLogger(typeof(LlmConnectionEndpoints));
        LogDeleted(logger, id, bindings.Count);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<LlmCredentialDto>, ProblemHttpResult>> TestLlmConnection(
        Guid id,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
        ILlmKeyValidator validator,
        Infrastructure.Llm.ILlmProviderCatalog catalog,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var credential = await OwnAsync(db, currentUser, id, cancellationToken);
        if (credential is null)
        {
            return NotFound();
        }

        // FR-157: a connection of a disabled provider isn't checked (no outbound call) and its status is left as it was.
        if (LlmProviderRules.IsEnabled(credential.Provider, catalog.IsEnabled(credential.Provider)) is { IsFailure: true } disabled)
        {
            return ApiProblems.FromError(disabled.Error!);
        }

        var logger = loggers.CreateLogger(typeof(LlmConnectionEndpoints));
        LlmKeyCheck check;
        if (secrets.TryUnprotect(credential.SecretEncrypted, out var key) && key.Length > 0)
        {
            check = await validator.CheckAsync(credential.Provider, key, cancellationToken);
        }
        else
        {
            // A key that can't be decrypted (lost key ring) can never work again: it has to be added anew.
            check = new LlmKeyCheck(LlmKeyCheckOutcome.Rejected, null, "The saved key can't be decrypted.");
        }

        LogChecked(logger, credential.Provider, check.Outcome, check.StatusCode);
        if (check.Outcome == LlmKeyCheckOutcome.Unavailable)
        {
            return ApiProblems.Of(ErrorCodes.UpstreamError, $"The key couldn't be checked: {check.Message}");
        }

        credential.Status = check.Outcome == LlmKeyCheckOutcome.Valid ? LlmCredentialStatus.Valid : LlmCredentialStatus.Invalid;
        credential.LastValidatedAt = Now(time);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDto(credential, (await BoundProjectsAsync(db, [credential.Id], cancellationToken)).GetValueOrDefault(credential.Id) ?? []));
    }

    /// <summary>The caller's own connection with that id (tracked), or <c>null</c>: other users' connections don't exist
    /// for the caller.</summary>
    private static Task<LlmCredential?> OwnAsync(AideDbContext db, ICurrentUser currentUser, Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequiredUserId;
        return db.LlmCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
    }

    private static ProblemHttpResult? CheckProblem(LlmKeyCheck check) => check.Outcome switch
    {
        LlmKeyCheckOutcome.Valid => null,
        LlmKeyCheckOutcome.Rejected => ApiProblems.Of(ErrorCodes.CredentialInvalid, check.Message),
        _ => ApiProblems.Of(ErrorCodes.UpstreamError, $"The key couldn't be checked: {check.Message}"),
    };

    /// <summary>The current time at PostgreSQL's <c>timestamptz</c> precision (microseconds), so a response matches what is
    /// read back later.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static ProblemHttpResult NotFound() => ApiProblems.NotFound("The connection was not found.");

    private static async Task<Dictionary<Guid, List<LlmBoundProjectDto>>> BoundProjectsAsync(
        AideDbContext db, IReadOnlyList<Guid> credentialIds, CancellationToken cancellationToken)
    {
        if (credentialIds.Count == 0)
        {
            return [];
        }

        var rows = await (
                from s in db.ProjectLlmSettings.AsNoTracking()
                join p in db.Projects.AsNoTracking() on s.ProjectId equals p.Id
                where s.CredentialId != null && credentialIds.Contains(s.CredentialId.Value)
                orderby p.Name
                select new { CredentialId = s.CredentialId!.Value, p.Id, p.Name })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.CredentialId).ToDictionary(g => g.Key, g => g.Select(r => new LlmBoundProjectDto(r.Id, r.Name)).ToList());
    }

    private static LlmCredentialDto ToDto(LlmCredential c, IReadOnlyList<LlmBoundProjectDto> boundProjects) =>
        new(c.Id, c.Provider, c.Label, c.Last4, c.Status, c.LastValidatedAt, c.CreatedAt, boundProjects);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM key check for {Provider}: {Outcome} (status {StatusCode})")]
    private static partial void LogChecked(ILogger logger, LlmProvider provider, LlmKeyCheckOutcome outcome, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM connection {CredentialId} added for {Provider}")]
    private static partial void LogCreated(ILogger logger, Guid credentialId, LlmProvider provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM connection {CredentialId} deleted; {Bindings} project bindings cleared")]
    private static partial void LogDeleted(ILogger logger, Guid credentialId, int bindings);
}
