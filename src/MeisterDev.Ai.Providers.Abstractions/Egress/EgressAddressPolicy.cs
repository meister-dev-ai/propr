// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     Classifies IP addresses (and literal-IP hosts) that server-side outbound requests must not reach —
///     loopback, private, link-local (including cloud metadata), CGNAT, and other non-public ranges — so a
///     user- or admin-supplied URL cannot be turned into a request against internal infrastructure (SSRF).
/// </summary>
public static class EgressAddressPolicy
{
    // Every range an outbound request may not reach, as CIDR. Adding or withdrawing one is an edit to this
    // table and to nothing else, and each line reads as the range it blocks.
    private static readonly IPNetwork[] BlockedIpv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"), // "this host on this network"
        IPNetwork.Parse("10.0.0.0/8"), // RFC 1918 private
        IPNetwork.Parse("100.64.0.0/10"), // CGNAT
        IPNetwork.Parse("127.0.0.0/8"), // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local, and with it 169.254.169.254 cloud metadata
        IPNetwork.Parse("172.16.0.0/12"), // RFC 1918 private
        IPNetwork.Parse("192.0.0.0/24"), // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"), // TEST-NET-1
        IPNetwork.Parse("192.88.99.0/24"), // 6to4 relay anycast
        IPNetwork.Parse("192.168.0.0/16"), // RFC 1918 private
        IPNetwork.Parse("198.18.0.0/15"), // benchmarking
        IPNetwork.Parse("198.51.100.0/24"), // TEST-NET-2
        IPNetwork.Parse("203.0.113.0/24"), // TEST-NET-3
        IPNetwork.Parse("224.0.0.0/3"), // multicast, reserved and broadcast
    ];

    private static readonly IPNetwork[] BlockedIpv6 =
    [
        IPNetwork.Parse("::/128"), // unspecified
        IPNetwork.Parse("::1/128"), // loopback
        IPNetwork.Parse("100::/64"), // discard-only, drops everything sent to it
        IPNetwork.Parse("2001::/32"), // Teredo, tunnels an obfuscated IPv4 client and relay
        IPNetwork.Parse("2001:2::/48"), // benchmarking
        IPNetwork.Parse("2001:db8::/32"), // documentation
        IPNetwork.Parse("fc00::/7"), // unique-local
        IPNetwork.Parse("fe80::/10"), // link-local
        IPNetwork.Parse("fec0::/10"), // site-local, deprecated and still blocked
        IPNetwork.Parse("ff00::/8"), // multicast
    ];

    // The prefixes that carry an IPv4 address inside an IPv6 one, with the byte the address starts at.
    private static readonly (IPNetwork Range, int AddressStart)[] Ipv4Carrying =
    [
        (IPNetwork.Parse("64:ff9b::/96"), 12), // NAT64 well-known prefix
        (IPNetwork.Parse("2002::/16"), 2), // 6to4
        (IPNetwork.Parse("::/96"), 12), // IPv4-compatible, deprecated
    ];

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
        return IsInAny(BlockedIpv4, address);
    }

    private static bool IsBlockedIpv6(IPAddress address)
    {
        if (IsInAny(BlockedIpv6, address))
        {
            return true;
        }

        // The carrier prefixes hold an IPv4 address that a translation gateway routes to, so the address
        // reached is the one inside. It is read out and measured against the IPv4 table, so an internal
        // address cannot hide inside an IPv6 literal.
        foreach (var (range, start) in Ipv4Carrying)
        {
            if (range.Contains(address))
            {
                return IsBlockedIpv4(new IPAddress(address.GetAddressBytes()[start..(start + 4)]));
            }
        }

        return false;
    }

    private static bool IsInAny(IPNetwork[] ranges, IPAddress address)
    {
        foreach (var range in ranges)
        {
            if (range.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
