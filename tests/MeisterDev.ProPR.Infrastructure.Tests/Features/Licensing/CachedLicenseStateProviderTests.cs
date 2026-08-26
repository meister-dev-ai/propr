// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     Covers what the provider reports for each thing the installation can have on file, and that a stored
///     document is verified again rather than reused indefinitely.
/// </summary>
public sealed class CachedLicenseStateProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoLicenseOnFile_ReadsAsNoneAndCommunity()
    {
        using var chain = LicenseTestChain.Create();
        using var harness = Harness.For(chain, new CountingLicenseStore(null));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.None, state.Kind);
        Assert.Equal(InstallationEdition.Community, state.Edition);
        Assert.Null(state.Claims);
        Assert.Null(state.ActivatedAt);
    }

    [Fact]
    public async Task AStoredLicenseInsideItsTerm_ReadsAsVerifiedAndCommercial()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1));
        var actor = Guid.NewGuid();
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(chain.Sign(claims), actor)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(InstallationEdition.Commercial, state.Edition);
        Assert.Equal(LicenseTermStatus.Active, state.TermStatus);
        Assert.NotNull(state.Claims);
        Assert.Equal(claims.LicenseId, state.Claims.LicenseId);
        Assert.Equal(claims.Licensee, state.Claims.Licensee);
        Assert.Equal(Now.AddHours(-4), state.ActivatedAt);
        Assert.Equal(actor, state.ActivatedByUserId);
    }

    // A license whose term has ended is still a license this build accepts. The claims stay available for
    // display, and the grace window that follows the term keeps the installation on the commercial edition so an
    // expiry does not interrupt work already under way.
    [Fact]
    public async Task AStoredLicenseInsideItsGraceWindow_StaysVerifiedAndCommercial()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-1));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(chain.Sign(claims), null)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(LicenseTermStatus.Expired, state.TermStatus);
        Assert.Equal(LicenseStage.Grace, state.Stage);
        Assert.Equal(InstallationEdition.Commercial, state.Edition);
        Assert.NotNull(state.Claims);
    }

    [Fact]
    public async Task AStoredLicensePastItsGraceWindow_StaysVerifiedButReadsAsCommunity()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-15));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(chain.Sign(claims), null)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(LicenseTermStatus.Expired, state.TermStatus);
        Assert.Equal(LicenseStage.Reverted, state.Stage);
        Assert.Equal(InstallationEdition.Community, state.Edition);
        Assert.NotNull(state.Claims);
    }

    // The stage rides the state the provider hands out, so it is derived against the licensing clock rather than
    // recomputed by each caller. Crossing a boundary is observed at the next load, which is what the cache
    // duration bounds.
    [Fact]
    public async Task ALicenseThatCrossesTheGraceBoundary_IsObservedOnTheNextLoad()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-13));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(chain.Sign(claims), null)));

        var inGrace = await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromDays(2));
        var afterGrace = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStage.Grace, inGrace.Stage);
        Assert.Equal(InstallationEdition.Commercial, inGrace.Edition);
        Assert.Equal(LicenseStage.Reverted, afterGrace.Stage);
        Assert.Equal(InstallationEdition.Community, afterGrace.Edition);
    }

    // The term is judged against the licensing clock rather than the host clock. Here the host clock reads inside
    // the term, which on its own reports the license as active, while the installation has already recorded an
    // instant past it. That is the state a host clock set back leaves behind, and the license stays expired.
    [Fact]
    public async Task AHostClockBehindTheRecordedInstant_LeavesAnExpiredLicenseExpired()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(30));
        using var harness = Harness.For(
            chain,
            new CountingLicenseStore(Readable(chain.Sign(claims), null)),
            Now.AddDays(60));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(LicenseTermStatus.Expired, state.TermStatus);
        Assert.Equal(InstallationEdition.Community, state.Edition);
    }

    [Fact]
    public async Task AStoredLicenseBeforeItsTerm_StaysVerifiedButReadsAsCommunity()
    {
        using var chain = LicenseTestChain.Create();
        var claims = LicenseTestChain.ClaimsFor(Now.AddDays(30), Now.AddYears(1));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(chain.Sign(claims), null)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(LicenseTermStatus.NotYetValid, state.TermStatus);
        Assert.Equal(InstallationEdition.Community, state.Edition);
    }

    // A row that cannot be opened carries no claims, so it cannot be reported as a license that failed to
    // verify: the recovery differs, and there is nothing to say about a document nobody has read.
    [Fact]
    public async Task AnUnreadableRow_ReadsAsUnreadableAndCommunity()
    {
        using var chain = LicenseTestChain.Create();
        using var harness = Harness.For(chain, new CountingLicenseStore(Unreadable()));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Unreadable, state.Kind);
        Assert.Equal(InstallationEdition.Community, state.Edition);
        Assert.Null(state.Claims);
        Assert.Null(state.FailureReason);
        Assert.Equal(Now.AddHours(-4), state.ActivatedAt);
    }

    // A document altered after signing no longer matches what its signer produced, which is reported as an
    // untrusted signer rather than as a malformed file.
    [Fact]
    public async Task AProtectedButTamperedDocument_ReadsAsInvalidWithAnUntrustedSigner()
    {
        using var chain = LicenseTestChain.Create();
        var tampered = LicenseTestChain.TamperPayload(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(tampered, null)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Invalid, state.Kind);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, state.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(state.FailureDetail));
        Assert.Equal(InstallationEdition.Community, state.Edition);
        Assert.Null(state.Claims);
    }

    // A document from a chain this build does not lead back to verifies as a signature and still fails, because
    // a signature only says which key wrote the document.
    [Fact]
    public async Task ADocumentFromAnotherChain_ReadsAsInvalidWithAnUntrustedSigner()
    {
        using var trustedChain = LicenseTestChain.Create();
        using var otherChain = LicenseTestChain.Create();
        var foreignLicense = otherChain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        using var harness = Harness.For(trustedChain, new CountingLicenseStore(Readable(foreignLicense, null)));

        var state = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.Invalid, state.Kind);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, state.FailureReason);
        Assert.Equal(InstallationEdition.Community, state.Edition);
    }

    // Capability checks run on every review, so a second read inside the cache window must not reach the
    // database or run verification again.
    [Fact]
    public async Task ASecondReadInsideTheCacheWindow_ReusesTheFirstResult()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));
        using var harness = Harness.For(chain, store);

        var first = await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = await harness.Provider.GetStateAsync();

        Assert.Equal(1, store.LoadCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task ACacheEntryExpiresWhenTheUtcClockMovesBackwards()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));
        using var harness = Harness.For(chain, store);

        await harness.Provider.GetStateAsync();
        harness.TimeProvider.MoveUtcTo(Now.AddHours(-1));
        harness.TimeProvider.AdvanceTimestamp(TimeSpan.FromSeconds(61));
        await harness.Provider.GetStateAsync();

        Assert.Equal(2, store.LoadCount);
    }

    [Fact]
    public async Task AReadAfterTheCacheWindowHasPassed_LoadsAndVerifiesAgain()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));
        using var harness = Harness.For(chain, store);

        await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(61));
        await harness.Provider.GetStateAsync();

        Assert.Equal(2, store.LoadCount);
    }

    // Reading the licensing clock is what records the observed instant, and the clock is read on the load rather
    // than on the cache check. Anything that wants the ratchet kept moving therefore only has to read the state
    // at a cadence longer than the cache window; a read inside the window costs nothing and records nothing.
    [Fact]
    public async Task ACacheMiss_AdvancesTheObservedInstantAndACacheHitDoesNot()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));
        using var harness = Harness.For(chain, store);

        await harness.Provider.GetStateAsync();
        var afterFirstLoad = harness.ObservedTime.AdvanceCount;

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await harness.Provider.GetStateAsync();
        var afterCacheHit = harness.ObservedTime.AdvanceCount;

        harness.TimeProvider.Advance(TimeSpan.FromHours(2));
        await harness.Provider.GetStateAsync();

        Assert.Equal(1, afterFirstLoad);
        Assert.Equal(1, afterCacheHit);
        Assert.Equal(2, harness.ObservedTime.AdvanceCount);
        Assert.Equal(Now.AddHours(2).AddSeconds(30), harness.ObservedTime.Recorded);
    }

    // Renewal has to take effect at once rather than at the end of the term the installation is running out. The
    // activation discards the cached state, so the next resolution reads the renewed license without the clock
    // having to move.
    [Fact]
    public async Task ARenewedLicenseActivatedDuringGrace_IsInForceOnTheNextResolution()
    {
        using var chain = LicenseTestChain.Create();
        var expiredLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-1)));
        var store = new CountingLicenseStore(Readable(expiredLicense, null));
        using var harness = Harness.For(chain, store);
        using var anchor = chain.CreateAnchor();

        var duringGrace = await harness.Provider.GetStateAsync();
        var activation = CreateActivation(harness, store, anchor);

        var result = await activation.HandleAsync(new ActivateLicenseCommand(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));

        var afterRenewal = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStage.Grace, duringGrace.Stage);
        Assert.True(result.IsActivated);
        Assert.Equal(LicenseStage.Active, afterRenewal.Stage);
        Assert.Equal(InstallationEdition.Commercial, afterRenewal.Edition);
    }

    // An installation rebuilt from a backup activates the license file it already has. That file is inside its
    // grace window, so activation accepts it and the installation comes back up entitled rather than on Community
    // with no way to restore itself until a renewal arrives.
    [Fact]
    public async Task TheSameInGraceLicenseActivatedAgain_LeavesTheInstallationInGraceAndCommercial()
    {
        using var chain = LicenseTestChain.Create();
        var inGraceLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-1)));
        var store = new CountingLicenseStore(Readable(inGraceLicense, null));
        using var harness = Harness.For(chain, store);
        using var anchor = chain.CreateAnchor();

        var result = await CreateActivation(harness, store, anchor)
            .HandleAsync(new ActivateLicenseCommand(inGraceLicense, null));

        var afterActivation = await harness.Provider.GetStateAsync();

        Assert.True(result.IsActivated);
        Assert.Equal(inGraceLicense, store.Stored?.CompactLicense);
        Assert.Equal(LicenseStage.Grace, afterActivation.Stage);
        Assert.Equal(InstallationEdition.Commercial, afterActivation.Edition);
    }

    // A document past its grace window grants nothing once stored, so activation refuses it and the license the
    // installation already had stays in place.
    [Fact]
    public async Task ALicensePastItsGraceWindowActivated_IsRefusedAndLeavesTheStoredLicenseAlone()
    {
        using var chain = LicenseTestChain.Create();
        var inGraceLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-1)));
        var store = new CountingLicenseStore(Readable(inGraceLicense, null));
        using var harness = Harness.For(chain, store);
        using var anchor = chain.CreateAnchor();

        var result = await CreateActivation(harness, store, anchor).HandleAsync(
            new ActivateLicenseCommand(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-15))), null));

        Assert.False(result.IsActivated);
        Assert.Equal(LicenseFailureReason.Expired, result.RefusalReason);
        Assert.Equal(inGraceLicense, store.Stored?.CompactLicense);
    }

    // The process that activated a license must not keep serving the previous answer for a minute.
    [Fact]
    public async Task InvalidatingTheCache_ForcesTheNextReadToLoadAgain()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(null);
        using var harness = Harness.For(chain, store);

        var beforeActivation = await harness.Provider.GetStateAsync();
        store.Stored = Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null);
        harness.Provider.Invalidate();
        var afterActivation = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.None, beforeActivation.Kind);
        Assert.Equal(LicenseStateKind.Verified, afterActivation.Kind);
        Assert.Equal(2, store.LoadCount);
    }

    // An activation that lands while a load is already in flight must not be masked by that load's result. The
    // caller still receives what its own load read, and the cache is left empty so the next read goes back to
    // the store.
    [Fact]
    public async Task InvalidatingWhileALoadIsInFlight_KeepsThatLoadsResultOutOfTheCache()
    {
        using var chain = LicenseTestChain.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CountingLicenseStore(null) { LoadGate = release.Task };
        using var harness = Harness.For(chain, store);

        var inFlight = harness.Provider.GetStateAsync();
        await store.LoadStarted;

        harness.Provider.Invalidate();
        store.Stored = Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null);
        release.SetResult();

        var loadedBeforeActivation = await inFlight;
        var afterActivation = await harness.Provider.GetStateAsync();

        Assert.Equal(LicenseStateKind.None, loadedBeforeActivation.Kind);
        Assert.Equal(LicenseStateKind.Verified, afterActivation.Kind);
        Assert.Equal(2, store.LoadCount);
    }

    // The condition persists for as long as the installation runs and is re-checked every cache window, so
    // logging each load would fill the log with one repeated line.
    [Fact]
    public async Task APersistingUnreadableRow_IsReportedOnceAcrossRepeatedReads()
    {
        using var chain = LicenseTestChain.Create();
        using var harness = Harness.For(chain, new CountingLicenseStore(Unreadable()));

        await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(61));
        await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(61));
        await harness.Provider.GetStateAsync();

        Assert.Single(harness.Warnings);
        Assert.Contains("could not be read back", harness.Warnings[0], StringComparison.Ordinal);
    }

    // A condition that clears and comes back is a new event for the operator, so the report is not suppressed
    // for the rest of the process's life.
    [Fact]
    public async Task AnUnreadableRowThatClearsAndReturns_IsReportedAgain()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Unreadable());
        using var harness = Harness.For(chain, store);

        await harness.Provider.GetStateAsync();

        store.Stored = Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null);
        harness.Provider.Invalidate();
        await harness.Provider.GetStateAsync();

        store.Stored = Unreadable();
        harness.Provider.Invalidate();
        await harness.Provider.GetStateAsync();

        Assert.Equal(2, harness.Warnings.Count);
    }

    [Fact]
    public async Task APersistingUnverifiableDocument_IsReportedOnceWithItsReason()
    {
        using var chain = LicenseTestChain.Create();
        var tampered = LicenseTestChain.TamperPayload(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))));
        using var harness = Harness.For(chain, new CountingLicenseStore(Readable(tampered, null)));

        await harness.Provider.GetStateAsync();
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(61));
        await harness.Provider.GetStateAsync();

        Assert.Single(harness.Warnings);
        Assert.Contains(nameof(LicenseFailureReason.UntrustedSigner), harness.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerifiedLicense_IsNotReported()
    {
        using var chain = LicenseTestChain.Create();
        using var harness = Harness.For(
            chain,
            new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null)));

        await harness.Provider.GetStateAsync();

        Assert.Empty(harness.Warnings);
    }

    // Callers that arrive together while nothing is cached must share one load rather than each running their
    // own, which is what the gate around the refresh is for.
    [Fact]
    public async Task ConcurrentFirstReads_LoadOnce()
    {
        using var chain = LicenseTestChain.Create();
        var store = new CountingLicenseStore(Readable(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null));
        using var harness = Harness.For(chain, store);

        // The gate holds the load open until the test releases it, so the eight reads are guaranteed to
        // overlap rather than relying on the scheduler to interleave them.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.LoadGate = release.Task;

        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(() => harness.Provider.GetStateAsync())).ToArray();
        await store.LoadStarted;

        release.SetResult();
        var states = await Task.WhenAll(reads);

        Assert.Equal(1, store.LoadCount);
        Assert.All(states, state => Assert.Equal(LicenseStateKind.Verified, state.Kind));
    }

    // The store is scoped because it holds a database context, and the provider is a singleton, so the load
    // has to open a scope of its own rather than capture one.
    [Fact]
    public async Task TheProvider_ResolvesTheStoreFromItsOwnScope()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        var services = new ServiceCollection();
        services.AddSingleton(codec);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddDbContext<MeisterProPRDbContext>(options => options
            .UseInMemoryDatabase("TestDb_LicenseStateProviderScope")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<IActivatedLicenseStore, InstallationLicenseRepository>();
        services.AddScoped<IHighestObservedTimeStore, InstallationObservedTimeRepository>();

        var serviceProvider = services.BuildServiceProvider();

        using (var seedingScope = serviceProvider.CreateScope())
        {
            await seedingScope.ServiceProvider.GetRequiredService<IActivatedLicenseStore>().SetAsync(compactLicense, null);
        }

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        using var anchor = chain.CreateAnchor();
        using var sut = new CachedLicenseStateProvider(
            scopeFactory,
            new LicenseVerifier(anchor),
            new RatchetedLicensingClock(
                scopeFactory,
                new FakeTimeProvider(Now),
                new RatchetedLicensingClockTests.CapturingLogger<RatchetedLicensingClock>([])),
            new FakeTimeProvider(Now),
            new CapturingLogger([]));

        var state = await sut.GetStateAsync();

        Assert.Equal(LicenseStateKind.Verified, state.Kind);
        Assert.Equal(InstallationEdition.Commercial, state.Edition);
    }

    /// <summary>
    ///     The real activation handler over the harness's store, provider and licensing clock, so an activation
    ///     and the resolution that follows it are judged against one instant. The summary it returns is not what
    ///     these tests assert on; the state the provider reports afterwards is.
    /// </summary>
    private static ActivateLicenseHandler CreateActivation(
        Harness harness,
        IActivatedLicenseStore store,
        LicenseTrustAnchor anchor)
    {
        var capabilityService = Substitute.For<ILicensingCapabilityService>();
        capabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new LicensingSummaryDto(InstallationEdition.Commercial, Now, []));

        return new ActivateLicenseHandler(
            new LicenseVerifier(anchor),
            store,
            Substitute.For<ILicenseActivationEventStore>(),
            harness.Provider,
            capabilityService,
            harness.LicensingClock,
            NullLogger<ActivateLicenseHandler>.Instance);
    }

    private static StoredLicense Readable(string compactLicense, Guid? activatedByUserId) => new()
    {
        CompactLicense = compactLicense,
        ActivatedAt = Now.AddHours(-4),
        ActivatedByUserId = activatedByUserId,
    };

    private static StoredLicense Unreadable() => new() { CompactLicense = null, ActivatedAt = Now.AddHours(-4) };

    /// <summary>
    ///     The provider under test, over a store that counts loads and a logger that keeps its warnings. The
    ///     licensing clock is the production one over an in-memory row, so a test that moves the fake host clock
    ///     sees the same term evaluation an installation would.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly LicenseTrustAnchor _anchor;

        private Harness(LicenseTrustAnchor anchor, CachedLicenseStateProvider provider, AdjustableTimeProvider timeProvider, List<string> warnings)
        {
            this._anchor = anchor;
            this.Provider = provider;
            this.TimeProvider = timeProvider;
            this.Warnings = warnings;
        }

        public CachedLicenseStateProvider Provider { get; }

        public AdjustableTimeProvider TimeProvider { get; }

        /// <summary>The single row the licensing clock records into, so a test can see when the ratchet advanced.</summary>
        public RatchetedLicensingClockTests.InMemoryObservedTimeStore ObservedTime { get; private set; } = new();

        /// <summary>
        ///     The production clock the provider judges terms by. Activation reads the same one, so a test that
        ///     spans both sees one instant rather than two.
        /// </summary>
        public ILicensingClock LicensingClock { get; private set; } = null!;

        public List<string> Warnings { get; }

        /// <summary>Builds the harness.</summary>
        /// <param name="chain">The signing chain the trust anchor accepts.</param>
        /// <param name="store">The license the installation has on file.</param>
        /// <param name="recordedInstant">
        ///     The highest instant the installation has already recorded, for a test about a host clock that reads
        ///     earlier than it. Left unset, the clock records the fake host clock on its first reading and reports
        ///     it unchanged.
        /// </param>
        /// <returns>The harness. The caller disposes it.</returns>
        public static Harness For(
            LicenseTestChain chain,
            CountingLicenseStore store,
            DateTimeOffset? recordedInstant = null)
        {
            // One instance across the scopes the clock opens, standing in for the single row an installation has.
            var observedTime = new RatchetedLicensingClockTests.InMemoryObservedTimeStore(recordedInstant);

            var services = new ServiceCollection();
            services.AddScoped<IActivatedLicenseStore>(_ => store);
            services.AddScoped<IHighestObservedTimeStore>(_ => observedTime);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var anchor = chain.CreateAnchor();
            var timeProvider = new AdjustableTimeProvider(Now);
            var warnings = new List<string>();

            // The clock keeps its own log, so a report about the host clock cannot be mistaken for one about the
            // stored license.
            var clock = new RatchetedLicensingClock(
                scopeFactory,
                timeProvider,
                new RatchetedLicensingClockTests.CapturingLogger<RatchetedLicensingClock>([]));

            return new Harness(
                anchor,
                new CachedLicenseStateProvider(scopeFactory, new LicenseVerifier(anchor), clock, timeProvider, new CapturingLogger(warnings)),
                timeProvider,
                warnings)
            {
                ObservedTime = observedTime,
                LicensingClock = clock,
            };
        }

        public void Dispose()
        {
            this.Provider.Dispose();
            this._anchor.Dispose();
        }
    }

    /// <summary>
    ///     A clock whose calendar reading and elapsed-time reading move independently. The provider cache uses
    ///     the latter, while license term evaluation sees the former.
    /// </summary>
    private sealed class AdjustableTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        private DateTimeOffset _instant = instant;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => this._instant;

        public override long GetTimestamp() => Interlocked.Read(ref this._timestamp);

        public void Advance(TimeSpan duration)
        {
            this._instant = this._instant.Add(duration);
            Interlocked.Add(ref this._timestamp, duration.Ticks);
        }

        public void AdvanceTimestamp(TimeSpan duration) => Interlocked.Add(ref this._timestamp, duration.Ticks);

        public void MoveUtcTo(DateTimeOffset instant) => this._instant = instant;
    }

    /// <summary>
    ///     A store that hands back one stored license and counts how often it was asked, which is how a cache
    ///     hit is told apart from a reload.
    /// </summary>
    private sealed class CountingLicenseStore(StoredLicense? stored) : IActivatedLicenseStore
    {
        private readonly TaskCompletionSource _loadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _loadCount;

        public StoredLicense? Stored { get; set; } = stored;

        /// <summary>Set to hold a load open until the test completes it, which is how a load is made to overlap.</summary>
        public Task? LoadGate { get; set; }

        /// <summary>Completes once a load has reached this store.</summary>
        public Task LoadStarted => this._loadStarted.Task;

        public int LoadCount => Volatile.Read(ref this._loadCount);

        public async Task<StoredLicense?> GetAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this._loadCount);
            this._loadStarted.TrySetResult();

            // What this load read, captured before the gate. A value set while the load is held open therefore
            // belongs to the next load, which is how a store read that predates an activation is modelled.
            var read = this.Stored;

            if (this.LoadGate is { } gate)
            {
                await gate;
            }

            return read;
        }

        public Task SetAsync(string compactLicense, Guid? activatedByUserId, CancellationToken cancellationToken = default)
        {
            this.Stored = new StoredLicense { CompactLicense = compactLicense, ActivatedAt = Now, ActivatedByUserId = activatedByUserId };

            return Task.CompletedTask;
        }

        public async Task<LicenseMutation> ReplaceAsync(
            string compactLicense,
            Guid? activatedByUserId,
            CancellationToken cancellationToken = default)
        {
            var previous = this.Stored;
            await this.SetAsync(compactLicense, activatedByUserId, cancellationToken);
            return new LicenseMutation(previous, Now);
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            this.Stored = null;

            return Task.CompletedTask;
        }

        public Task<LicenseMutation> RemoveAndGetAsync(CancellationToken cancellationToken = default)
        {
            var removed = this.Stored;
            this.Stored = null;
            return Task.FromResult(new LicenseMutation(removed, Now));
        }
    }

    /// <summary>Keeps the rendered warnings, which is what the once-per-transition rule is asserted against.</summary>
    private sealed class CapturingLogger(List<string> warnings) : ILogger<CachedLicenseStateProvider>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
