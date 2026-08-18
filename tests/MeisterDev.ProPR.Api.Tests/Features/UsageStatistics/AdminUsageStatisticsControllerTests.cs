// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.UsageStatistics.Controllers;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.UsageStatistics;

public sealed class AdminUsageStatisticsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetUsageStatistics_AdminCaller_ReturnsTheCurrentState()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true);

        var result = await controller.GetUsageStatistics();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsSettingsDto>(ok.Value);
        Assert.Equal(UsageStatisticsEdition.Community, payload.Edition);
        Assert.False(payload.ManagedByLicense);
    }

    // The endpoints report what the installation sends about itself, so they require a platform administrator
    // rather than any signed-in user.
    [Fact]
    public async Task GetUsageStatistics_NonAdminCaller_IsRefused()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: false);

        var result = await controller.GetUsageStatistics();

        Assert.IsNotType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PatchUsageStatistics_NonAdminCaller_IsRefused()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: false);

        var result = await controller.PatchUsageStatistics(new PatchAdminUsageStatisticsRequest(false));

        Assert.IsNotType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetUsageStatisticsPreview_NonAdminCaller_IsRefused()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: false);

        var result = await controller.GetUsageStatisticsPreview();

        Assert.IsNotType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PatchUsageStatistics_CommunityInstallation_StoresTheChoice()
    {
        var actor = Guid.NewGuid();
        var controller = CreateController(InstallationEdition.Community, isAdmin: true, actor);

        var result = await controller.PatchUsageStatistics(new PatchAdminUsageStatisticsRequest(false));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsSettingsDto>(ok.Value);
        Assert.False(payload.CommunityOptIn);
    }

    // A locked control is refused by the API as well. The UI rendering it disabled is not the enforcement
    // point.
    [Fact]
    public async Task PatchUsageStatistics_CommercialInstallation_IsRefusedWithTheLicenseMessage()
    {
        var controller = CreateController(InstallationEdition.Commercial, isAdmin: true);

        var result = await controller.PatchUsageStatistics(new PatchAdminUsageStatisticsRequest(false));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task GetUsageStatisticsPreview_AdminCaller_ReturnsTheLiteralPayload()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true);

        var result = await controller.GetUsageStatisticsPreview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsPreviewDto>(ok.Value);
        Assert.Contains("\"schemaVersion\":1", payload.Payload, StringComparison.Ordinal);
        Assert.Contains("\"edition\":\"community\"", payload.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSendNow_NonAdminCaller_IsRefused()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: false);

        var result = await controller.PostSendNow();

        Assert.IsNotType<OkObjectResult>(result);
    }

    // The daily loop's rules still apply, so the response reports which of them stopped the send.
    [Fact]
    public async Task PostSendNow_AdminCaller_ReportsWhatTheCycleDecided()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true, gateOpen: false);

        var result = await controller.PostSendNow();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsSendResultDto>(ok.Value);
        Assert.Equal(UsageStatisticsSendDecision.AwaitingConsent, payload.Decision);
    }

    // Without a sender there is nothing to run, so the endpoint reports the service as unavailable rather
    // than throwing, as the rest of the controller does.
    [Fact]
    public async Task PostSendNow_WithoutASender_ReportsTheServiceAsUnavailable()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true, withSender: false);

        var result = await controller.PostSendNow();

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public async Task PostNoticeShown_AdminCaller_OpensTheGate()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true, gateOpen: false);

        var result = await controller.PostNoticeShown();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsSettingsDto>(ok.Value);
        Assert.True(payload.ConsentGateSatisfied);
    }

    [Fact]
    public async Task PostNoticeDismiss_AdminCaller_StopsTheNoticeBeingRequired()
    {
        var controller = CreateController(InstallationEdition.Community, isAdmin: true);

        var result = await controller.PostNoticeDismiss();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<UsageStatisticsSettingsDto>(ok.Value);
        Assert.False(payload.NoticeRequired);
    }

    // The module is not registered without a database, so the endpoint reports the service as unavailable
    // rather than throwing.
    [Fact]
    public async Task AnInstallationWithoutADatabase_ReportsTheServiceAsUnavailable()
    {
        var controller = new AdminUsageStatisticsController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["IsAdmin"] = true;

        var result = await controller.GetUsageStatistics();

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    private static AdminUsageStatisticsController CreateController(
        InstallationEdition edition,
        bool isAdmin,
        Guid? actorUserId = null,
        bool gateOpen = true,
        bool withSender = true)
    {
        var controller = new AdminUsageStatisticsController(CreateService(edition, gateOpen, withSender))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        if (isAdmin)
        {
            controller.HttpContext.Items["IsAdmin"] = true;
        }

        if (actorUserId.HasValue)
        {
            controller.HttpContext.Items["UserId"] = actorUserId.Value.ToString();
        }

        return controller;
    }

    private static UsageStatisticsService CreateService(
        InstallationEdition edition,
        bool gateOpen,
        bool withSender = true)
    {
        var state = new UsageStatisticsState(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            true,
            gateOpen ? Now.AddDays(-1) : null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null);

        var store = Substitute.For<IUsageStatisticsStateStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.SetCommunityOptInAsync(Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(state with { CommunityOptIn = callInfo.ArgAt<bool>(0) }));
        store.RecordConsentGateSatisfiedAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state with { ConsentGateSatisfiedAt = Now }));
        store.RecordNoticeDismissedAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state with { NoticeDismissedAt = Now }));

        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LicensingSummaryDto(edition, null, [])));
        var editionResolver = new UsageStatisticsEditionResolver(licensing);

        var countSource = Substitute.For<IUsageStatisticsCountSource>();
        countSource.CountAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsCounts(3, 4, 5, null, null)));

        var productVersion = Substitute.For<IProductVersionProvider>();
        productVersion.Version.Returns("1.0.0.alpha.0049");

        var timeProvider = new FixedTimeProvider(Now);
        var builder = new UsageStatisticsSnapshotBuilder(countSource, productVersion, timeProvider);

        var sender = withSender
            ? new UsageStatisticsSender(
                store,
                builder,
                editionResolver,
                Substitute.For<IUsageStatisticsPingClient>(),
                timeProvider)
            : null;

        return new UsageStatisticsService(store, builder, editionResolver, productVersion, sender);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
