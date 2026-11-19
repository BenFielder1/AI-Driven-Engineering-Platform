namespace Tevrin.Engine.Domain.Artifacts;

/// <summary>Stored artifact status (§4.3). <c>approved</c> is a moment, not a stored status.</summary>
public enum ArtifactStatus
{
    Draft = 0,
    Locked = 1,
    Rejected = 2,
    Superseded = 3,
    Invalidated = 4,
    Corrupted = 5,
}
