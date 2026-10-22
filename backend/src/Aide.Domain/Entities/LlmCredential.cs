namespace Aide.Domain.Entities;

/// <summary>A user's LLM provider key (FR-78, D-V2-5); table <c>llm_credentials</c>. Projects bind one through
/// <see cref="ProjectLlmSettings.CredentialId"/> (FR-79). The key is encrypted at rest and never returned.</summary>
public sealed class LlmCredential
{
    /// <summary><see cref="Last4"/> of a key migrated from v1 before its plaintext was read (it can't be computed in SQL).</summary>
    public const string UnknownLast4 = "????";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>Only providers that need a key: <see cref="LlmProvider.Anthropic"/> or <see cref="LlmProvider.Openai"/>
    /// (a check constraint enforces it).</summary>
    public LlmProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext of the key (<c>SecretPurposes.LlmApiKey</c>).</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>The key's last 4 characters, shown as <c>••••a1b2</c>; <see cref="UnknownLast4"/> until backfilled.</summary>
    public string Last4 { get; set; } = UnknownLast4;

    public LlmCredentialStatus Status { get; set; } = LlmCredentialStatus.Unchecked;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The last 4 characters of a key (the whole key if shorter).</summary>
    public static string Last4Of(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length <= 4 ? key : key[^4..];
    }
}

/// <summary>Validation state of an LLM credential (FR-78). Wire values: <c>valid</c>, <c>invalid</c>, <c>unchecked</c>.</summary>
public enum LlmCredentialStatus
{
    Valid = 0,
    Invalid = 1,
    Unchecked = 2,
}
