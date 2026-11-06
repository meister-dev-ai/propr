// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Hosting;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace MeisterDev.ProPR.Api.Tests.Hosting;

public sealed class TrustedProxyNetworksTests
{
    [Fact]
    public void Parse_WithNetworksInCidrNotation_ReturnsEveryNetwork()
    {
        IPNetwork[] expected =
        [
            IPNetwork.Parse("100.100.0.0/17"),
            IPNetwork.Parse("100.100.128.0/19"),
            IPNetwork.Parse("2001:db8::/32"),
            IPNetwork.Parse("203.0.113.7/32"),
        ];

        var networks = TrustedProxyNetworks.Parse(" 100.100.0.0/17, 100.100.128.0/19 ,2001:db8::/32,203.0.113.7/32 ");

        Assert.Equal(expected, networks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public void Parse_WithoutANetwork_ReturnsNoNetwork(string? value)
    {
        Assert.Empty(TrustedProxyNetworks.Parse(value));
    }

    [Theory]
    [InlineData("100.100.0.0")]
    [InlineData("100.100.0.1/17")]
    [InlineData("10.0.0.5/8")]
    [InlineData("10/8")]
    [InlineData("2001:db8::1/32")]
    [InlineData("100.100.0.0/33")]
    [InlineData("proxy.internal/24")]
    [InlineData("10.0.0.0/8;100.100.0.0/17")]
    public void Parse_WithAnEntryThatIsNoNetwork_RefusesTheStartAndNamesTheEntry(string entry)
    {
        var refusal = Assert.Throws<OptionsValidationException>(() => TrustedProxyNetworks.Parse("10.0.0.0/8," + entry));

        Assert.Contains(TrustedProxyNetworks.ConfigurationKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"'{entry}'", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Parse_WithANetworkThatCoversEveryAddress_RefusesTheStart(string entry)
    {
        var refusal = Assert.Throws<OptionsValidationException>(() => TrustedProxyNetworks.Parse(entry));

        Assert.Contains("covers every address", refusal.Message, StringComparison.Ordinal);
    }
}
