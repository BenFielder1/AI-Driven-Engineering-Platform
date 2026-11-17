using System.Net;
using System.Net.Sockets;

namespace Aide.Domain.Notifications;

/// <summary>
/// NFR-SEC (SSRF): the addresses a notification channel may connect to. Channel URLs are entered by project Owners, so
/// the API pod must never be steered at itself, the cluster, the host or a cloud metadata service: loopback, private
/// (RFC 1918), shared/CGNAT, link-local (incl. 169.254.169.254), unique-local, multicast, reserved, documentation and
/// unspecified ranges are refused, for IPv4 and IPv6, including IPv4 embedded in IPv6 (mapped, compatible, NAT64, 6to4).
/// </summary>
public static class NetworkRules
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicV4(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false,
        };
    }

    private static bool IsPublicV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return !(b[0] == 0                                        // 0.0.0.0/8 "this network"
            || b[0] == 10                                         // 10/8 private
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)         // 100.64/10 shared (CGNAT; Alibaba metadata)
            || b[0] == 127                                        // loopback
            || (b[0] == 169 && b[1] == 254)                       // link-local, cloud metadata
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)          // 172.16/12 private
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)            // 192.0.0/24 IETF protocol assignments
            || (b[0] == 192 && b[1] == 0 && b[2] == 2)            // TEST-NET-1
            || (b[0] == 192 && b[1] == 88 && b[2] == 99)          // 6to4 relay anycast
            || (b[0] == 192 && b[1] == 168)                       // 192.168/16 private
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))        // 198.18/15 benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)         // TEST-NET-2
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)          // TEST-NET-3
            || b[0] >= 224);                                      // multicast 224/4, reserved 240/4, broadcast
    }

    private static bool IsPublicV6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC)
        {
            return false; // fc00::/7 unique local (incl. fd00:ec2::254 metadata)
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
        {
            return false; // 2001:db8::/32 documentation
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            return false; // 2001::/32 Teredo (tunnels to arbitrary IPv4)
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(new IPAddress(b.AsSpan(2, 4))); // 2002::/16 6to4 embeds an IPv4 address
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
        {
            return IsPublicV4(new IPAddress(b.AsSpan(12, 4))); // 64:ff9b::/96 NAT64
        }

        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            return false; // ::/96 IPv4-compatible (deprecated) and other ::x.x.x.x forms
        }

        if (b[0] == 0x01 && b[1] == 0x00 && b.AsSpan(2, 6).IndexOfAnyExcept((byte)0) < 0)
        {
            return false; // 100::/64 discard-only
        }

        return true;
    }
}
