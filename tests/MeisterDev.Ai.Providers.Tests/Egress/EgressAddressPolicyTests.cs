// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Egress;

namespace MeisterDev.Ai.Providers.Tests.Egress;

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
    [InlineData("192.0.0.1")] // IETF protocol assignments 192.0.0.0/24
    [InlineData("192.0.2.1")] // TEST-NET-1
    [InlineData("192.88.99.1")] // 6to4 relay anycast
    [InlineData("198.18.0.1")] // benchmarking 198.18.0.0/15
    [InlineData("198.19.255.254")] // benchmarking, upper half
    [InlineData("198.51.100.1")] // TEST-NET-2
    [InlineData("203.0.113.1")] // TEST-NET-3
    [InlineData("2001:db8::1")] // IPv6 documentation
    [InlineData("fec0::1")] // site-local, deprecated and still blocked
    [InlineData("2001:2::1")] // IPv6 benchmarking
    [InlineData("100::1")] // IPv6 discard-only
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
    [InlineData("192.0.1.1")] // just above 192.0.0.0/24
    [InlineData("192.0.3.1")] // just above TEST-NET-1
    [InlineData("192.88.98.1")] // just below the 6to4 relay block
    [InlineData("198.17.255.254")] // just below benchmarking
    [InlineData("198.20.0.1")] // just above benchmarking
    [InlineData("198.51.101.1")] // just above TEST-NET-2
    [InlineData("203.0.114.1")] // just above TEST-NET-3
    [InlineData("2001:db9::1")] // just above the IPv6 documentation prefix
    [InlineData("2001:3::1")] // just above IPv6 benchmarking
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

    // An address carrying userinfo holds a credential in a field nothing treats as one: it is stored in the
    // clear, it is not what any family reads its credential from, and it travels into anything quoting the
    // address. The rule reaches every value the installation checks, so a declared address gets it too.
    [Theory]
    [InlineData("https://someone@api.openai.com/v1")]
    [InlineData("https://someone:sk-live-0123456789@api.openai.com/v1")]
    [InlineData("https://:sk-live-0123456789@api.openai.com/v1")]
    public void AnAddressCarryingACredentialIsRefused(string url)
    {
        var refusal = EgressUrlPolicy.Locked.GetRefusalReason(url, "baseUrl");

        Assert.NotNull(refusal);
        Assert.Contains("must not carry a credential in the address", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressWithNoUserinfoIsStillPermitted()
    {
        Assert.Null(EgressUrlPolicy.Locked.GetRefusalReason("https://api.openai.com/v1", "baseUrl"));
    }

    // The carve-out is about the scheme and the address. A credential in the address is stored in the clear
    // beside the profile whatever host it names, so the carve-out does not reach it.
    [Theory]
    [InlineData("http://someone:sk-live-0123456789@127.0.0.1:8976/callback")]
    [InlineData("http://someone@localhost:8976/callback")]
    public void TheLoopbackCarveOutStillRefusesACredentialInTheAddress(string url)
    {
        var refusal = EgressUrlPolicy.Locked.GetRefusalReason(url, "callbackUrl", admitLoopbackOverHttp: true);

        Assert.NotNull(refusal);
        Assert.Contains("must not carry a credential in the address", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoopbackCarveOutStillAdmitsAPlainCallback()
    {
        Assert.Null(
            EgressUrlPolicy.Locked.GetRefusalReason(
                "http://127.0.0.1:8976/callback",
                "callbackUrl",
                admitLoopbackOverHttp: true));
    }
}
