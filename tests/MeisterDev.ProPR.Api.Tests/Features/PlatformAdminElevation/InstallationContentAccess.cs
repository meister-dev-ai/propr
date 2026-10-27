// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     Lets a platform administrator read the content of every client of a test host, as the clients of the System
///     tenant are read. Hosts whose tests concern the shape of an answer, not the elevation rule, use it, because they
///     store reviews without the clients and tenants the rule looks up.
/// </summary>
internal static class InstallationContentAccess
{
    /// <summary>The client a review job of the host belongs to.</summary>
    public static readonly Guid InstallationClientId = Guid.Parse("0c0c0c0c-0000-4000-8000-000000000001");

    /// <summary>Replaces the elevation service with one that grants the content of every client of the installation.</summary>
    public static IServiceCollection AddInstallationContentAccess(this IServiceCollection services)
    {
        var elevations = Substitute.For<IPlatformAdminElevationService>();
        elevations.GetReviewClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(InstallationClientId));
        elevations.ResolveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ContentAccessGrant>>(
            [
                .. call.Arg<IReadOnlyCollection<Guid>>()
                    .Select(clientId => new ContentAccessGrant(clientId, "Installation client", TenantCatalog.SystemTenantId, ContentAccessKind.Installation)),
            ]));

        services.RemoveAll<IPlatformAdminElevationService>();
        services.AddSingleton(elevations);
        return services;
    }
}
