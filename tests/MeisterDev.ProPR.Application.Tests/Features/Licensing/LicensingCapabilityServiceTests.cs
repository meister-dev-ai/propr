// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     Capability resolution over the real capability service, the real policy store and the real catalog, with
///     license states built by signing documents with a chain generated for the test. What the summary reports is
///     therefore what a request would get rather than what a substitute was told to return.
/// </summary>
public sealed class LicensingCapabilityServiceTests : IDisposable
{
    private const int SingletonPolicyId = 1;
    private const string LicensedKey = PremiumCapabilityKey.MentionAnswering;
    private const string UnlicensedKey = PremiumCapabilityKey.Budgeting;

    /// <summary>The state an earlier build stored to switch a capability on. Nothing defines it any more.</summary>
    private const PremiumCapabilityOverrideState RemovedEnableState = (PremiumCapabilityOverrideState)1;

    /// <summary>
    ///     A state number no build of this product has defined, standing in for one a later build could add. An
    ///     override can only take a capability away, so a row an older build cannot name still has to withhold.
    /// </summary>
    private const PremiumCapabilityOverrideState UnknownState = (PremiumCapabilityOverrideState)3;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly LicenseVerifier _verifier;

    public LicensingCapabilityServiceTests()
    {
        this._chain = LicenseTestChain.Create();
        this._anchor = this._chain.CreateAnchor();
        this._verifier = new LicenseVerifier(this._anchor);
    }

    public void Dispose()
    {
        this._anchor.Dispose();
        this._chain.Dispose();
    }

    // The three license states, each against both override states. A capability the license does not name is
    // unavailable however the override is set.
    [Theory]
    [InlineData(LicenseFixture.None, PremiumCapabilityOverrideState.Default, false, PremiumCapabilityUnavailableReason.NoLicense)]
    [InlineData(LicenseFixture.None, PremiumCapabilityOverrideState.Disabled, false, PremiumCapabilityUnavailableReason.NoLicense)]
    [InlineData(LicenseFixture.WithoutTheKey, PremiumCapabilityOverrideState.Default, false, PremiumCapabilityUnavailableReason.NotInLicense)]
    [InlineData(LicenseFixture.WithoutTheKey, PremiumCapabilityOverrideState.Disabled, false, PremiumCapabilityUnavailableReason.NotInLicense)]
    [InlineData(LicenseFixture.WithTheKey, PremiumCapabilityOverrideState.Default, true, null)]
    [InlineData(LicenseFixture.WithTheKey, PremiumCapabilityOverrideState.Disabled, false, PremiumCapabilityUnavailableReason.DisabledByOverride)]
    public async Task GetCapabilityAsync_ResolvesFromTheLicenseAndTheOverride(
        LicenseFixture fixture,
        PremiumCapabilityOverrideState overrideState,
        bool expectedAvailability,
        PremiumCapabilityUnavailableReason? expectedReason)
    {
        await using var db = CreateContext();
        await SeedOverrideAsync(db, LicensedKey, overrideState);
        var sut = this.CreateService(db, this.StateFor(fixture));

        var capability = await sut.GetCapabilityAsync(LicensedKey);

        Assert.Equal(expectedAvailability, capability.IsAvailable);
        Assert.Equal(expectedReason, capability.Reason);
        Assert.Equal(overrideState, capability.OverrideState);
    }

    // A row carrying the removed enable state, written directly because no write path can produce one. The
    // migration deletes such rows, and resolution has to ignore one that outlives it rather than treat an
    // unknown number as a grant.
    [Fact]
    public async Task GetCapabilityAsync_StoredEnableStateWithoutALicense_DoesNotGrantTheCapability()
    {
        await using var db = CreateContext();
        await SeedOverrideAsync(db, LicensedKey, RemovedEnableState);
        var sut = this.CreateService(db, LicenseState.None());

        var capability = await sut.GetCapabilityAsync(LicensedKey);

        Assert.False(capability.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.NoLicense, capability.Reason);
        Assert.Equal(PremiumCapabilityOverrideState.Default, capability.OverrideState);
    }

