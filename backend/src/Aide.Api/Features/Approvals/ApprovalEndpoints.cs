using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Gates;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Approvals;

/// <summary>
/// FR-48: signed single-use one-click approval links (§6 <c>/api/approvals/:token</c>). The link opens a confirmation
/// page; the decision is taken only by the confirming POST, by a signed-in Owner of the gate's project (browser session).
/// Unknown or malformed tokens are 404.
/// </summary>
public static class ApprovalEndpoints
{
    public const int TokenMaxLength = 200;

    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/approvals/{token}")
            .WithTags("Approvals")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapGet("/", GetApprovalLink)
            .WithName("GetApprovalLink")
            .WithSummary("What a one-click approval link would decide (Owner of the gate's project, browser session): the gate, project, decision and gated artifact. Reading never uses the link.")
            .Produces<ApprovalLinkViewDto>()
            .WithResponseDescriptions(
                (StatusCodes.Status200OK, "The link is valid, or its gate is no longer pending (status gate_closed)."),
                (StatusCodes.Status401Unauthorized, "UNAUTHENTICATED: sign in, then open the link again."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: the caller is not an Owner of the gate's project (or used an API token)."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: unknown or malformed token."),
                (StatusCodes.Status409Conflict, "LINK_USED: the link was already used."),
                (StatusCodes.Status410Gone, "LINK_EXPIRED: the link is older than 72 hours."));

        group.MapPost("/", ConfirmApprovalLink)
            .WithName("ConfirmApprovalLink")
            .WithSummary("Confirm the link's decision (Owner of the gate's project, browser session): approves or rejects the gate exactly as the gate endpoints do, and uses up the link (and its sibling link for the same gate).")
            .Produces<GateDto>()
            .ProducesValidationProblem()
            .WithResponseDescriptions(
                (StatusCodes.Status200OK, "The gate as decided."),
                (StatusCodes.Status400BadRequest, "VALIDATION: comment too long."),
                (StatusCodes.Status401Unauthorized, "UNAUTHENTICATED: sign in, then open the link again."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN: the caller is not an Owner of the gate's project (or used an API token); ANTIFORGERY: missing X-XSRF-TOKEN."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: unknown or malformed token."),
                (StatusCodes.Status409Conflict, "LINK_USED (the link was already used), GATE_NOT_PENDING (the gate was decided or cancelled elsewhere) or CONFLICT (a concurrent change)."),
                (StatusCodes.Status410Gone, "LINK_EXPIRED: the link is older than 72 hours."));

        return app;
    }

    private static async Task<Results<Ok<ApprovalLinkViewDto>, ProblemHttpResult>> GetApprovalLink(
        [MaxLength(TokenMaxLength)] string token,
        ICurrentUser currentUser,
        ApprovalLinkService links,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(token, currentUser, links, db, time, cancellationToken);
        if (opened.Problem is { } problem)
        {
            return problem;
        }

        var (link, gate) = (opened.Link!, opened.Gate!);
        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == gate.ProjectId).Select(p => p.Name).FirstAsync(cancellationToken);
        ApprovalArtifactSummaryDto? artifact = null;
        if (gate.ArtifactId is { } artifactId)
        {
            artifact = await db.Artifacts.AsNoTracking()
                .Where(a => a.Id == artifactId)
                .Select(a => new { a.Type, a.Version, a.Content })
                .FirstOrDefaultAsync(cancellationToken) is { } a
                    ? new ApprovalArtifactSummaryDto(a.Type, a.Version, FirstHeading(a.Content))
                    : null;
        }

        return TypedResults.Ok(new ApprovalLinkViewDto(
            gate.Id,
            gate.ProjectId,
            projectName,
            gate.Type,
            link.Decision,
            artifact,
            link.ExpiresAt,
            gate.Status == GateStatus.Pending ? ApprovalLinkStatus.Valid : ApprovalLinkStatus.GateClosed));
    }

