// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Api.Features.Licensing.Controllers;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicenseActivationHistory;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetSystemProfile;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Licensing;

public sealed class AdminLicensingControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     The serializer configuration the API installs on its controllers, so a payload asserted here is
    ///     written the way the endpoint writes it rather than the way a default serializer would.
    /// </summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = CreateApiJsonOptions();

    [Fact]
    public async Task GetLicensing_AdminCaller_ReturnsCurrentSummary()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<LicensingSummaryDto>(ok.Value);
        Assert.Equal(InstallationEdition.Community, payload.Edition);
        Assert.Single(payload.Capabilities);
    }

    // The panel reports the licensed value next to what the installation currently holds. The counts are
    // reporting numbers, so they are gathered on this read alone rather than by the capability service every
    // session read goes through.
    [Fact]
    public async Task GetLicensing_ReportsTheCurrentCountAgainstEachMeasuredLimit()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var payload = Assert.IsType<LicensingSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(payload.Limits);
        Assert.Equal(4, LimitFor(payload, LicenseLimitKey.Clients).InformationalCount);
        Assert.Equal(2, LimitFor(payload, LicenseLimitKey.Runners).InformationalCount);
        Assert.Equal(1, LimitFor(payload, LicenseLimitKey.ConcurrentReviews).InformationalCount);
    }

    // Authors per month is counted from the rollup, and this harness composes the handler without one.
    // Reporting zero would read as an installation nobody opened a pull request on, so the row carries no
    // number instead.
    [Fact]
    public async Task GetLicensing_WithoutARollup_LeavesTheAuthorsLimitWithoutACount()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var payload = Assert.IsType<LicensingSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);
        var authors = LimitFor(payload, LicenseLimitKey.AuthorsPerMonth);
        Assert.Null(authors.InformationalCount);
        Assert.Equal(50, authors.LicensedCount);
    }

    // The ceiling comes from the resolver every enforcement point reads, so the payload reports the number a
    // refusal quotes. Here the license states unlimited runners and no concurrent-review limit at all, while
    // the resolved ceilings are the community values, and those are what the payload has to carry.
    [Fact]
    public async Task GetLicensing_ReportsTheEffectiveCeilingAndItsSourceForEveryLimit()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var payload = Assert.IsType<LicensingSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);

        var clients = LimitFor(payload, LicenseLimitKey.Clients);
        Assert.Equal(LicenseLimitCeiling.Count, clients.EffectiveCeiling);
        Assert.Equal(10, clients.EffectiveCount);
        Assert.Equal(LicenseLimitSource.License, clients.EffectiveSource);

        var runners = LimitFor(payload, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitAllowance.Unlimited, runners.Allowance);
        Assert.Equal(LicenseLimitCeiling.Count, runners.EffectiveCeiling);
        Assert.Equal(0, runners.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, runners.EffectiveSource);

        var concurrentReviews = LimitFor(payload, LicenseLimitKey.ConcurrentReviews);
        Assert.Equal(LicenseLimitAllowance.Absent, concurrentReviews.Allowance);
        Assert.Equal(LicenseLimitCeiling.Count, concurrentReviews.EffectiveCeiling);
        Assert.Equal(1, concurrentReviews.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, concurrentReviews.EffectiveSource);
    }

    // A dimension nothing counts carries no ceiling to enforce, which is a different answer from a counted
    // dimension held to no ceiling. The payload keeps the two apart, and carries no number for either.
    [Fact]
    public async Task GetLicensing_ReportsAnUnmeteredDimensionWithoutANumber()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var payload = Assert.IsType<LicensingSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);
        var authors = LimitFor(payload, LicenseLimitKey.AuthorsPerMonth);

        Assert.Equal(LicenseLimitCeiling.Unmetered, authors.EffectiveCeiling);
        Assert.Null(authors.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, authors.EffectiveSource);
        Assert.Equal(50, authors.LicensedCount);
    }

    // The two author fields are read off the wire rather than off the DTO, because the peak carries a date and
    // a date is the one member of this payload whose serialized form is not the property value itself. A month
    // written as a full instant, or with a time zone applied to it, would move the month a reader sees.
    [Fact]
    public async Task GetLicensing_WritesTheAuthorOverageAndThePeakMonthAsTheApiSerializesThem()
    {
        var rollup = Substitute.For<IAuthorActivityRollupStore>();
        rollup.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>()).Returns(new AuthorActivityMonthCounts(new DateOnly(2026, 8, 1), 63, 0));
        rollup.GetTrailingYearPeakAsync(Arg.Any<CancellationToken>())
            .Returns(new AuthorMonthCount(new DateOnly(2026, 4, 1), 71));

        var evaluator = Substitute.For<IAuthorOverageEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<AuthorMonthCount?>(), Arg.Any<CancellationToken>())
            .Returns(new AuthorOverageState(50, 63, IsInOverage: true));

        using var harness = Harness.Create(rollupStore: rollup, overageEvaluator: evaluator);

        var result = await harness.Controller.GetLicensing(CancellationToken.None);

        var payload = Assert.IsType<LicensingSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);
        var json = JsonSerializer.Serialize(payload, ApiJsonOptions);

        Assert.Contains("""authorOverage":{"licensedCount":50,"observedCount":63,"isInOverage":true}""", json, StringComparison.Ordinal);
        Assert.Contains("""authorPeakMonth":{"month":"2026-04-01","authorCount":71}""", json, StringComparison.Ordinal);
    }

    // The evaluation gets the count the read already took and the month it was counted for, so the endpoint
    // counts the month once and the record is written against the month the number describes.
    [Fact]
    public async Task GetLicensing_HandsTheAuthorCountItReadAndItsMonthToTheEvaluation()
    {
        var rollup = Substitute.For<IAuthorActivityRollupStore>();
        rollup.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>())
            .Returns(new AuthorActivityMonthCounts(new DateOnly(2026, 8, 1), 63, 0));
        var evaluator = Substitute.For<IAuthorOverageEvaluator>();

        using var harness = Harness.Create(rollupStore: rollup, overageEvaluator: evaluator);

        await harness.Controller.GetLicensing(CancellationToken.None);

        await evaluator.Received(1)
            .EvaluateAsync(new AuthorMonthCount(new DateOnly(2026, 8, 1), 63), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryRoute_RefusesAnAuthenticatedNonAdminCaller()
    {
        using var harness = Harness.Create(isAdmin: false, actorUserId: Guid.NewGuid());

        foreach (var result in await harness.InvokeEveryRouteAsync())
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task EveryRoute_RefusesAnUnauthenticatedCaller()
    {
        using var harness = Harness.Create(isAdmin: false);

        foreach (var result in await harness.InvokeEveryRouteAsync())
        {
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task ActivateLicense_AcceptedDocument_StoresItRecordsItAndInvalidatesTheCachedState()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        var compactLicense = harness.SignAcceptedLicense("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05", "Northwind Traders");

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(compactLicense),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<LicensingSummaryDto>(ok.Value);
        Assert.Equal(compactLicense, harness.LicenseStore.CompactLicense);

        var recorded = Assert.Single(await harness.EventStore.ListRecentAsync(10));
        Assert.Equal(LicenseActivationAction.Activated, recorded.Action);
        Assert.Equal(Now, recorded.OccurredAt);
        Assert.Equal(harness.ActorUserId, recorded.ActorUserId);
        Assert.Equal("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05", recorded.LicenseId);
        Assert.Equal("Northwind Traders", recorded.Licensee);

        harness.LicenseStateProvider.Received(1).Invalidate();
    }

    [Fact]
    public async Task ActivateLicense_RequestCancellationAfterStorage_DoesNotCancelTheHistoryWrite()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        harness.LicenseStore.AfterMutation = cancellation.Cancel;
        var compactLicense = harness.SignAcceptedLicense("activation-cancellation", "Northwind Traders");

        await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(compactLicense),
            cancellation.Token);

        Assert.Equal(compactLicense, harness.LicenseStore.CompactLicense);
        harness.LicenseStateProvider.Received(1).Invalidate();
        Assert.Single(await harness.EventStore.ListRecentAsync(10));
        Assert.False(harness.EventStore.LastRecordCancellationToken.IsCancellationRequested);
        await harness.LicensingCapabilityService.Received(1)
            .GetSummaryAsync(Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    [Fact]
    public async Task ActivateLicense_OverAnExistingLicense_RecordsOneReplacement()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        await harness.LicenseStore.SetAsync(harness.SignAcceptedLicense("first-license", "Northwind Traders"), null);
        var replacement = harness.SignAcceptedLicense("second-license", "Northwind Traders");

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(replacement),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(replacement, harness.LicenseStore.CompactLicense);

        var recorded = Assert.Single(await harness.EventStore.ListRecentAsync(10));
        Assert.Equal(LicenseActivationAction.Replaced, recorded.Action);
        Assert.Equal("second-license", recorded.LicenseId);
    }

    // The document is verified before anything is stored, so a document this build does not accept has to leave
    // the installation on the license it already had.
    [Fact]
    public async Task ActivateLicense_DocumentFromAnotherSigner_IsRefusedAndLeavesThePreviousLicenseInPlace()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        var previous = harness.SignAcceptedLicense("first-license", "Northwind Traders");
        await harness.LicenseStore.SetAsync(previous, null);

        using var otherChain = LicenseTestChain.Create();
        var foreignLicense = otherChain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(foreignLicense),
            CancellationToken.None);

        AssertRefusedWith(LicenseFailureReason.UntrustedSigner, result);
        Assert.Equal(previous, harness.LicenseStore.CompactLicense);
        Assert.Empty(await harness.EventStore.ListRecentAsync(10));
        harness.LicenseStateProvider.DidNotReceive().Invalidate();
    }

    [Fact]
    public async Task ActivateLicense_EmptyDocument_IsRefusedAsMalformed()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest("   "),
            CancellationToken.None);

        AssertRefusedWith(LicenseFailureReason.Malformed, result);
        Assert.Null(harness.LicenseStore.CompactLicense);
    }

    // A document whose term has ended is in force for as long as its grace window runs, so activating it is how
    // an installation rebuilt from a backup comes back up on the license file it already had.
    [Fact]
    public async Task ActivateLicense_DocumentInsideItsGraceWindow_IsAcceptedAndStored()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        var inGrace = harness.Chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-1)) with { LicenseId = "in-grace" });

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(inGrace),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(inGrace, harness.LicenseStore.CompactLicense);
        Assert.Equal("in-grace", Assert.Single(await harness.EventStore.ListRecentAsync(10)).LicenseId);
    }

    // Activation is the one place the license lifecycle is a refusal rather than a reported state. Past the grace
    // window the document grants nothing, so storing it would leave the installation configured with a license it
    // cannot use.
    [Fact]
    public async Task ActivateLicense_DocumentPastItsGraceWindow_IsRefusedAndStoresNothing()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        var expiredLicense = harness.Chain.Sign(LicenseTestChain.ClaimsFor(Now.AddYears(-2), Now.AddDays(-15)));

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(expiredLicense),
            CancellationToken.None);

        AssertRefusedWith(LicenseFailureReason.Expired, result);
        Assert.Null(harness.LicenseStore.CompactLicense);
        Assert.Empty(await harness.EventStore.ListRecentAsync(10));
    }

    [Fact]
    public async Task ActivateLicense_DocumentWhoseTermHasNotStarted_IsRefusedAndStoresNothing()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        var futureLicense = harness.Chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(30), Now.AddYears(1)));

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(futureLicense),
            CancellationToken.None);

        AssertRefusedWith(LicenseFailureReason.NotYetValid, result);
        Assert.Null(harness.LicenseStore.CompactLicense);
        Assert.Empty(await harness.EventStore.ListRecentAsync(10));
    }

    [Fact]
    public async Task RemoveLicense_LicenseOnFile_RemovesItRecordsItAndInvalidatesTheCachedState()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        await harness.LicenseStore.SetAsync(harness.SignAcceptedLicense("first-license", "Northwind Traders"), null);

        var result = await harness.Controller.RemoveLicense(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(harness.LicenseStore.CompactLicense);

        var recorded = Assert.Single(await harness.EventStore.ListRecentAsync(10));
        Assert.Equal(LicenseActivationAction.Removed, recorded.Action);
        Assert.Equal(harness.ActorUserId, recorded.ActorUserId);
        Assert.Equal("first-license", recorded.LicenseId);
        Assert.Equal("Northwind Traders", recorded.Licensee);

        harness.LicenseStateProvider.Received(1).Invalidate();
    }

    [Fact]
    public async Task RemoveLicense_RequestCancellationAfterStorage_DoesNotCancelTheHistoryWrite()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        await harness.LicenseStore.SetAsync(harness.SignAcceptedLicense("removal-cancellation", "Northwind Traders"), null);
        using var cancellation = new CancellationTokenSource();
        harness.LicenseStore.AfterMutation = cancellation.Cancel;

        await harness.Controller.RemoveLicense(cancellation.Token);

        Assert.Null(harness.LicenseStore.CompactLicense);
        harness.LicenseStateProvider.Received(1).Invalidate();
        Assert.Single(await harness.EventStore.ListRecentAsync(10));
        Assert.False(harness.EventStore.LastRecordCancellationToken.IsCancellationRequested);
    }

    // The license has already changed when the history write runs, so failing the request would report an
    // activation that did not happen while the installation runs on the new license. The failure is reported in
    // the log instead, and the cached state is discarded whatever the history write does.
    [Fact]
    public async Task ActivateLicense_HistoryWriteFails_KeepsTheActivationAndReportsTheGapInTheLog()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        harness.EventStore.FailWrites = true;
        var compactLicense = harness.SignAcceptedLicense("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05", "Northwind Traders");

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(compactLicense),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(compactLicense, harness.LicenseStore.CompactLicense);
        harness.LicenseStateProvider.Received(1).Invalidate();

        var entry = Assert.Single(harness.ActivationLog.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains("was not recorded", entry.Message, StringComparison.Ordinal);
        Assert.Contains("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    // The summary is read after the license has been stored, and that read reloads the state, advances the
    // observed instant and on a first activation seeds the identity and observes the profile. Raising from any
    // of it would tell an operator the activation failed while the installation runs on the new license, and
    // the retry would record a second replacement.
    [Fact]
    public async Task ActivateLicense_SummaryReadFails_KeepsTheActivationAndReportsItWithoutTheSummary()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        harness.LicensingCapabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns<LicensingSummaryDto>(_ => throw new InvalidOperationException("the database went away"));
        var compactLicense = harness.SignAcceptedLicense("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05", "Northwind Traders");

        var result = await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(compactLicense),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(compactLicense, harness.LicenseStore.CompactLicense);
        Assert.Single(await harness.EventStore.ListRecentAsync(10));
        harness.LicenseStateProvider.Received(1).Invalidate();

        var entry = Assert.Single(harness.ActivationLog.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains("could not be read back", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    [Fact]
    public async Task RemoveLicense_HistoryWriteFails_KeepsTheRemovalAndReportsTheGapInTheLog()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        await harness.LicenseStore.SetAsync(harness.SignAcceptedLicense("first-license", "Northwind Traders"), null);
        harness.EventStore.FailWrites = true;

        var result = await harness.Controller.RemoveLicense(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(harness.LicenseStore.CompactLicense);
        harness.LicenseStateProvider.Received(1).Invalidate();

        var entry = Assert.Single(harness.RemovalLog.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains("was not recorded", entry.Message, StringComparison.Ordinal);
        Assert.Contains("first-license", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    [Fact]
    public async Task RemoveLicense_NoLicenseOnFile_ReportsSuccessAndRecordsNothing()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());

        var result = await harness.Controller.RemoveLicense(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await harness.EventStore.ListRecentAsync(10));
        harness.LicenseStateProvider.DidNotReceive().Invalidate();
    }

    // An installation answers from the record once the license itself is gone, so removing one has to leave the
    // earlier entries in place.
    [Fact]
    public async Task GetLicenseHistory_ReturnsEveryChangeNewestFirstAndKeepsThemAfterRemoval()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());

        await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(harness.SignAcceptedLicense("first-license", "Northwind Traders")),
            CancellationToken.None);
        harness.Advance(TimeSpan.FromMinutes(5));
        await harness.Controller.ActivateLicense(
            new ActivateLicenseRequest(harness.SignAcceptedLicense("second-license", "Northwind Traders")),
            CancellationToken.None);
        harness.Advance(TimeSpan.FromMinutes(5));
        await harness.Controller.RemoveLicense(CancellationToken.None);

        var result = await harness.Controller.GetLicenseHistory(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var history = Assert.IsAssignableFrom<IReadOnlyList<LicenseActivationEventDto>>(ok.Value);

        Assert.Collection(
            history,
            entry =>
            {
                Assert.Equal(LicenseActivationAction.Removed, entry.Action);
                Assert.Equal("second-license", entry.LicenseId);
            },
            entry =>
            {
                Assert.Equal(LicenseActivationAction.Replaced, entry.Action);
                Assert.Equal("second-license", entry.LicenseId);
            },
            entry =>
            {
                Assert.Equal(LicenseActivationAction.Activated, entry.Action);
                Assert.Equal("first-license", entry.LicenseId);
            });
    }

    [Fact]
    public async Task PatchLicensingOverrides_AdminCaller_ForwardsActorAndOverrideMutation()
    {
        var actorUserId = Guid.NewGuid();
        using var harness = Harness.Create(actorUserId: actorUserId);
        harness.LicensingCapabilityService.UpdateAsync(
                Arg.Any<IReadOnlyCollection<CapabilityOverrideMutation>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateSummary(InstallationEdition.Commercial));

        var result = await harness.Controller.PatchLicensingOverrides(
            new PatchLicensingOverridesRequest(
            [
                new PatchPremiumCapabilityOverrideRequest(
                    PremiumCapabilityKey.MultipleScmProviders,
                    PremiumCapabilityOverrideState.Disabled),
            ]),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<LicensingSummaryDto>(ok.Value);
        Assert.Equal(InstallationEdition.Commercial, payload.Edition);

        await harness.LicensingCapabilityService.Received(1)
            .UpdateAsync(
                Arg.Is<IReadOnlyCollection<CapabilityOverrideMutation>>(mutations =>
                    mutations.Count == 1
                    && mutations.First().Key == PremiumCapabilityKey.MultipleScmProviders
                    && mutations.First().OverrideState == PremiumCapabilityOverrideState.Disabled),
                actorUserId,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchLicensingOverrides_UnknownCapability_ReturnsBadRequest()
    {
        using var harness = Harness.Create(actorUserId: Guid.NewGuid());
        harness.LicensingCapabilityService.UpdateAsync(
                Arg.Any<IReadOnlyCollection<CapabilityOverrideMutation>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<LicensingSummaryDto>>(_ => throw new KeyNotFoundException("Unknown premium capability 'nope'."));

        var result = await harness.Controller.PatchLicensingOverrides(
            new PatchLicensingOverridesRequest(
            [
                new PatchPremiumCapabilityOverrideRequest("nope", PremiumCapabilityOverrideState.Disabled),
            ]),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
    }

    // The read surface has to say which components the installation could not observe, so an absent one comes
    // back as an explicit null rather than being left out of the payload.
    [Fact]
    public async Task GetSystemProfile_ReturnsTheProfileTheChangesAndTheHostNames()
    {
        using var harness = Harness.Create();

        var result = await harness.Controller.GetSystemProfile(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<SystemProfileDto>(ok.Value);

        Assert.Equal("hash-two", payload.Current!.ProfileHash);
        Assert.Equal(Now, payload.Current.CapturedAt);
        Assert.Equal(Now.AddMinutes(15), payload.Current.UpdatedAt);
        Assert.Null(payload.Current.Stable.PostgresSystemIdentifier);
        Assert.Equal("propr", payload.Current.Stable.DatabaseName);
        Assert.Equal(Now, payload.Current.Stable.IdentityCreatedAt);
        Assert.Equal(["1a"], payload.Current.Stable.ScmHostHashes);
        Assert.Equal("replica-a", payload.Current.Volatile.MachineName);

        var drift = Assert.Single(payload.Drift);
        Assert.Equal(["databaseName"], drift.ChangedComponents);
        Assert.Equal("hash-one", drift.PreviousHash);

        Assert.Equal("replica-a", Assert.Single(payload.Hostnames).Hostname);
    }

    // Every route answers with the same 503 when the licensing module is not registered, which is what a
    // deployment without a database configured looks like.
    [Fact]
    public async Task EveryRoute_ReportsServiceUnavailableWithoutTheLicensingModule()
    {
        var controller = new AdminLicensingController();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items["IsAdmin"] = true;

        IActionResult[] results =
        [
            await controller.GetLicensing(CancellationToken.None),
            await controller.ActivateLicense(new ActivateLicenseRequest("token"), CancellationToken.None),
            await controller.RemoveLicense(CancellationToken.None),
            await controller.GetLicenseHistory(CancellationToken.None),
            await controller.GetSystemProfile(CancellationToken.None),
            await controller.PatchLicensingOverrides(new PatchLicensingOverridesRequest(null), CancellationToken.None),
        ];

        Assert.All(
            results,
            result => Assert.Equal(
                StatusCodes.Status503ServiceUnavailable,
                Assert.IsType<StatusCodeResult>(result).StatusCode));
    }

    private static void AssertRefusedWith(LicenseFailureReason expectedReason, IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<LicenseActivationRefusedPayload>(badRequest.Value);

        Assert.Equal("license_not_accepted", payload.Error);
        Assert.Equal(expectedReason, payload.Reason);
        Assert.NotEmpty(payload.Message);
    }

    private static LicenseLimitDto LimitFor(LicensingSummaryDto summary, LicenseLimitKey key) =>
        Assert.Single(summary.Limits!.Where(limit => limit.Key == key));

    private static JsonSerializerOptions CreateApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Program.ConfigureJsonSerialization(options);

        return options;
    }

    private static LicensingSummaryDto CreateSummary(InstallationEdition edition)
    {
        return new LicensingSummaryDto(
            edition,
            edition == InstallationEdition.Commercial ? Now : null,
            [
                new PremiumCapabilityDto(
                    PremiumCapabilityKey.MultipleScmProviders,
                    "Multiple SCM providers",
                    true,
                    PremiumCapabilityOverrideState.Default,
                    edition == InstallationEdition.Commercial,
                    edition == InstallationEdition.Commercial ? null : "A commercial license is required, including in self-hosted deployments."),
            ],
            Limits:
            [
                new LicenseLimitDto(LicenseLimitKey.AuthorsPerMonth, LicenseLimitAllowance.Count, 50),
                new LicenseLimitDto(LicenseLimitKey.Clients, LicenseLimitAllowance.Count, 10),
                new LicenseLimitDto(LicenseLimitKey.Runners, LicenseLimitAllowance.Unlimited),
                new LicenseLimitDto(LicenseLimitKey.ConcurrentReviews, LicenseLimitAllowance.Absent),
            ]);
    }

    /// <summary>
    ///     The controller over the real activation handlers, with the two stores standing in for the database and
    ///     a verifier over a chain generated for the test. The stores are held rather than substituted so a test
    ///     can assert what an activation left behind; the refusal cases check exactly that.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly LicenseTrustAnchor _anchor;
        private readonly MutableTimeProvider _timeProvider;

        private Harness(
            LicenseTestChain chain,
            LicenseTrustAnchor anchor,
            bool isAdmin,
            Guid? actorUserId,
            IAuthorActivityRollupStore? rollupStore,
            IAuthorOverageEvaluator? overageEvaluator)
        {
            this.Chain = chain;
            this._anchor = anchor;
            this._timeProvider = new MutableTimeProvider(Now);
            this.ActorUserId = actorUserId;

            var verifier = new LicenseVerifier(anchor);
            this.LicensingCapabilityService = Substitute.For<ILicensingCapabilityService>();
            this.LicensingCapabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
                .Returns(CreateSummary(InstallationEdition.Community));
            this.LicenseStateProvider = Substitute.For<ILicenseStateProvider>();

            this.Controller = new AdminLicensingController(
                new GetLicensingSummaryHandler(
                    this.LicensingCapabilityService,
                    this.ResourceCounts,
                    this.LimitResolver,
                    rollupStore,
                    overageEvaluator),
                new UpdateLicensingHandler(this.LicensingCapabilityService),
                new ActivateLicenseHandler(
                    verifier,
                    this.LicenseStore,
                    this.EventStore,
                    this.LicenseStateProvider,
                    this.LicensingCapabilityService,
                    new HarnessLicensingClock(this._timeProvider),
                    this.ActivationLog),
                new RemoveLicenseHandler(
                    verifier,
                    this.LicenseStore,
                    this.EventStore,
                    this.LicenseStateProvider,
                    this._timeProvider,
                    this.RemovalLog),
                new GetLicenseActivationHistoryHandler(this.EventStore),
                new GetSystemProfileHandler(this.SystemProfileStore))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };

            if (isAdmin)
            {
                this.Controller.HttpContext.Items["IsAdmin"] = true;
            }

            if (actorUserId.HasValue)
            {
                this.Controller.HttpContext.Items["UserId"] = actorUserId.Value.ToString();
            }
        }

        public ListLogger<ActivateLicenseHandler> ActivationLog { get; } = new();

        public Guid? ActorUserId { get; }

        public LicenseTestChain Chain { get; }

        public AdminLicensingController Controller { get; }

        public InMemoryActivationEventStore EventStore { get; } = new();

        public InMemoryLicenseStore LicenseStore { get; } = new();

        public ListLogger<RemoveLicenseHandler> RemovalLog { get; } = new();

        public ILicenseStateProvider LicenseStateProvider { get; }

        public ILicensingCapabilityService LicensingCapabilityService { get; }

        public ILicenseLimitResolver LimitResolver { get; } = new StubLimitResolver();

        public ILicensedResourceCountSource ResourceCounts { get; } = new StubResourceCountSource();

        public ISystemProfileStore SystemProfileStore { get; } = CreateSystemProfileStore();

        public static Harness Create(
            bool isAdmin = true,
            Guid? actorUserId = null,
            IAuthorActivityRollupStore? rollupStore = null,
            IAuthorOverageEvaluator? overageEvaluator = null)
        {
            var chain = LicenseTestChain.Create();

            return new Harness(
                chain,
                chain.CreateAnchor(),
                isAdmin,
                actorUserId,
                rollupStore,
                overageEvaluator);
        }

        public void Advance(TimeSpan amount) => this._timeProvider.Advance(amount);

        public void Dispose()
        {
            this._anchor.Dispose();
            this.Chain.Dispose();
        }

        public async Task<IReadOnlyList<IActionResult>> InvokeEveryRouteAsync()
        {
            return
            [
                await this.Controller.GetLicensing(CancellationToken.None),
                await this.Controller.ActivateLicense(new ActivateLicenseRequest("token"), CancellationToken.None),
                await this.Controller.RemoveLicense(CancellationToken.None),
                await this.Controller.GetLicenseHistory(CancellationToken.None),
                await this.Controller.GetSystemProfile(CancellationToken.None),
                await this.Controller.PatchLicensingOverrides(
                    new PatchLicensingOverridesRequest(null),
                    CancellationToken.None),
            ];
        }

        /// <summary>
        ///     A store standing in for one observation of the system, so the read surface has a profile, a
        ///     recorded change and a host name to return.
        /// </summary>
        private static ISystemProfileStore CreateSystemProfileStore()
        {
            var store = Substitute.For<ISystemProfileStore>();

            store.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
                new SystemProfileSnapshot
                {
                    Stable = new SystemProfileStableComponents
                    {
                        PostgresSystemIdentifier = null,
                        DatabaseName = "propr",
                        DatabaseOid = 16401,
                        IdentityCreatedAtUnixSeconds = Now.ToUnixTimeSeconds(),
                        ScmHostHashes = ["1a"],
                    },
                    Volatile = new SystemProfileVolatileComponents { MachineName = "replica-a" },
                    ProfileHash = "hash-two",
                    CapturedAt = Now,
                    UpdatedAt = Now.AddMinutes(15),
                });

            store.ListDriftAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                new List<SystemProfileDrift>
                {
                    new()
                    {
                        OccurredAt = Now.AddMinutes(15),
                        ChangedComponents = ["databaseName"],
                        PreviousHash = "hash-one",
                        NewHash = "hash-two",
                    },
                });

            store.ListHostnamesAsync(Arg.Any<CancellationToken>()).Returns(new List<ReplicaHostname> { new("replica-a", Now, Now.AddMinutes(15)) });

            return store;
        }

        /// <summary>A document this harness's verifier accepts, whose term is running at the harness clock.</summary>
        public string SignAcceptedLicense(string licenseId, string licensee)
        {
            return this.Chain.Sign(
                LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with
                {
                    LicenseId = licenseId,
                    Licensee = licensee,
                });
        }
    }

    private sealed class InMemoryLicenseStore : IActivatedLicenseStore
    {
        public Action? AfterMutation { get; set; }

        public string? CompactLicense { get; private set; }

        /// <summary>
        ///     The instant a mutation reports. The real store reads it from the database inside the locked
        ///     transaction, and the history is ordered by it, so a test that cares about ordering sets it.
        /// </summary>
        public DateTimeOffset MutationInstant { get; set; } = Now;

        public Task<StoredLicense?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                this.CompactLicense is null
                    ? null
                    : new StoredLicense
                    {
                        CompactLicense = this.CompactLicense,
                        ActivatedAt = Now,
                    });
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            this.CompactLicense = null;
            this.AfterMutation?.Invoke();

            return Task.CompletedTask;
        }

        public async Task<LicenseMutation> RemoveAndGetAsync(CancellationToken cancellationToken = default)
        {
            var removed = await this.GetAsync(cancellationToken);
            await this.RemoveAsync(cancellationToken);
            return new LicenseMutation(removed, this.MutationInstant);
        }

        public async Task<LicenseMutation> ReplaceAsync(
            string compactLicense,
            Guid? activatedByUserId,
            CancellationToken cancellationToken = default)
        {
            var previous = await this.GetAsync(cancellationToken);
            await this.SetAsync(compactLicense, activatedByUserId, cancellationToken);
            return new LicenseMutation(previous, this.MutationInstant);
        }

        public Task SetAsync(
            string compactLicense,
            Guid? activatedByUserId,
            CancellationToken cancellationToken = default)
        {
            this.CompactLicense = compactLicense;
            this.AfterMutation?.Invoke();

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryActivationEventStore : ILicenseActivationEventStore
    {
        private readonly List<LicenseActivationEvent> _events = [];

        /// <summary>Makes every write fail, which stands in for the history table being unreachable.</summary>
        public bool FailWrites { get; set; }

        public CancellationToken LastRecordCancellationToken { get; private set; }

        public Task<IReadOnlyList<LicenseActivationEvent>> ListRecentAsync(
            int maxEvents,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LicenseActivationEvent>>(this._events.AsEnumerable().Reverse().Take(maxEvents).ToList().AsReadOnly());
        }

        public Task RecordAsync(
            LicenseActivationEvent activationEvent,
            CancellationToken cancellationToken = default)
        {
            this.LastRecordCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.FailWrites)
            {
                throw new InvalidOperationException("The license history could not be written.");
            }

            this._events.Add(activationEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan amount) => this._now += amount;

        public override DateTimeOffset GetUtcNow() => this._now;
    }

    /// <summary>
    ///     The licensing clock over the harness clock, so a test moving one moves both. The production clock
    ///     compares against a stored instant, which has its own tests; here the term instant is the one the test
    ///     set, which is what makes the stage a document is judged in exactly assertable.
    /// </summary>
    private sealed class HarnessLicensingClock(TimeProvider timeProvider) : ILicensingClock
    {
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(timeProvider.GetUtcNow());
    }

    /// <summary>Fixed counts, so a test can assert which limit each of them was reported against.</summary>
    private sealed class StubResourceCountSource : ILicensedResourceCountSource
    {
        public Task<LicensedResourceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicensedResourceCounts(4, 2, 1));
    }

    /// <summary>
    ///     One resolved ceiling per quota, chosen to differ from what the summary's license states wherever
    ///     the two can differ. A payload that copied the stated allowance instead of reading the resolver
    ///     would report the runner ceiling as unlimited and the concurrent-review ceiling as absent.
    /// </summary>
    private sealed class StubLimitResolver : ILicenseLimitResolver
    {
        public Task<LicenseLimitResolution> ResolveAsync(
            LicenseLimitKey key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                key switch
                {
                    LicenseLimitKey.AuthorsPerMonth => LicenseLimitResolution.Unmetered(
                        key,
                        LicenseLimitSource.Community,
                        LicenseStage.Active),
                    LicenseLimitKey.Clients => LicenseLimitResolution.Of(
                        key,
                        10,
                        LicenseLimitSource.License,
                        LicenseStage.Active),
                    LicenseLimitKey.Runners => LicenseLimitResolution.Of(
                        key,
                        0,
                        LicenseLimitSource.Community,
                        LicenseStage.Active),
                    _ => LicenseLimitResolution.Of(key, 1, LicenseLimitSource.Community, LicenseStage.Active),
                });
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