    [Fact]
    public async Task GetCapabilityAsync_StoredEnableStateForAKeyTheLicenseDoesNotName_DoesNotGrantTheCapability()
    {
        await using var db = CreateContext();
        await SeedOverrideAsync(db, UnlicensedKey, RemovedEnableState);
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var capability = await sut.GetCapabilityAsync(UnlicensedKey);

        Assert.False(capability.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.NotInLicense, capability.Reason);
    }

    // A row carrying a number this build does not define, written by a later build than the one reading it. Every
    // state an override can carry withholds a capability, so an unknown one withholds as well. Leaving it to the
    // license would make a capability available again that an administrator had turned off.
    [Fact]
    public async Task GetCapabilityAsync_StoredStateThisBuildDoesNotDefine_WithholdsTheCapability()
    {
        await using var db = CreateContext();
        await SeedOverrideAsync(db, LicensedKey, UnknownState);
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var capability = await sut.GetCapabilityAsync(LicensedKey);

        Assert.False(capability.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.DisabledByOverride, capability.Reason);
        Assert.Equal(PremiumCapabilityOverrideState.Disabled, capability.OverrideState);
    }

    // A disable-override on a capability the license does not name changes nothing: the reason stays the one that
    // tells the operator to obtain a license covering it.
    [Fact]
    public async Task GetCapabilityAsync_DisableOverrideForAKeyTheLicenseDoesNotName_StillReportsItAsNotInTheLicense()
    {
        var definition = new StaticPremiumCapabilityCatalog().Get(UnlicensedKey);
        Assert.NotNull(definition);

        await using var db = CreateContext();
        await SeedOverrideAsync(db, UnlicensedKey, PremiumCapabilityOverrideState.Disabled);
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var capability = await sut.GetCapabilityAsync(UnlicensedKey);

        Assert.False(capability.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.NotInLicense, capability.Reason);
        Assert.Equal(definition.NotInLicenseMessage, capability.Message);
    }

