// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

/// <summary>Shared doubles for the usage-statistics tests.</summary>
internal static class UsageStatisticsTestDoubles
{
    /// <summary>Builds a state with the gate open and sending on. Callers adjust it with a <c>with</c> expression.</summary>
    public static UsageStatisticsState EnabledState(DateTimeOffset now)
    {
        return new UsageStatisticsState(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            true,
            now.AddDays(-30),
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }

    public static UsageStatisticsEditionResolver EditionResolver(InstallationEdition edition)
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LicensingSummaryDto(edition, null, [])));

        return new UsageStatisticsEditionResolver(licensing);
    }

    public static IProductVersionProvider ProductVersion(string version)
    {
        var provider = Substitute.For<IProductVersionProvider>();
        provider.Version.Returns(version);
        return provider;
    }

    public static IUsageStatisticsCountSource CountSource(UsageStatisticsCounts counts)
    {
        var source = Substitute.For<IUsageStatisticsCountSource>();
        source.CountAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(counts));
        return source;
    }
}
