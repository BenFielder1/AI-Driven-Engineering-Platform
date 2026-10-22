using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.Api.Features.Connections;

/// <summary>A project that has an LLM connection bound (FR-79).</summary>
public sealed record LlmBoundProjectDto(Guid Id, string Name);

/// <summary>
/// One of the caller's LLM connections (FR-78). The key is never returned: only the provider, label, its last 4
/// characters, the validation status (<c>valid</c>, <c>invalid</c>, <c>unchecked</c>) and when it was last checked, plus
/// the projects it is bound to. <c>last4</c> is <c>????</c> for a key migrated from v1 until the start-up backfill reads it.
/// </summary>
public sealed record LlmCredentialDto(
    Guid Id,
    LlmProvider Provider,
    string Label,
    string Last4,
    LlmCredentialStatus Status,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LlmBoundProjectDto> BoundProjects);

/// <summary>
/// The caller's LLM connections, newest first (FR-78), and <c>keylessProviders</c>: the providers that need no key and
/// are always available (Ollama at the system base URL, and Fake).
/// </summary>
public sealed record LlmConnectionsDto(IReadOnlyList<LlmCredentialDto> Credentials, IReadOnlyList<LlmProvider> KeylessProviders);

/// <summary>Add an LLM connection (FR-78): <c>anthropic</c> or <c>openai</c>, a label and the key. The key is checked with
/// the provider before anything is saved.</summary>
public sealed record CreateLlmCredentialRequest
{
    public required LlmProvider Provider { get; init; }

    [Required]
    [MaxLength(LlmConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }

    [Required]
    [MaxLength(LlmConnectionRules.MaxApiKeyLength)]
    public required string ApiKey { get; init; }
}

/// <summary>Rename an LLM connection (FR-78).</summary>
public sealed record UpdateLlmCredentialRequest
{
    [Required]
    [MaxLength(LlmConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }
}
