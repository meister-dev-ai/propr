// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;


namespace MeisterDev.ProPR.TestSupport;

public static class AdoGuidedDiscoveryTestSupport
{
    public static IProviderAdminDiscoveryService Create()
    {
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        discovery.Provider.Returns(ScmProvider.AzureDevOps);
        discovery.ResolveGuidedSourceAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<ProCursorSourceKind>(), Arg.Any<CanonicalSourceReferenceDto>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(call => AdoGuidedDiscovery.ResolveSourceAsync(
                discovery, call.ArgAt<Guid>(0), call.ArgAt<Guid>(1),
                call.ArgAt<string>(2), call.ArgAt<ProCursorSourceKind>(3), call.ArgAt<CanonicalSourceReferenceDto>(4),
                call.ArgAt<CancellationToken>(5), call.ArgAt<Guid?>(6)));
        return discovery;
    }
}
