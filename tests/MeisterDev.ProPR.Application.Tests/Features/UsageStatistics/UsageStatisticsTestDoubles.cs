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

    public static ILicenseStateProvider LicenseStateProvider(LicenseState state)
    {
        var provider = Substitute.For<ILicenseStateProvider>();
        provider.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        return provider;
    }

    public static ILicensingIdentityStore LicensingIdentityStore(Guid identity)
    {
        var store = Substitute.For<ILicensingIdentityStore>();
        store.GetOrCreateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(identity));
        return store;
    }

    public static ISystemProfileStore SystemProfileStore(string? profileHash)
    {
        var store = Substitute.For<ISystemProfileStore>();
        store.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SystemProfileSnapshot?>(profileHash is null ? null : Profile(profileHash)));
        return store;
    }

    public static ILicensedResourceCountSource LicensedResourceCountSource(LicensedResourceCounts counts)
    {
        var source = Substitute.For<ILicensedResourceCountSource>();
        source.GetCountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(counts));
        return source;
    }

    private static SystemProfileSnapshot Profile(string profileHash)
    {
        var capturedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return new SystemProfileSnapshot
        {
            Stable = new SystemProfileStableComponents
            {
                PostgresSystemIdentifier = "7100000000000000001",
                DatabaseName = "propr",
                DatabaseOid = 16384,
                IdentityCreatedAtUnixSeconds = capturedAt.ToUnixTimeSeconds(),
                ScmHostHashes = null,
            },
            Volatile = new SystemProfileVolatileComponents(),
            ProfileHash = profileHash,
            CapturedAt = capturedAt,
            UpdatedAt = capturedAt,
        };
    }
}
