// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Infrastructure.Egress;

namespace MeisterDev.ProPR.Infrastructure.Tests.Egress;

/// <summary>
///     The posture applied to an address this host hands to something that opens its own sockets, where a
///     message handler cannot reach.
/// </summary>
public sealed class OutboundHostGuardTests
{
    [Theory]
    [InlineData("https://10.4.1.7/acme/propr.git")]
    [InlineData("https://169.254.169.254/acme/propr.git")]
    [InlineData("https://127.0.0.1/acme/propr.git")]
    [InlineData("https://[fd00::1]/acme/propr.git")]
    public async Task ALiteralPrivateAddressIsRefusedWithoutResolvingAnything(string remoteUrl)
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new ThrowingResolver());

        var refusal = await Refusal(guard, remoteUrl);

        Assert.NotNull(refusal);
        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostNameResolvingToAPrivateAddressIsRefused()
    {
        var resolver = new FixedResolver(IPAddress.Parse("192.168.10.4"));
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, resolver);

        var refusal = await Refusal(guard, "https://git.internal.example/acme/propr.git");

        Assert.NotNull(refusal);
        Assert.Contains("git.internal.example", refusal, StringComparison.Ordinal);

        // The address's own host is the one classified, so a guard resolving something else cannot pass here.
        Assert.Equal(["git.internal.example"], resolver.RequestedHosts);
    }

    // A name that answers with a public address and an internal one is being used to reach the internal one,
    // so the whole host is refused.
    [Fact]
    public async Task AHostNameResolvingToBothAPublicAndAPrivateAddressIsRefused()
    {
        var guard = new OutboundHostGuard(
            EgressUrlPolicy.Locked,
            new FixedResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.9")));

        Assert.NotNull(await Refusal(guard, "https://git.example.com/acme/propr.git"));
    }

    [Fact]
    public async Task AHostNameResolvingToAPublicAddressIsPermitted()
    {
        var resolver = new FixedResolver(IPAddress.Parse("93.184.216.34"));
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, resolver);

        Assert.Null(await Refusal(guard, "https://git.example.com/acme/propr.git"));
        Assert.Equal(["git.example.com"], resolver.RequestedHosts);
    }

    [Fact]
    public async Task AHostNameThatCannotBeResolvedIsRefused()
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new UnresolvableResolver());

        var refusal = await Refusal(guard, "https://git.example.invalid/acme/propr.git");

        Assert.NotNull(refusal);
        Assert.Contains("git.example.invalid", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/var/lib/propr/mirrors/acme")]
    [InlineData("file:///var/lib/propr/mirrors/acme")]
    [InlineData("")]
    public async Task AnAddressWithNoNetworkHostIsPermitted(string address)
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new ThrowingResolver());

        Assert.Null(await Refusal(guard, address));
    }

    [Fact]
    public async Task EveryAddressIsPermittedWhereTheInstallationAllowsPrivateEgress()
    {
        var guard = new OutboundHostGuard(
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false),
            new ThrowingResolver());

        Assert.Null(await Refusal(guard, "https://169.254.169.254/acme/propr.git"));
    }

    // Every remote ProPR hands to git is built from an http or https host address, so a remote on another
    // transport is refused for its scheme. Neither the address rule nor the pin the caller applies afterwards
    // reaches a connection on ssh or on a remote file share.
    [Theory]
    [InlineData("ssh://git@git.example.com/acme/propr.git")]
    [InlineData("git://git.example.com/acme/propr.git")]
    [InlineData("ftp://git.example.com/acme/propr.git")]
    [InlineData("file://server/share/acme.git")]
    [InlineData("file://10.4.1.7/share/acme.git")]
    public async Task ARemoteOnATransportProPRDoesNotUseIsRefusedWithoutResolvingAnything(string address)
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new ThrowingResolver());

        var refusal = await Refusal(guard, address);

        Assert.NotNull(refusal);
        Assert.Contains($"'{new Uri(address).Scheme}' scheme", refusal, StringComparison.Ordinal);
    }

    // The opt-in relaxes which addresses are reachable and not which transports are dialled.
    [Fact]
    public async Task ARemoteOnAnotherTransportIsRefusedWithTheOptInAsWell()
    {
        var guard = new OutboundHostGuard(
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false),
            new ThrowingResolver());

        var refusal = await Refusal(guard, "ssh://git@git.internal.example/acme/propr.git");

        Assert.NotNull(refusal);
        Assert.Contains("'ssh' scheme", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileUriOnThisMachineIsPermitted()
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new ThrowingResolver());

        Assert.Null(await Refusal(guard, "file://localhost/var/lib/propr/mirrors/acme"));
    }

    [Fact]
    public async Task AHostNameThatResolvesToNothingIsRefusedForThatReason()
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new FixedResolver());

        var refusal = await Refusal(guard, "https://git.example.com/acme/propr.git");

        Assert.NotNull(refusal);
        Assert.Contains("no address", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("private", refusal, StringComparison.Ordinal);
    }

    // What a caller pins the subprocess to, so it connects to the addresses this check classified.
    [Fact]
    public async Task APermittedHostNameReportsTheAddressesItWasPermittedAt()
    {
        var guard = new OutboundHostGuard(
            EgressUrlPolicy.Locked,
            new FixedResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35")));

        var decision = await guard.CheckAsync("https://git.example.com/acme/propr.git", CancellationToken.None);

        Assert.Null(decision.RefusalReason);
        Assert.Equal(
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35")],
            decision.ApprovedAddresses);
    }

    // The address git connects to is the one in the remote, so there is no name for a resolver to answer a
    // second time and no answer of its own that may decide the destination.
    [Fact]
    public async Task ALiteralPublicAddressIsPermittedWithoutResolvingAnythingAndReportsNothingToPin()
    {
        var guard = new OutboundHostGuard(EgressUrlPolicy.Locked, new ThrowingResolver());

        var decision = await guard.CheckAsync("https://93.184.216.34/acme/propr.git", CancellationToken.None);

        Assert.Null(decision.RefusalReason);
        Assert.Empty(decision.ApprovedAddresses);
    }

    // A git remote is a source-control address, so it is held to the rule this host's own clients follow: a
    // public host over plain http is refused whatever the installation permits privately, because the
    // credential git sends with every request would otherwise cross the internet in the clear.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APublicRemoteOverPlainHttpIsRefusedUnderEveryPosture(bool allowPrivateEgress)
    {
        var guard = new OutboundHostGuard(
            new EgressUrlPolicy(allowPrivateEgress, AllowInsecureScheme: false),
            new ThrowingResolver());

        var refusal = await Refusal(guard, "http://git.example.com/acme/propr.git");

        // The whole reason, so a refusal that mentions https for another cause cannot stand in for this one.
        Assert.Equal(
            "The repository's address must use https. Plain http reaches a private or loopback host only.",
            refusal);
    }

    [Fact]
    public async Task APrivateRemoteOverPlainHttpIsReachedWithTheOptIn()
    {
        var guard = new OutboundHostGuard(
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false),
            new ThrowingResolver());

        Assert.Null(await Refusal(guard, "http://10.4.1.7/acme/propr.git"));
    }

    [Fact]
    public async Task NothingIsReportedToPinWhereTheInstallationAllowsPrivateEgress()
    {
        var guard = new OutboundHostGuard(
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false),
            new ThrowingResolver());

        var decision = await guard.CheckAsync("https://git.example.com/acme/propr.git", CancellationToken.None);

        Assert.Null(decision.RefusalReason);
        Assert.Empty(decision.ApprovedAddresses);
    }

    /// <summary>The reason the guard refuses <paramref name="address" />, or null where it permits it.</summary>
    /// <param name="guard">The guard under test.</param>
    /// <param name="address">The address handed to it.</param>
    private static async Task<string?> Refusal(OutboundHostGuard guard, string address)
    {
        return (await guard.CheckAsync(address, CancellationToken.None)).RefusalReason;
    }

    /// <summary>A resolver that fails the test if it is asked to resolve anything.</summary>
    private sealed class ThrowingResolver : IOutboundHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            throw new InvalidOperationException($"'{host}' was resolved where the answer cannot change the outcome.");
        }
    }

    /// <summary>
    ///     A resolver answering with fixed addresses, standing in for a name server, and recording what it was
    ///     asked for so a test can tell the address's own host from any other.
    /// </summary>
    private sealed class FixedResolver(params IPAddress[] addresses) : IOutboundHostResolver
    {
        public List<string> RequestedHosts { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            this.RequestedHosts.Add(host);

            return Task.FromResult(addresses);
        }
    }

    /// <summary>A resolver answering the way one does for a name that does not exist.</summary>
    private sealed class UnresolvableResolver : IOutboundHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
    }
}
