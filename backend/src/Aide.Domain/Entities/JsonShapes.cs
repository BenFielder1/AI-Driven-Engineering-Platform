using Aide.Domain.Artifacts;
using Aide.Domain.Execution;
using Aide.Domain.Review;

namespace Aide.Domain.Entities;

// Shapes of the jsonb columns. Enums inside them use the same snake_case wire values as columns.

/// <summary>A file written by an accepted step (FR-34): path, action and SHA-256 of the content.</summary>
public sealed record FileManifestEntry(string Path, FileAction Action, string ContentHash);

/// <summary>An artifact version an agent run used as input (FR-23).</summary>
public sealed record InputArtifactRef(Guid ArtifactId, ArtifactType Type, int Version);

/// <summary>A compliance category score with the agent's evidence (FR-38, §4.7).</summary>
public sealed record CategoryScoreEntry(ReviewCategory Category, decimal Score, string? Evidence = null);

/// <summary>A drift area reported by the review agent (§4.7).</summary>
public sealed record DriftAreaEntry(string Area, Severity Severity, string Description);

/// <summary>A configured project notification channel (FR-47). The secret is encrypted with Data Protection.</summary>
public sealed record NotificationChannelConfig(
    NotificationChannelType Type,
    string Target,
    string? SecretEncrypted,
    IReadOnlyList<string> Events);
