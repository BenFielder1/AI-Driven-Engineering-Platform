using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Api.Pipelines;

/// <summary>
/// FR-334: artifacts and gates of a run without a subject have no release version and belong to the run alone. The SDLC
/// keeps its release-version subject, so SDLC code reads only rows that have one: queries start from
/// <see cref="Sdlc(IQueryable{Artifact})"/> / <see cref="Sdlc(IQueryable{ApprovalGate})"/>, and a row's release version is
/// read through <see cref="ReleaseVersion"/>.
/// </summary>
internal static class SdlcRows
{
    /// <summary>The SDLC's artifacts: those with a release version.</summary>
    public static IQueryable<Artifact> Sdlc(this IQueryable<Artifact> artifacts) => artifacts.Where(a => a.ReleaseVersionId != null);

    /// <summary>The SDLC's gates: those with a release version.</summary>
    public static IQueryable<ApprovalGate> Sdlc(this IQueryable<ApprovalGate> gates) => gates.Where(g => g.ReleaseVersionId != null);

    /// <summary>The release version of an SDLC artifact or gate; throws when the row belongs to a subject-less run.</summary>
    public static Guid ReleaseVersion(Guid? releaseVersionId) =>
        releaseVersionId ?? throw new InvalidOperationException("An SDLC artifact or gate has no release version (FR-334: it belongs to a run without a subject).");
}
