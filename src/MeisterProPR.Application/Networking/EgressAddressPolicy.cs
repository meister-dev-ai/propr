// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;

namespace MeisterProPR.Application.Networking;

/// <summary>
///     Classifies IP addresses (and literal-IP hosts) that server-side outbound requests must not reach —
///     loopback, private, link-local (including cloud metadata), CGNAT, and other non-public ranges — so a
///     user- or admin-supplied URL cannot be turned into a request against internal infrastructure (SSRF).
/// </summary>
public static class EgressAddressPolicy
{
    /// <summary>Returns <c>true</c> when <paramref name="address" /> must not be the target of an outbound request.</summary>
    /// <param name="address">The resolved destination address.</param>
    public static bool IsBlockedEgressAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Unwrap IPv4-mapped IPv6 (::ffff:a.b.c.d) and evaluate it as IPv4 so a mapped private
        // address cannot slip past the IPv4 range checks.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIpv4(address),
            AddressFamily.InterNetworkV6 => IsBlockedIpv6(address),
            _ => true, // Unknown address families are not safe to route to.
        };
    }

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="host" /> is a literal IP address in a blocked range. A
    ///     non-IP hostname returns <c>false</c> — it requires DNS resolution, which is enforced at connect time.
    /// </summary>
    /// <param name="host">The URL host component.</param>
    public static bool IsBlockedEgressHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Normalize URL-host forms before parsing: a trailing FQDN dot (e.g. "127.0.0.1.") and a bracketed
        // IPv6 literal (e.g. "[::1]") both otherwise defeat IPAddress.TryParse.
        var candidate = host.TrimEnd('.');
        if (candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']')
        {
            candidate = candidate[1..^1];
        }

        return IPAddress.TryParse(candidate, out var address) && IsBlockedEgressAddress(address);
    }

    private static bool IsBlockedIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 => true, // 0.0.0.0/8 "this host"
            10 => true, // RFC 1918
            127 => true, // loopback 127.0.0.0/8
            100 when bytes[1] is >= 64 and <= 127 => true, // CGNAT 100.64.0.0/10
            169 when bytes[1] == 254 => true, // link-local 169.254.0.0/16 (incl. 169.254.169.254 metadata)
            172 when bytes[1] is >= 16 and <= 31 => true, // RFC 1918
            192 when bytes[1] == 168 => true, // RFC 1918
            >= 224 => true, // multicast 224.0.0.0/4 + reserved/broadcast 240.0.0.0/4
            _ => false,
        };
    }

    private static bool IsBlockedIpv6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) // ::1
            || address.Equals(IPAddress.IPv6Any) // ::
            || address.IsIPv6LinkLocal // fe80::/10
            || address.IsIPv6SiteLocal // fec0::/10 (deprecated but still blocked)
            || address.IsIPv6Multicast // ff00::/8
            || IsUniqueLocalIpv6(address)) // fc00::/7
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        // Teredo 2001:0000::/32 tunnels an (obfuscated) IPv4 client + relay — block outright.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return true;
        }

        // NAT64 / 6to4 / IPv4-compatible forms embed an IPv4 target that a translation gateway will route
        // to; extract that IPv4 and re-check it so an internal address cannot hide inside an IPv6 literal.
        return TryGetEmbeddedIpv4(bytes, out var embedded) && IsBlockedIpv4(embedded);
    }

    private static bool TryGetEmbeddedIpv4(byte[] bytes, out IPAddress embedded)
    {
        embedded = IPAddress.None;

        // NAT64 well-known prefix 64:ff9b::/96 → the low 32 bits are the embedded IPv4.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B
            && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0
            && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
        {
            embedded = new IPAddress(bytes[12..16]);
            return true;
        }

        // 6to4 2002::/16 → bytes 2..6 are the embedded IPv4.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            embedded = new IPAddress(bytes[2..6]);
            return true;
        }

        // IPv4-compatible ::a.b.c.d (deprecated): the ::/96 prefix with a low word other than :: / ::1.
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0
            && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0
            && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0
            && !(bytes[12] == 0 && bytes[13] == 0 && bytes[14] == 0 && (bytes[15] == 0 || bytes[15] == 1)))
        {
            embedded = new IPAddress(bytes[12..16]);
            return true;
        }

        return false;
    }

    private static bool IsUniqueLocalIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }
}
