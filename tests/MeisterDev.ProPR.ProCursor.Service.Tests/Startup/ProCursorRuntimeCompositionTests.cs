// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.ProCursor.Infrastructure.Remote;
using MeisterDev.ProPR.ProCursor.Service.Tests.Support;
using MeisterDev.ProPR.ProCursor.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.ProCursor.Service.Tests.Startup;

public sealed class ProCursorRuntimeCompositionTests
{
    [Fact]
    public void ProCursorServiceHost_ResolvesProCursorOwnedGatewayAndWorkers()
    {
        using var factory = new ProCursorServiceFactory();
        using var scope = factory.Services.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IProCursorGateway>();
        var runtimeCache = scope.ServiceProvider.GetRequiredService<IProCursorRuntimeConfigurationCache>();
        var indexWorker = scope.ServiceProvider.GetRequiredService<ProCursorIndexWorker>();
        var rollupWorker = scope.ServiceProvider.GetRequiredService<ProCursorTokenUsageRollupWorker>();

        Assert.NotNull(gateway);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Infrastructure.Remote", runtimeCache.GetType().Namespace);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Workers", indexWorker.GetType().Namespace);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Workers", rollupWorker.GetType().Namespace);
    }
}
