// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights;

namespace MeisterDev.ProPR.CodeInsights.Tests;

public sealed class CodeInsightsCollectionGateTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task BothGatesOpen_CollectionRuns()
    {
        var gate = CreateGate(licensed: true, optedIn: true);

        Assert.True(await gate.IsCollectionEnabledAsync(ClientId));
    }

    [Fact]
    public async Task WithoutTheCommercialCapability_TheClientToggleIsIrrelevant()
    {
        // Community edition: nothing is collected and no model token is spent, whatever the client asked for.
        var gate = CreateGate(licensed: false, optedIn: true);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
    }

    [Fact]
    public async Task WithTheCapabilityButNoOptIn_NothingIsCollected()
    {
        var gate = CreateGate(licensed: true, optedIn: false);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
    }

    [Fact]
    public async Task AnUnlicensedInstallation_IsNotEvenAskedAboutTheClient()
    {
        // Order matters for cost: the cheap installation-wide check short-circuits the per-client lookup.
        var clientRegistry = Substitute.For<IClientRegistry>();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));

        var gate = new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensing);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
        await clientRegistry.DidNotReceive()
            .GetCodeInsightsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoLicensingServiceRegistered_TheGateIsClosed()
    {
        // This is the opposite polarity to LicensingCapabilityGuard, deliberately. That helper decides
        // whether to block a user's own action, so an absent service means "allow". This gate decides
        // whether to start spending the customer's model budget and read their discussion, so an
        // unestablished edition must mean "no".
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCodeInsightsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(true);

        var gate = new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensingCapabilityService: null);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
        await clientRegistry.DidNotReceive()
            .GetCodeInsightsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingLicenceLookup_ClosesTheGateRatherThanThrowing()
    {
        var clientRegistry = Substitute.For<IClientRegistry>();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("licensing is unavailable"));

        var gate = new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensing);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
    }

    [Fact]
    public async Task AFailingClientLookup_ClosesTheGateRatherThanThrowing()
    {
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCodeInsightsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database is unreachable"));
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var gate = new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensing);

        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
    }

    [Fact]
    public async Task TheGateIsReResolvedEveryCall()
    {
        // The edition and the opt-in can both change at runtime. A cached "yes" would keep collecting
        // after someone turned it off.
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCodeInsightsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(true, false);
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var gate = new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensing);

        Assert.True(await gate.IsCollectionEnabledAsync(ClientId));
        Assert.False(await gate.IsCollectionEnabledAsync(ClientId));
    }

    private static CodeInsightsCollectionGate CreateGate(bool licensed, bool optedIn)
    {
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCodeInsightsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(optedIn);

        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(licensed));

        return new CodeInsightsCollectionGate(
            clientRegistry,
            NullLogger<CodeInsightsCollectionGate>.Instance,
            licensing);
    }
}
