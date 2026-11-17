namespace Aide.Domain.Artifacts;

/// <summary>
/// Result of re-checking an artifact's content hash on read (FR-20). <c>corrupted</c> means the stored content no
/// longer matches <c>content_hash</c> (§4.3), or the row is already marked <see cref="ArtifactStatus.Corrupted"/>.
/// </summary>
public enum ArtifactIntegrity
{
    Ok = 0,
    Corrupted = 1,
}

/// <summary>The kind of one line of a line diff between two artifact versions (FR-21).</summary>
public enum DiffLineKind
{
    /// <summary>Unchanged; present in both versions.</summary>
    Context = 0,

    /// <summary>Only in the newer (<c>to</c>) version.</summary>
    Added = 1,

    /// <summary>Only in the older (<c>from</c>) version.</summary>
    Removed = 2,

    /// <summary>
    /// A run of unchanged lines left out of the structured diff (more than <see cref="LineDiff.DefaultContext"/> lines
    /// away from any change). <see cref="DiffLine.Skipped"/> is how many; the line numbers are those of the first one.
    /// </summary>
    Skipped = 3,
}