    // Each reason carries its own message, so the three cases are told apart without parsing text.
    [Fact]
    public async Task GetCapabilityAsync_EachReason_CarriesTheMessageWrittenForIt()
    {
        var definition = new StaticPremiumCapabilityCatalog().Get(LicensedKey);
        Assert.NotNull(definition);

        await using var noLicenseDb = CreateContext();
        var noLicense = await this.CreateService(noLicenseDb, LicenseState.None()).GetCapabilityAsync(LicensedKey);

        await using var notInLicenseDb = CreateContext();
        var notInLicense = await this
            .CreateService(notInLicenseDb, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), UnlicensedKey))
            .GetCapabilityAsync(LicensedKey);

        await using var disabledDb = CreateContext();
        await SeedOverrideAsync(disabledDb, LicensedKey, PremiumCapabilityOverrideState.Disabled);
        var disabled = await this
            .CreateService(disabledDb, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey))
            .GetCapabilityAsync(LicensedKey);

        Assert.Equal(definition.CommercialRequiredMessage, noLicense.Message);
        Assert.Equal(definition.NotInLicenseMessage, notInLicense.Message);
        Assert.Equal(definition.CommercialDisabledMessage, disabled.Message);
    }

    // Every entry has to distinguish the three cases, not just the one the resolution test happens to use.
    [Fact]
    public void TheCapabilityCatalog_GivesEveryEntryThreeDistinctMessages()
    {
        var catalog = new StaticPremiumCapabilityCatalog();

        Assert.All(
            catalog.GetAll(),
            definition =>
            {
                string[] messages =
                [
                    definition.CommercialRequiredMessage,
                    definition.NotInLicenseMessage,
                    definition.CommercialDisabledMessage,
                ];

                Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
                Assert.Equal(3, messages.Distinct(StringComparer.Ordinal).Count());
            });
    }

    [Fact]
    public async Task GetCapabilityAsync_AnAvailableCapability_CarriesNoReasonAndNoMessage()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var capability = await sut.GetCapabilityAsync(LicensedKey);

        Assert.True(capability.IsAvailable);
        Assert.Null(capability.Reason);
        Assert.Null(capability.Message);
    }

    [Fact]
    public async Task GetSummaryAsync_NoLicense_ReportsCommunityWithNoCapabilityAvailable()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, LicenseState.None());

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(InstallationEdition.Community, summary.Edition);
        Assert.Null(summary.ActivatedAt);
        Assert.NotEmpty(summary.Capabilities);
        Assert.All(
            summary.Capabilities,
            capability =>
            {
                Assert.False(capability.IsAvailable);
                Assert.Equal(PremiumCapabilityUnavailableReason.NoLicense, capability.Reason);
            });
    }

    // The stored edition column says commercial and no license is on file. The derived state is what answers, so
    // the installation reports community.
    [Fact]
    public async Task GetSummaryAsync_StoredEditionSaysCommercialWithNoLicense_ReportsCommunity()
    {
        await using var db = CreateContext();
        await SeedStoredEditionAsync(db, InstallationEdition.Commercial);
        var sut = this.CreateService(db, LicenseState.None());

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(InstallationEdition.Community, summary.Edition);

        // The reason has to be no-license rather than not-in-license: the latter would mean the stored column
        // opened the edition gate and only the capability list refused.
        Assert.All(
            summary.Capabilities,
            capability =>
            {
                Assert.False(capability.IsAvailable);
                Assert.Equal(PremiumCapabilityUnavailableReason.NoLicense, capability.Reason);
            });
    }

    [Fact]
    public async Task GetSummaryAsync_ActiveLicense_ReportsCommercialAndWhenItWasActivated()
    {
        await using var db = CreateContext();
        var activatedAt = Now.AddDays(-3);
        var sut = this.CreateService(
            db,
            this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), [LicensedKey], activatedAt));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(InstallationEdition.Commercial, summary.Edition);
        Assert.Equal(activatedAt, summary.ActivatedAt);

        var licensed = Assert.Single(summary.Capabilities, capability => capability.Key == LicensedKey);
        Assert.True(licensed.IsAvailable);
        Assert.All(
            summary.Capabilities.Where(capability => capability.Key != LicensedKey),
            capability => Assert.Equal(PremiumCapabilityUnavailableReason.NotInLicense, capability.Reason));
    }

    // A term that has ended keeps granting what the license names until the grace window closes, so an expiry
    // does not take a capability away from a review that is already running.
    [Fact]
    public async Task GetSummaryAsync_LicenseInsideItsGraceWindow_KeepsReportingCommercial()
    {
        await using var db = CreateContext();
        var activatedAt = Now.AddYears(-2);
        var sut = this.CreateService(
            db,
            this.VerifiedState(Now.AddYears(-2), Now.AddDays(-1), [LicensedKey], activatedAt));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.Grace, summary.Stage);
        Assert.Equal(InstallationEdition.Commercial, summary.Edition);
        Assert.Equal(activatedAt, summary.ActivatedAt);
        Assert.True(Assert.Single(summary.Capabilities, capability => capability.Key == LicensedKey).IsAvailable);
    }

    // Only a term still inside its grace window counts as licensed. The edition mapping the license state owns
    // decides that, so a license past both reaches capability resolution as an installation that has reverted.
    [Fact]
    public async Task GetSummaryAsync_LicensePastItsGraceWindow_ReportsCommunityAndNoActivationInstant()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(
            db,
            this.VerifiedState(Now.AddYears(-2), Now.AddDays(-15), [LicensedKey], Now.AddYears(-2)));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.Reverted, summary.Stage);
        Assert.Equal(InstallationEdition.Community, summary.Edition);
        Assert.Null(summary.ActivatedAt);
        Assert.All(
            summary.Capabilities,
            capability => Assert.Equal(PremiumCapabilityUnavailableReason.Reverted, capability.Reason));
    }

    [Fact]
    public async Task GetSummaryAsync_LicenseWhoseTermHasNotStarted_ReportsCommunity()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(30), Now.AddYears(1), LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.NotYetValid, summary.Stage);
        Assert.Equal(InstallationEdition.Community, summary.Edition);
        Assert.All(summary.Capabilities, capability => Assert.False(capability.IsAvailable));
        Assert.All(
            summary.Capabilities,
            capability => Assert.Equal(PremiumCapabilityUnavailableReason.NotYetValid, capability.Reason));
    }

    // Availability and the reason behind it, one case per stage. A caller branches on the reason, so an
    // installation whose license ran out has to be told apart from one that never had a license: the first
    // renews, the second obtains one.
    [Theory]
    [InlineData(-30, 365, LicenseStage.Active, true, null)]
    [InlineData(-30, 10, LicenseStage.Warning, true, null)]
    [InlineData(-30, -1, LicenseStage.Grace, true, null)]
    [InlineData(-30, -15, LicenseStage.Reverted, false, PremiumCapabilityUnavailableReason.Reverted)]
    [InlineData(30, 365, LicenseStage.NotYetValid, false, PremiumCapabilityUnavailableReason.NotYetValid)]
    public async Task GetCapabilityAsync_ResolvesAvailabilityAndReasonPerStage(
        int notBeforeDays,
        int expiresAtDays,
        LicenseStage expectedStage,
        bool expectedAvailability,
        PremiumCapabilityUnavailableReason? expectedReason)
    {
        await using var db = CreateContext();
        var state = this.VerifiedState(Now.AddDays(notBeforeDays), Now.AddDays(expiresAtDays), LicensedKey);
        var sut = this.CreateService(db, state);

        var capability = await sut.GetCapabilityAsync(LicensedKey);

        Assert.Equal(expectedStage, state.Stage);
        Assert.Equal(expectedAvailability, capability.IsAvailable);
        Assert.Equal(expectedReason, capability.Reason);
    }

    // The two stages outside the entitlement carry their own message, because an operator renewing a license and
    // one waiting for a term to start need to be told different things.
    [Fact]
    public async Task GetCapabilityAsync_TheStagesOutsideTheEntitlement_CarryTheirOwnMessages()
    {
        await using var revertedDb = CreateContext();
        var reverted = await this
            .CreateService(revertedDb, this.VerifiedState(Now.AddYears(-2), Now.AddDays(-15), LicensedKey))
            .GetCapabilityAsync(LicensedKey);

        await using var notYetValidDb = CreateContext();
        var notYetValid = await this
            .CreateService(notYetValidDb, this.VerifiedState(Now.AddDays(30), Now.AddYears(1), LicensedKey))
            .GetCapabilityAsync(LicensedKey);

        await using var noLicenseDb = CreateContext();
        var noLicense = await this.CreateService(noLicenseDb, LicenseState.None()).GetCapabilityAsync(LicensedKey);

        Assert.Contains("expired", reverted.Message, StringComparison.Ordinal);
        Assert.Contains("has not started yet", notYetValid.Message, StringComparison.Ordinal);
        Assert.Equal(3, new[] { reverted.Message, notYetValid.Message, noLicense.Message }.Distinct(StringComparer.Ordinal).Count());
    }

    // The panel reads the boundaries off the summary, so they have to survive the trip through the DTO whatever
    // the edition: an installation that has reverted needs to see when its term ended as much as one inside it.
    [Fact]
    public async Task GetSummaryAsync_CarriesTheStageAndItsBoundaryInstants()
    {
        await using var db = CreateContext();
        var notBefore = Now.AddYears(-1);
        var expiresAt = Now.AddDays(10);
        var state = this.VerifiedState(notBefore, expiresAt, LicensedKey);
        var sut = this.CreateService(db, state);

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.Warning, summary.Stage);
        Assert.Equal(notBefore, summary.NotBefore);
        Assert.Equal(expiresAt, summary.ExpiresAt);
        Assert.Equal(expiresAt - LicenseState.WarningWindow, summary.WarningStartsAt);
        Assert.Equal(expiresAt + LicenseState.GraceWindow, summary.GraceEndsAt);
        Assert.Equal(10, summary.DaysRemaining);
    }

    [Fact]
    public async Task GetSummaryAsync_NoLicense_CarriesNoStageAndNoBoundaryInstants()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, LicenseState.None());

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.None, summary.Stage);
        Assert.Null(summary.NotBefore);
        Assert.Null(summary.ExpiresAt);
        Assert.Null(summary.WarningStartsAt);
        Assert.Null(summary.GraceEndsAt);
        Assert.Null(summary.DaysRemaining);
    }

    // The count is null before the term begins because no entitlement has started to be used up, and the
    // contract says so, so a client that renders it does not read a zero as "expiring today".
    [Fact]
    public async Task GetSummaryAsync_LicenseWhoseTermHasNotStarted_ReportsTheBoundariesWithoutADaysRemaining()
    {
        await using var db = CreateContext();
        var notBefore = Now.AddDays(30);
        var expiresAt = Now.AddYears(1);
        var sut = this.CreateService(db, this.VerifiedState(notBefore, expiresAt, LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.NotYetValid, summary.Stage);
        Assert.Equal(notBefore, summary.NotBefore);
        Assert.Equal(expiresAt, summary.ExpiresAt);
        Assert.Null(summary.DaysRemaining);
    }

    // A renewal request quotes who the license was issued to and which license it is, so both survive the trip
    // through the DTO.
    [Fact]
    public async Task GetSummaryAsync_CarriesWhoTheLicenseWasIssuedToAndItsIdentifier()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal("ProPR Licensing Test Fixtures", summary.Licensee);
        Assert.Equal("0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40", summary.LicenseId);
    }

    // The two are read off the license document, so an installation with none reports neither rather than an
    // empty string a panel would render as a blank licensee.
    [Fact]
    public async Task GetSummaryAsync_NoLicense_CarriesNoLicenseeAndNoLicenseIdentifier()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, LicenseState.None());

        var summary = await sut.GetSummaryAsync();

        Assert.Null(summary.Licensee);
        Assert.Null(summary.LicenseId);
    }

    // A license that has run out is still the document an operator renews from, so what it says stays readable
    // after the grace window closes.
    [Fact]
    public async Task GetSummaryAsync_RevertedLicense_StillCarriesWhatTheDocumentSays()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddYears(-2), Now.AddDays(-15), LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseStage.Reverted, summary.Stage);
        Assert.Equal("ProPR Licensing Test Fixtures", summary.Licensee);
        Assert.NotNull(summary.Limits);
        Assert.Equal(4, LimitFor(summary, LicenseLimitKey.Runners).LicensedCount);
    }

    // All four dimensions are reported whatever the license states, so the panel lists the same rows every time
    // and a limit the license leaves out is visible as one it does not constrain.
    [Fact]
    public async Task GetSummaryAsync_ReportsEveryLimitAndTellsAStatedOneFromAnAbsentOne()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.NotNull(summary.Limits);
        Assert.Equal(
            [
                LicenseLimitKey.AuthorsPerMonth,
                LicenseLimitKey.Clients,
                LicenseLimitKey.Runners,
                LicenseLimitKey.ConcurrentReviews,
            ],
            summary.Limits!.Select(limit => limit.Key));

        var runners = LimitFor(summary, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitAllowance.Count, runners.Allowance);
        Assert.Equal(4, runners.LicensedCount);

        var clients = LimitFor(summary, LicenseLimitKey.Clients);
        Assert.Equal(LicenseLimitAllowance.Absent, clients.Allowance);
        Assert.Null(clients.LicensedCount);
    }

    // A limit stated as unlimited is a different answer from one the license leaves out, and the panel renders
    // them differently, so the two cannot collapse into each other.
    [Fact]
    public async Task GetSummaryAsync_TellsAnUnlimitedLimitFromAnAbsentOne()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(
            db,
            this.VerifiedState(
                Now.AddDays(-1),
                Now.AddYears(1),
                [LicensedKey],
                Now.AddDays(-2),
                new LicenseLimits { Clients = LicenseLimit.Unlimited, ConcurrentReviews = LicenseLimit.Of(0) }));

        var summary = await sut.GetSummaryAsync();

        Assert.Equal(LicenseLimitAllowance.Unlimited, LimitFor(summary, LicenseLimitKey.Clients).Allowance);
        Assert.Null(LimitFor(summary, LicenseLimitKey.Clients).LicensedCount);
        Assert.Equal(LicenseLimitAllowance.Count, LimitFor(summary, LicenseLimitKey.ConcurrentReviews).Allowance);
        Assert.Equal(0, LimitFor(summary, LicenseLimitKey.ConcurrentReviews).LicensedCount);
        Assert.Equal(LicenseLimitAllowance.Absent, LimitFor(summary, LicenseLimitKey.AuthorsPerMonth).Allowance);
    }

    // An installation with no license still lists the four dimensions, so the panel's limits card is readable
    // before a license is activated rather than empty.
    [Fact]
    public async Task GetSummaryAsync_NoLicense_ReportsEveryLimitAsAbsent()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, LicenseState.None());

        var summary = await sut.GetSummaryAsync();

        Assert.NotNull(summary.Limits);
        Assert.Equal(4, summary.Limits!.Count);
        Assert.All(summary.Limits, limit => Assert.Equal(LicenseLimitAllowance.Absent, limit.Allowance));
    }

    // The counts belong to the administration read, which gathers them. Every other caller of this summary is a
    // sign-in or session read that would pay for database work it never looks at.
    [Fact]
    public async Task GetSummaryAsync_ReportsNoCurrentCounts()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var summary = await sut.GetSummaryAsync();

        Assert.All(summary.Limits!, limit => Assert.Null(limit.InformationalCount));
    }

    // The identifier is carried whatever the edition and whatever the stage, because it says which installation
    // this is rather than what it is entitled to, and a Community installation quotes it in a support
    // conversation as much as a licensed one.
    [Theory]
    [InlineData(LicenseFixture.None)]
    [InlineData(LicenseFixture.WithTheKey)]
    public async Task GetSummaryAsync_CarriesTheSameLicensingIdentityOnEveryRead(LicenseFixture fixture)
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.StateFor(fixture));

        var summary = await sut.GetSummaryAsync();
        var reread = await sut.GetSummaryAsync();

        Assert.NotNull(summary.LicensingIdentity);
        Assert.NotEqual(Guid.Empty, summary.LicensingIdentity!.Value);
        Assert.Equal(summary.LicensingIdentity, reread.LicensingIdentity);
        Assert.Equal(summary.LicensingIdentity, (await db.LicensingIdentity.AsNoTracking().SingleAsync()).Identifier);
    }

    [Fact]
    public async Task UpdateAsync_CarriesTheLicensingIdentityInTheSummaryItReturns()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var summary = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled)],
            null);

        Assert.Equal((await db.LicensingIdentity.AsNoTracking().SingleAsync()).Identifier, summary.LicensingIdentity);
    }

    [Fact]
    public async Task GetAuthOptionsAsync_SsoInTheLicense_OffersTheSsoSignInMethod()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(
            db,
            this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), PremiumCapabilityKey.SsoAuthentication));

        var options = await sut.GetAuthOptionsAsync();

        Assert.Equal(InstallationEdition.Commercial, options.Edition);
        Assert.Contains("sso", options.AvailableSignInMethods);
    }

    [Fact]
    public async Task GetAuthOptionsAsync_SsoNotInTheLicense_OffersPasswordOnly()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var options = await sut.GetAuthOptionsAsync();

        Assert.Equal(["password"], options.AvailableSignInMethods);
    }

    [Fact]
    public async Task UpdateAsync_DisablingALicensedCapability_PersistsTheOverrideAndReportsItInTheSummary()
    {
        await using var db = CreateContext();
        var actorUserId = Guid.NewGuid();
        var sut = this.CreateService(
            db,
            this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey, UnlicensedKey));

        var summary = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled)],
            actorUserId);

        var licensed = Assert.Single(summary.Capabilities, capability => capability.Key == LicensedKey);
        Assert.Equal(PremiumCapabilityOverrideState.Disabled, licensed.OverrideState);
        Assert.False(licensed.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.DisabledByOverride, licensed.Reason);
        Assert.Equal("Mention answering is currently disabled for this installation.", licensed.Message);

        var stillAvailable = Assert.Single(summary.Capabilities, capability => capability.Key == UnlicensedKey);
        Assert.True(stillAvailable.IsAvailable);

        var overrideRecord = await db.PremiumCapabilityOverrides.AsNoTracking().SingleAsync();
        Assert.Equal(LicensedKey, overrideRecord.CapabilityKey);
        Assert.Equal(PremiumCapabilityOverrideState.Disabled, overrideRecord.OverrideState);
        Assert.Equal(actorUserId, overrideRecord.UpdatedByUserId);
    }

    [Fact]
    public async Task UpdateAsync_DefaultOverride_ClearsThePreviousOverride()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled)],
            null);

        var summary = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Default)],
            null);

        var licensed = Assert.Single(summary.Capabilities, capability => capability.Key == LicensedKey);
        Assert.Equal(PremiumCapabilityOverrideState.Default, licensed.OverrideState);
        Assert.True(licensed.IsAvailable);
        Assert.Empty(await db.PremiumCapabilityOverrides.AsNoTracking().ToListAsync());
    }

    // The write path takes an override state and nothing else, so the only two it can be given are the two the
    // endpoint accepts. Disabling a capability no license names still leaves it unavailable for the license's
    // reason, and clearing the override cannot make it available either.
    [Fact]
    public async Task UpdateAsync_DisablingACapabilityNoLicenseNames_LeavesItUnavailableForTheLicensesReason()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, LicenseState.None());

        var disabled = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled)],
            null);

        var afterDisable = Assert.Single(disabled.Capabilities, capability => capability.Key == LicensedKey);
        Assert.False(afterDisable.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.NoLicense, afterDisable.Reason);

        var cleared = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Default)],
            null);

        var afterClear = Assert.Single(cleared.Capabilities, capability => capability.Key == LicensedKey);
        Assert.False(afterClear.IsAvailable);
        Assert.Equal(PremiumCapabilityUnavailableReason.NoLicense, afterClear.Reason);
    }

    [Fact]
    public async Task UpdateAsync_UnknownCapability_IsRefusedAndPersistsNothing()
    {
        await using var db = CreateContext();
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.UpdateAsync(
            [
                new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled),
                new CapabilityOverrideMutation("not-a-capability", PremiumCapabilityOverrideState.Disabled),
            ],
            null));

        Assert.Empty(await db.PremiumCapabilityOverrides.AsNoTracking().ToListAsync());
    }

    // The stored edition follows from the activated license, so an override write must leave it alone.
    [Fact]
    public async Task UpdateAsync_LeavesTheStoredEditionUntouched()
    {
        await using var db = CreateContext();
        await SeedStoredEditionAsync(db, InstallationEdition.Commercial);
        var sut = this.CreateService(db, this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey));

        var summary = await sut.UpdateAsync(
            [new CapabilityOverrideMutation(LicensedKey, PremiumCapabilityOverrideState.Disabled)],
            null);

        Assert.Equal(InstallationEdition.Commercial, summary.Edition);
        Assert.Equal(
            InstallationEdition.Commercial,
            (await db.InstallationEditions.AsNoTracking().SingleAsync()).Edition);
    }

    /// <summary>Which license the installation has, as the resolution matrix varies it.</summary>
    public enum LicenseFixture
    {
        /// <summary>No license is on file.</summary>
        None = 0,

        /// <summary>A license whose term is running, naming a different capability.</summary>
        WithoutTheKey = 1,

        /// <summary>A license whose term is running, naming the capability under test.</summary>
        WithTheKey = 2,
    }

    private LicenseState StateFor(LicenseFixture fixture)
    {
        return fixture switch
        {
            LicenseFixture.WithoutTheKey => this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), UnlicensedKey),
            LicenseFixture.WithTheKey => this.VerifiedState(Now.AddDays(-1), Now.AddYears(1), LicensedKey),
            _ => LicenseState.None(),
        };
    }

    private static LicenseLimitDto LimitFor(LicensingSummaryDto summary, LicenseLimitKey key) =>
        Assert.Single(summary.Limits!.Where(limit => limit.Key == key));

    private LicenseState VerifiedState(
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        params string[] capabilities)
    {
        return this.VerifiedState(notBefore, expiresAt, capabilities, Now.AddDays(-2));
    }

    /// <summary>
    ///     A license state over a document this test's chain signed and the product's own verifier accepted, so
    ///     the term status, the lifecycle stage and the capability list the resolution reads are the ones a
    ///     stored document carries.
    /// </summary>
    private LicenseState VerifiedState(
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> capabilities,
        DateTimeOffset activatedAt,
        LicenseLimits? limits = null)
    {
        var claims = LicenseTestChain.ClaimsFor(notBefore, expiresAt) with { Capabilities = capabilities };
        if (limits is not null)
        {
            claims = claims with { Limits = limits };
        }

        var compactLicense = this._chain.Sign(claims);
        var verification = this._verifier.Verify(compactLicense, Now);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        return LicenseState.Verified(verification.License, Now, activatedAt, null);
    }

    private LicensingCapabilityService CreateService(MeisterProPRDbContext db, LicenseState licenseState)
    {
        var catalog = new StaticPremiumCapabilityCatalog();

        return new LicensingCapabilityService(
            catalog,
            new LicensingPolicyRepository(db, catalog),
            new FixedLicenseStateProvider(licenseState),
            new LicensingIdentityRepository(db, TimeProvider.System));
    }

    /// <summary>
    ///     Writes an override row directly, which is the only way to establish one carrying the removed enable
    ///     state: no write path can produce it any more.
    /// </summary>
    private static async Task SeedOverrideAsync(
        MeisterProPRDbContext db,
        string capabilityKey,
        PremiumCapabilityOverrideState overrideState)
    {
        if (overrideState == PremiumCapabilityOverrideState.Default)
        {
            return;
        }

        db.PremiumCapabilityOverrides.Add(
            new PremiumCapabilityOverrideRecord
            {
                CapabilityKey = capabilityKey,
                OverrideState = overrideState,
                UpdatedAt = Now,
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    ///     Writes the stored edition column. Written directly because no code path sets it any more: what the
    ///     installation is entitled to comes from the license.
    /// </summary>
    private static async Task SeedStoredEditionAsync(MeisterProPRDbContext db, InstallationEdition edition)
    {
        db.InstallationEditions.Add(
            new InstallationEditionRecord
            {
                Id = SingletonPolicyId,
                Edition = edition,
                UpdatedAt = Now,
            });

        await db.SaveChangesAsync();
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicensingCapabilityService_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private sealed class FixedLicenseStateProvider(LicenseState state) : ILicenseStateProvider
    {
        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(state);
        }

        public void Invalidate()
        {
        }
    }
}
