// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Networking;

namespace MeisterProPR.Application.Tests.Networking;

public sealed class EgressAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.5.5")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")] // CGNAT 100.64/10
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("::1")] // IPv6 loopback
    [InlineData("::")] // IPv6 unspecified
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // ULA
    [InlineData("fd12:3456::1")] // ULA
    [InlineData("ff02::1")] // multicast
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped private
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped metadata
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64-embedded 169.254.169.254 metadata
    [InlineData("64:ff9b::a00:1")] // NAT64-embedded 10.0.0.1
    [InlineData("2002:7f00:1::")] // 6to4-embedded 127.0.0.1
    [InlineData("2002:a00:1::")] // 6to4-embedded 10.0.0.1
    [InlineData("2001::")] // Teredo 2001:0000::/32
    [InlineData("::7f00:1")] // IPv4-compatible ::127.0.0.1
    public void IsBlockedEgressAddress_ForInternalAddress_ReturnsTrue(string ip)
    {
        Assert.True(EgressAddressPolicy.IsBlockedEgressAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("140.82.121.4")] // github
    [InlineData("172.15.0.1")] // just below RFC1918 172.16/12
    [InlineData("172.32.0.1")] // just above RFC1918 172.16/12
    [InlineData("100.63.0.1")] // just below CGNAT 100.64/10
    [InlineData("2606:4700:4700::1111")] // cloudflare v6
    [InlineData("::ffff:8.8.8.8")] // IPv4-mapped public
    [InlineData("64:ff9b::808:808")] // NAT64-embedded public 8.8.8.8 stays allowed
    public void IsBlockedEgressAddress_ForPublicAddress_ReturnsFalse(string ip)
    {
        Assert.False(EgressAddressPolicy.IsBlockedEgressAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1.", true)] // trailing FQDN dot is normalized away
    [InlineData("10.1.2.3", true)]
    [InlineData("[::1]", true)] // bracketed IPv6 URL-host form is unwrapped and blocked
    [InlineData("8.8.8.8", false)]
    [InlineData("api.openai.com", false)] // hostname → false (DNS enforced at connect time)
    [InlineData("localhost", false)] // hostname → false (not a literal IP)
    [InlineData("", false)]
    public void IsBlockedEgressHost_ClassifiesLiteralIpsOnly(string host, bool expected)
    {
        Assert.Equal(expected, EgressAddressPolicy.IsBlockedEgressHost(host));
    }
}
