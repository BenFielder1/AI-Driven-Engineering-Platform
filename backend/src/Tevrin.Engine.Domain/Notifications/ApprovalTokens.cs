using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Aide.Domain.Notifications;

/// <summary>Why an approval link can or can't be used now.</summary>
public enum ApprovalLinkState
{
    Valid = 0,
    Used = 1,
    Expired = 2,
}

/// <summary>
/// FR-48, NFR-SEC: one-click approval link tokens, <c>&lt;link id (32 hex)&gt;.&lt;nonce (base64url)&gt;</c>. The 256-bit
/// nonce is HMAC-SHA256(<c>Approvals:SigningKey</c>, link id): unguessable without the key, and re-derivable from the
/// link id when a delivery is retried, so the raw token is never stored. The database keeps only SHA-256(nonce) as
/// <c>nonce_hash</c>. A token is accepted only when its nonce matches the key's MAC <em>and</em> the stored hash, both
/// compared in constant time. Rotating the signing key invalidates outstanding links.
/// </summary>
public static class ApprovalTokens
{
    /// <summary>A link expires 72 hours after it is created.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>A signing key needs at least this many characters; a shorter one is treated as missing (no links).</summary>
    public const int MinimumKeyLength = 32;

    private const string Purpose = "aide-approval-link/v1:";
    private const int NonceLength = 32;

    public static bool IsUsableKey(string? key) => key is not null && key.Trim().Length >= MinimumKeyLength;

    public static byte[] Nonce(string signingKey, Guid linkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(signingKey);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(Purpose + linkId.ToString("N")));
    }

    /// <summary>What <c>approval_links.nonce_hash</c> stores: lowercase hex SHA-256 of the nonce.</summary>
    public static string NonceHash(ReadOnlySpan<byte> nonce) => Convert.ToHexStringLower(SHA256.HashData(nonce));

    public static string Create(string signingKey, Guid linkId) =>
        $"{linkId:N}.{Base64Url.EncodeToString(Nonce(signingKey, linkId))}";

    /// <summary>Splits a token; <c>false</c> for anything malformed.</summary>
    public static bool TryParse(string? token, out Guid linkId, out byte[] nonce)
    {
        linkId = Guid.Empty;
        nonce = [];
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot != 32 || !Guid.TryParseExact(token.AsSpan(0, 32), "N", out linkId))
        {
            return false;
        }

        var encoded = token.AsSpan(dot + 1);
        if (!Base64Url.IsValid(encoded, out var length) || length != NonceLength)
        {
            return false;
        }

        nonce = Base64Url.DecodeFromChars(encoded);
        return nonce.Length == NonceLength;
    }

    /// <summary>Whether <paramref name="nonce"/> is the key's MAC for the link and hashes to the stored value (constant time).</summary>
    public static bool Verify(string signingKey, Guid linkId, ReadOnlySpan<byte> nonce, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(storedHash);
        var macMatches = CryptographicOperations.FixedTimeEquals(Nonce(signingKey, linkId), nonce);
        var hashMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(NonceHash(nonce)),
            Encoding.ASCII.GetBytes(storedHash));
        return macMatches & hashMatches;
    }

    /// <summary>A used link stays used (FR-48 "already used") even after it would have expired.</summary>
    public static ApprovalLinkState State(DateTimeOffset expiresAt, DateTimeOffset? usedAt, DateTimeOffset now) =>
        usedAt is not null ? ApprovalLinkState.Used
        : now >= expiresAt ? ApprovalLinkState.Expired
        : ApprovalLinkState.Valid;
}
