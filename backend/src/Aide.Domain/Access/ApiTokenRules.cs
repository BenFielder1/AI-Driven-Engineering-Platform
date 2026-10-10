using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Common;

namespace Aide.Domain.Access;

/// <summary>FR-4: personal API tokens. The plain token is <c>aide_pat_</c> + base64url(32 random bytes), shown once;
/// only its SHA-256 hash (lowercase hex) is stored.</summary>
public static class ApiTokenRules
{
    public const string Prefix = "aide_pat_";

    /// <summary>Random bytes in a token.</summary>
    public const int SecretBytes = 32;

    public const int MaxNameLength = 100;

    /// <summary><c>last_used_at</c> is written at most this often per token.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    // base64url of 32 bytes without padding is 43 characters.
    private const int EncodedSecretLength = 43;

    private static readonly SearchValues<char> Base64UrlAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>Builds the plain token from <see cref="SecretBytes"/> random bytes (randomness is the caller's seam).</summary>
    public static string Format(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"A token secret is {SecretBytes} bytes.", nameof(secret));
        }

        return Prefix + Base64Url.EncodeToString(secret);
    }

    /// <summary>Cheap shape check before any database lookup.</summary>
    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == Prefix.Length + EncodedSecretLength
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && !token.AsSpan(Prefix.Length).ContainsAnyExcept(Base64UrlAlphabet);

    /// <summary>The stored form: SHA-256 of the UTF-8 token, lowercase hex (64 characters).</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>A token authenticates only while it is neither revoked nor expired.</summary>
    public static bool IsUsable(DateTimeOffset? revokedAt, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        revokedAt is null && (expiresAt is null || expiresAt > now);

    /// <summary>Whether a use at <paramref name="now"/> should be written to <c>last_used_at</c> (throttled).</summary>
    public static bool ShouldRecordUse(DateTimeOffset? lastUsedAt, DateTimeOffset now) =>
        lastUsedAt is null || now - lastUsedAt.Value >= LastUsedResolution;

    /// <summary>Validates a create request: a non-blank name of at most 100 characters, and an expiry in the future.</summary>
    public static Result ValidateNew(string? name, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("name: A token name is required.");
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            problems.Add($"name: A token name is at most {MaxNameLength} characters.");
        }

        if (expiresAt is { } expiry && expiry <= now)
        {
            problems.Add("expiresAt: The expiry must be in the future.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The token request is invalid.") { Details = problems });
    }
}
