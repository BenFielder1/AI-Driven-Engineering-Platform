using System.Security.Cryptography;
using System.Text;

namespace Aide.Domain.Notifications;

/// <summary>
/// FR-47, NFR-SEC: generic webhook bodies are signed with the channel's secret. The <c>X-AIDE-Signature</c> header is
/// <c>sha256=&lt;lowercase hex HMAC-SHA256 of the raw request body&gt;</c>. The body carries its own <c>id</c> and
/// <c>timestamp</c> (also sent as <c>X-AIDE-Delivery</c> / <c>X-AIDE-Timestamp</c>), so receivers can reject replays.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-AIDE-Signature";
    public const string TimestampHeaderName = "X-AIDE-Timestamp";
    public const string DeliveryHeaderName = "X-AIDE-Delivery";
    public const string EventHeaderName = "X-AIDE-Event";
    public const string IdempotencyHeaderName = "Idempotency-Key";
    public const string Prefix = "sha256=";

    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Prefix + Convert.ToHexStringLower(mac);
    }

    /// <summary>What a receiver does: recompute and compare in constant time.</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || header is null)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, body));
        var actual = Encoding.ASCII.GetBytes(header);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
