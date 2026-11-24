using System.Security.Cryptography;
using System.Text;

namespace Tevrin.Engine.Domain.Notifications;

/// <summary>
/// FR-47, NFR-SEC: generic webhook bodies are signed with the channel's secret. The <c>X-Tevrin-Signature</c> header is
/// <c>sha256=&lt;lowercase hex HMAC-SHA256 of the raw request body&gt;</c>. The body carries its own <c>id</c> and
/// <c>timestamp</c> (also sent as <c>X-Tevrin-Delivery</c> / <c>X-Tevrin-Timestamp</c>), so receivers can reject replays.
/// FR-326 (§4.77): through 0.8.x every delivery also carries the pre-0.8.0 <c>X-AIDE-*</c> headers with identical values
/// (<see cref="LegacyHeaders"/>), so receivers that check the old names keep working; they are dropped in 0.9.0.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Tevrin-Signature";
    public const string TimestampHeaderName = "X-Tevrin-Timestamp";
    public const string DeliveryHeaderName = "X-Tevrin-Delivery";
    public const string EventHeaderName = "X-Tevrin-Event";

    /// <summary>FR-326: the pre-0.8.0 signature header, sent alongside <see cref="HeaderName"/> until 0.9.0.</summary>
    public const string LegacyHeaderName = "X-AIDE-Signature";
    public const string LegacyTimestampHeaderName = "X-AIDE-Timestamp";
    public const string LegacyDeliveryHeaderName = "X-AIDE-Delivery";
    public const string LegacyEventHeaderName = "X-AIDE-Event";

    /// <summary>FR-326: each header and its pre-0.8.0 twin, which carries the same value.</summary>
    public static IReadOnlyList<(string Name, string LegacyName)> LegacyHeaders { get; } =
    [
        (HeaderName, LegacyHeaderName),
        (TimestampHeaderName, LegacyTimestampHeaderName),
        (DeliveryHeaderName, LegacyDeliveryHeaderName),
        (EventHeaderName, LegacyEventHeaderName),
    ];
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