    private static async Task<Results<Ok<GateDto>, ValidationProblem, ProblemHttpResult>> ConfirmApprovalLink(
        [MaxLength(TokenMaxLength)] string token,
        ConfirmApprovalLinkRequest? request,
        ICurrentUser currentUser,
        ApprovalLinkService links,
        AideDbContext db,
        GateService gates,
        GateReader reader,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(token, currentUser, links, db, time, cancellationToken);
        if (opened.Problem is { } problem)
        {
            return problem;
        }

        var (link, gate) = (opened.Link!, opened.Gate!);
        var now = time.GetUtcNow();

        // FR-48: the link is used up in the same transaction as the gate decision. The conditional update makes two
        // concurrent confirmations race on the row: only one marks it, the other gets LINK_USED and changes nothing.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await db.ApprovalLinks
            .Where(l => l.Id == link.Id && l.UsedAt == null && l.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UsedAt, now), cancellationToken);
        if (marked == 0)
        {
            var current = await db.ApprovalLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id, cancellationToken);
            return StateProblem(ApprovalTokens.State(current.ExpiresAt, current.UsedAt, now)) ?? ApiProblems.Of(ErrorCodes.LinkUsed, UsedTitle);
        }

        // The sibling link of the pair (approve ↔ reject) is void once either is used.
        await db.ApprovalLinks
            .Where(l => l.GateId == link.GateId && l.Id != link.Id && l.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UsedAt, now), cancellationToken);

        var decide = new DecideGateRequest { Comment = request?.Comment };
        var userId = currentUser.RequiredUserId;
        var decided = link.Decision == ApprovalLinkDecision.Approve
            ? await gates.ApproveAsync(gate.ProjectId, gate.Id, decide, userId, currentUser.Actor, cancellationToken)
            : await gates.RejectAsync(gate.ProjectId, gate.Id, decide, userId, currentUser.Actor, cancellationToken);
        if (decided.IsFailure)
        {
            // Rolled back with the transaction: the link stays unused.
            return ApiProblems.FromError(decided.Error!);
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await reader.ToDtoAsync(decided.Value, cancellationToken));
    }

    private const string UsedTitle = "This approval link has already been used.";

    private sealed record OpenedLink(ApprovalLink? Link, ApprovalGate? Gate, ProblemHttpResult? Problem);

    /// <summary>Token → link (404), caller is an Owner of the gate's project (403; Admins too), then used (409) / expired (410).</summary>
    private static async Task<OpenedLink> OpenAsync(
        string token,
        ICurrentUser currentUser,
        ApprovalLinkService links,
        AideDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var link = await links.FindAsync(token, cancellationToken);
        var gate = link is null
            ? null
            : await db.ApprovalGates.AsNoTracking().FirstOrDefaultAsync(g => g.Id == link.GateId, cancellationToken);
        if (link is null || gate is null)
        {
            return new OpenedLink(null, null, ApiProblems.NotFound("This approval link is not valid."));
        }

        var userId = currentUser.RequiredUserId;
        var isOwner = await db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.ProjectId == gate.ProjectId && m.UserId == userId && m.Role == ProjectRole.Owner, cancellationToken);
        if (!isOwner)
        {
            return new OpenedLink(null, null, ApiProblems.Forbidden("Only an Owner of the project can use this approval link."));
        }

        return StateProblem(ApprovalTokens.State(link.ExpiresAt, link.UsedAt, time.GetUtcNow())) is { } problem
            ? new OpenedLink(null, null, problem)
            : new OpenedLink(link, gate, null);
    }

    private static ProblemHttpResult? StateProblem(ApprovalLinkState state) => state switch
    {
        ApprovalLinkState.Used => ApiProblems.Of(ErrorCodes.LinkUsed, UsedTitle),
        ApprovalLinkState.Expired => ApiProblems.Of(ErrorCodes.LinkExpired, "This approval link has expired (links last 72 hours)."),
        _ => null,
    };

    /// <summary>The artifact's first Markdown heading, if any (the confirmation page's title).</summary>
    internal static string? FirstHeading(string content)
    {
        foreach (var line in (content ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                var title = trimmed.TrimStart('#').Trim();
                if (title.Length > 0)
                {
                    return title.Length <= 200 ? title : title[..200];
                }
            }
        }

        return null;
    }
}
