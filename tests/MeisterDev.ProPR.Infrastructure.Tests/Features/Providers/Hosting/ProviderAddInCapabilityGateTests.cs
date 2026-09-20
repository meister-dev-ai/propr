// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     The licence check a provider family's credential goes through on the review path.
/// </summary>
public sealed class ProviderAddInCapabilityGateTests
{
    private const string CapabilityKey = "example-connections";

    // A family that declares no capability needs none, which is every family the product ships. Asking the
    // licence about it would be asking about nothing.
    [Fact]
    public async Task AFamilyThatDeclaresNoCapabilityIsNotCheckedAgainstTheLicence()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        var gate = GateOver(capabilities);

        Assert.True(await gate.IsAvailableAsync(null));
        Assert.True(await gate.IsAvailableAsync("   "));

        await capabilities.DidNotReceive().IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A credential is read once per outbound provider request and a review issues those in parallel, so an
    // answer resolved per request would put a database read on every model call.
    [Fact]
    public async Task ManyChecksInsideOneWindowResolveTheLicenceOnce()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(true);

        var gate = GateOver(capabilities, new FakeTimeProvider(DateTimeOffset.UtcNow));

        for (var call = 0; call < 50; call++)
        {
            Assert.True(await gate.IsAvailableAsync(CapabilityKey));
        }

        await capabilities.Received(1).IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>());
    }

    // The window bounds how long a replica keeps running on an entitlement that has gone away.
    [Fact]
    public async Task AnEntitlementThatLapsesStopsBeingAvailableOnceTheWindowHasPassed()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(true);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = GateOver(capabilities, clock);

        Assert.True(await gate.IsAvailableAsync(CapabilityKey));

        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(false);
        Assert.True(await gate.IsAvailableAsync(CapabilityKey));

        clock.Advance(ProviderAddInCapabilityGate.CacheDuration + TimeSpan.FromSeconds(1));

        Assert.False(await gate.IsAvailableAsync(CapabilityKey));
    }

    // A family declares its capability key itself and the host has nothing to check the claim against, so a key
    // this installation's catalogue does not carry is unavailable rather than an error. Handing over a
    // credential on an unverifiable claim is the alternative.
    [Fact]
    public async Task AKeyTheInstallationsCatalogueDoesNotCarryIsUnavailable()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync("invented-capability", Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool>>(_ => throw new KeyNotFoundException("Unknown premium capability."));

        var gate = GateOver(capabilities);

        Assert.False(await gate.IsAvailableAsync("invented-capability"));
    }

    // Two refreshes of one key overlap whenever a held answer expires while several callers are in flight. The
    // one that finishes last is not the one with the most recent view, so recording by completion lets a slow
    // read that began before a revocation stand for the rest of the window.
    [Fact]
    public async Task ASlowerReadThatBeganEarlierDoesNotReplaceTheAnswerOfOneThatBeganLater()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = GateOver(capabilities, clock);

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            slowStarted.TrySetResult();
            return new ValueTask<bool>(
                slowMayFinish.Task.ContinueWith(
                    _ => true,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default));
        });

        var slow = Task.Run(async () => await gate.IsAvailableAsync(CapabilityKey));
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The entitlement is withdrawn, and a read that begins now sees it.
        clock.Advance(TimeSpan.FromSeconds(1));
        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(false);
        Assert.False(await gate.IsAvailableNowAsync(CapabilityKey));

        // The earlier read now finishes with the answer it was given before the withdrawal.
        slowMayFinish.SetResult();
        Assert.True(await slow.WaitAsync(TimeSpan.FromSeconds(10)));

        // What stands is the answer of the read that began later.
        capabilities.ClearReceivedCalls();
        Assert.False(await gate.IsAvailableAsync(CapabilityKey));
        await capabilities.DidNotReceive().IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>());
    }

    // The write-time check cannot take a held answer: a credential an action produced is stored minutes after the
    // action began, and the answer the review path holds can be a minute old.
    [Fact]
    public async Task TheResolvedAnswerIsReadEvenWhileAHeldOneIsStillFresh()
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(true);

        var gate = GateOver(capabilities, new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(await gate.IsAvailableAsync(CapabilityKey));

        capabilities.IsEnabledAsync(CapabilityKey, Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await gate.IsAvailableNowAsync(CapabilityKey));
        Assert.False(await gate.IsAvailableAsync(CapabilityKey));
    }

    private static ProviderAddInCapabilityGate GateOver(
        ILicensingCapabilityService capabilities,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => capabilities);

        return new ProviderAddInCapabilityGate(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            timeProvider);
    }
}
