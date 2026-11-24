using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tevrin.Engine.Domain.Triggers;

/// <summary>
/// FR-337, §4.72: the webhook signature <c>X-Tevrin-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret,
/// "&lt;t&gt;.&lt;raw body&gt;")&gt;</c>. A call verifies when <c>|now − t| ≤ 300 s</c> and the <c>v1</c> value equals the
/// HMAC under one of the accepted secrets (the current one, and the previous one during its 24-hour overlap), compared in
/// constant time.
/// </summary>
public static class PipelineWebhookSignature
{
    public const string SignatureHeader = "X-Tevrin-Signature";
    public const string DeliveryHeader = "X-Tevrin-Delivery";

    /// <summary>The largest clock difference accepted (§4.72).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    /// <summary>How long a rotated secret stays valid (FR-337).</summary>
    public static readonly TimeSpan RotationOverlap = TimeSpan.FromHours(24);

    /// <summary>The lowercase hex HMAC-SHA256 of <c>"&lt;t&gt;.&lt;body&gt;"</c> under <paramref name="secret"/> (UTF-8 key).</summary>
    public static string Compute(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>The header value for <paramref name="body"/> signed at <paramref name="timestamp"/>.</summary>
    public static string Header(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Compute(secret, timestamp, body)}";

    /// <summary>
    /// Whether <paramref name="header"/> is a valid signature of <paramref name="body"/> at <paramref name="now"/> under any of
    /// <paramref name="secrets"/>. Every malformed, stale or wrong header is <c>false</c>. Several <c>v1</c> entries are
    /// allowed (any may match).
    /// </summary>
    public static bool Verify(string? header, ReadOnlySpan<byte> body, IReadOnlyCollection<string> secrets, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrEmpty(header) || header.Length > 1024 || secrets.Count == 0)
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return false;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key == "t")
            {
                if (timestamp is not null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                {
                    return false;
                }

                timestamp = t;
            }
            else if (key == "v1")
            {
                if (value.Length != 64 || !IsHex(value))
                {
                    return false;
                }

                signatures.Add(Convert.FromHexString(value));
            }
        }

        if (timestamp is not { } ts || signatures.Count == 0 || !IsFresh(ts, now))
        {
            return false;
        }

        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Convert.FromHexString(Compute(secret, ts, body));
            foreach (var signature in signatures)
            {
                // Constant time per comparison; every pair is compared so timing doesn't reveal which secret matched.
                matched |= CryptographicOperations.FixedTimeEquals(expected, signature);
            }
        }

        return matched;
    }

    /// <summary><c>|now − t| ≤ 300 s</c>.</summary>
    public static bool IsFresh(long timestamp, DateTimeOffset now)
    {
        var nowSeconds = now.ToUnixTimeSeconds();
        return Math.Abs((decimal)nowSeconds - timestamp) <= (decimal)Tolerance.TotalSeconds;
    }

    /// <summary>The secrets a call may be signed with: the current one, and the previous one until it expires.</summary>
    public static IReadOnlyList<string> AcceptedSecrets(string current, string? previous, DateTimeOffset? previousExpiresAt, DateTimeOffset now) =>
        previous is not null && previousExpiresAt is { } expires && now < expires ? [current, previous] : [current];

    private static bool IsHex(string value) => value.All(char.IsAsciiHexDigit);
}
