// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Features.Licensing.Controllers;
using MeisterProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Licensing;

public sealed class AdminLicensingControllerTests
{
    [Fact]
    public async Task GetLicensing_AdminCaller_ReturnsCurrentSummary()
    {
        var licensingService = Substitute.For<ILicensingCapabilityService>();
        licensingService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(CreateSummary(InstallationEdition.Community));

        var controller = CreateController(licensingService, true);

        var result = await controller.GetLicensing(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<LicensingSummaryDto>(ok.Value);
        Assert.Equal(InstallationEdition.Community, payload.Edition);
        Assert.Single(payload.Capabilities);
    }

    [Fact]
    public async Task PatchLicensing_AdminCaller_ForwardsActorAndOverrideMutation()
    {
        var actorUserId = Guid.NewGuid();
        var licensingService = Substitute.For<ILicensingCapabilityService>();
        licensingService.UpdateAsync(
                Arg.Any<InstallationEdition>(),
                Arg.Any<IReadOnlyCollection<CapabilityOverrideMutation>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateSummary(InstallationEdition.Commercial));

        var controller = CreateController(licensingService, true, actorUserId);
        var request = new PatchAdminLicensingRequest(
            InstallationEdition.Commercial,
            [
                new PatchPremiumCapabilityOverrideRequest(
                    PremiumCapabilityKey.MultipleScmProviders,
                    PremiumCapabilityOverrideState.Enabled),
            ]);

        var result = await controller.PatchLicensing(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<LicensingSummaryDto>(ok.Value);
        Assert.Equal(InstallationEdition.Commercial, payload.Edition);

        await licensingService.Received(1)
            .UpdateAsync(
                InstallationEdition.Commercial,
                Arg.Is<IReadOnlyCollection<CapabilityOverrideMutation>>(mutations =>
                    mutations.Count == 1
                    && mutations.First().Key == PremiumCapabilityKey.MultipleScmProviders
                    && mutations.First().OverrideState == PremiumCapabilityOverrideState.Enabled),
                actorUserId,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchLicensing_InvalidOperation_ReturnsConflict()
    {
        var licensingService = Substitute.For<ILicensingCapabilityService>();
        licensingService.UpdateAsync(
                Arg.Any<InstallationEdition>(),
                Arg.Any<IReadOnlyCollection<CapabilityOverrideMutation>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<LicensingSummaryDto>>(_ => throw new InvalidOperationException("Community cannot enable premium capabilities."));

        var controller = CreateController(licensingService, true, Guid.NewGuid());
        var request = new PatchAdminLicensingRequest(
            InstallationEdition.Community,
            [
                new PatchPremiumCapabilityOverrideRequest(
                    PremiumCapabilityKey.MultipleScmProviders,
                    PremiumCapabilityOverrideState.Enabled),
            ]);

        var result = await controller.PatchLicensing(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    private static AdminLicensingController CreateController(
        ILicensingCapabilityService licensingService,
        bool isAdmin,
        Guid? actorUserId = null)
    {
        var controller = new AdminLicensingController(
            new GetLicensingSummaryHandler(licensingService),
            new UpdateLicensingHandler(licensingService))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
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

    private static LicensingSummaryDto CreateSummary(InstallationEdition edition)
    {
        return new LicensingSummaryDto(
            edition,
            edition == InstallationEdition.Commercial ? DateTimeOffset.UtcNow : null,
            [
                new PremiumCapabilityDto(
                    PremiumCapabilityKey.MultipleScmProviders,
                    "Multiple SCM providers",
                    true,
                    true,
                    PremiumCapabilityOverrideState.Default,
                    edition == InstallationEdition.Commercial,
                    edition == InstallationEdition.Commercial ? null : "Commercial edition is required."),
            ]);
    }
}
