using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Notifications;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Notifications;

/// <summary>The approve and reject URLs of a gate's one-click links (absolute, for channel messages only).</summary>
public sealed record ApprovalLinkUrls(string ApproveUrl, string RejectUrl);

/// <summary>FR-48: which gate types get one-click approval links. The pack decides (the SDLC: spec, plan and
/// pre-release gates).</summary>
public interface IApprovalLinkPolicy
{
    bool OffersApprovalLinks(string gateType);
}

/// <summary>
/// FR-48: creates and resolves one-click approval links (<see cref="ApprovalTokens"/>). Links are created as an approve +
/// reject pair when a gate the <see cref="IApprovalLinkPolicy"/> offers links for is announced; only the nonce hash is stored. The URLs go into
/// channel messages only — in-app notifications link to the project page, so a token never sits in the database.
/// Without a usable <c>Approvals:SigningKey</c> nothing is created and every token is unknown (fail closed).
/// </summary>
public sealed partial class ApprovalLinkService(
    IEngineDbContext db,
    IApprovalLinkPolicy policy,
    IOptions<ApprovalOptions> approvals,
    IOptions<AppOptions> app,
    ILogger<ApprovalLinkService> logger)
{
    private readonly string? _key = ApprovalTokens.IsUsableKey(approvals.Value.SigningKey) ? approvals.Value.SigningKey!.Trim() : null;

    public bool Enabled => _key is not null;

    /// <summary>Adds the gate's link pair to the unit of work (not saved) unless it has links already or gets none.</summary>
    public async Task EnsureLinksAsync(ApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!policy.OffersApprovalLinks(gate.Type) || gate.Status != GateStatus.Pending)
        {
            return;
        }

        if (_key is null)
        {
            LogNoSigningKey(logger, gate.Id);
            return;
        }

        if (await db.ApprovalLinks.AnyAsync(l => l.GateId == gate.Id, cancellationToken))
        {
            return;
        }

        foreach (var decision in new[] { ApprovalLinkDecision.Approve, ApprovalLinkDecision.Reject })
        {
            var link = new ApprovalLink
            {
                GateId = gate.Id,
                Decision = decision,
                ExpiresAt = now + ApprovalTokens.Lifetime,
                CreatedAt = now,
            };
            link.NonceHash = ApprovalTokens.NonceHash(ApprovalTokens.Nonce(_key, link.Id));
            db.ApprovalLinks.Add(link);
        }
    }

    /// <summary>The URLs of the gate's unused, unexpired link pair, or <c>null</c> (gate decided, links used/expired, or disabled).</summary>
    public async Task<ApprovalLinkUrls?> UrlsAsync(ApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (_key is null || gate.Status != GateStatus.Pending || !policy.OffersApprovalLinks(gate.Type))
        {
            return null;
        }

        var links = await db.ApprovalLinks.AsNoTracking()
            .Where(l => l.GateId == gate.Id && l.UsedAt == null && l.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        var approve = links.FirstOrDefault(l => l.Decision == ApprovalLinkDecision.Approve);
        var reject = links.FirstOrDefault(l => l.Decision == ApprovalLinkDecision.Reject);
        return approve is null || reject is null
            ? null
            : new ApprovalLinkUrls(Url(approve), Url(reject));
    }

    /// <summary>The link a token names, if the token is genuine (MAC and stored hash match, constant time); else <c>null</c>.</summary>
    public async Task<ApprovalLink?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (_key is null || !ApprovalTokens.TryParse(token, out var linkId, out var nonce))
        {
            return null;
        }

        var link = await db.ApprovalLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);
        return link is not null && ApprovalTokens.Verify(_key, linkId, nonce, link.NonceHash) ? link : null;
    }

    // FR-326: a link created before 0.8.0 keeps its pre-0.8.0 token (its stored hash is of that nonce).
    private string Url(ApprovalLink link) => app.Value.Absolute($"/approvals/{ApprovalTokens.Create(_key!, link.Id, link.NonceHash)}");

    [LoggerMessage(Level = LogLevel.Warning, Message = "No one-click approval links for gate {GateId}: Approvals:SigningKey is not set (or shorter than 32 characters).")]
    private static partial void LogNoSigningKey(ILogger logger, Guid gateId);
}
