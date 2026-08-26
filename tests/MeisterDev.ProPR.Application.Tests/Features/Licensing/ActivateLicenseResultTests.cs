// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

public sealed class ActivateLicenseResultTests
{
    [Fact]
    public void AnActivationResultCannotBeConstructedOutsideItsFactories()
    {
        Assert.Empty(typeof(ActivateLicenseResult).GetConstructors());
    }

    [Fact]
    public void TheFactoriesProduceTheTwoValidStates()
    {
        var suppliedSummary = new LicensingSummaryDto(InstallationEdition.Community, null, []);
        var activated = ActivateLicenseResult.Activated(suppliedSummary);
        var refused = ActivateLicenseResult.Refused(LicenseFailureReason.Malformed, "The document is malformed.");

        Assert.True(activated.IsActivated);
        Assert.Same(suppliedSummary, activated.Summary);
        Assert.Null(activated.RefusalReason);
        Assert.Null(activated.RefusalDetail);
        Assert.False(refused.IsActivated);
        Assert.Null(refused.Summary);
        Assert.Equal(LicenseFailureReason.Malformed, refused.RefusalReason);
        Assert.Equal("The document is malformed.", refused.RefusalDetail);
    }
}
