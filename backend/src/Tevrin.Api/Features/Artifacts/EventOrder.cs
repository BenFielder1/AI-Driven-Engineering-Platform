using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Api.Features.Artifacts;

/// <summary>
/// D-0.7.0-28: the order of per-row events written in one save (they share a timestamp, so their v7 ids give the order).
/// 0.6.1 took it from unordered queries, i.e. the table's physical row order; it is now explicit and reproduces the
/// characterisation snapshots.
/// </summary>
public static class EventOrder
{
    /// <summary><c>ARTIFACT_INVALIDATED</c> (cascade and rollback): newest artifact first (the most downstream one), then
    /// highest version, then id.</summary>
    public static IEnumerable<Artifact> Invalidated(IEnumerable<Artifact> artifacts) =>
        artifacts.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Version).ThenByDescending(a => a.Id);

    /// <summary><c>GATE_CANCELLED</c>: oldest gate first, then id.</summary>
    public static IEnumerable<ApprovalGate> Cancelled(IEnumerable<ApprovalGate> gates) =>
        gates.OrderBy(g => g.CreatedAt).ThenBy(g => g.Id);

    /// <summary><c>ARTIFACT_SUPERSEDED</c>: lowest version first.</summary>
    public static IEnumerable<Artifact> Superseded(IEnumerable<Artifact> drafts) =>
        drafts.OrderBy(a => a.Version).ThenBy(a => a.Id);
}
