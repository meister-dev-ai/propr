// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using MeisterProPR.ProCursor.Service.Tests.Support;
using MeisterProPR.ProCursor.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.ProCursor.Service.Tests.Startup;

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
        Assert.Equal("MeisterProPR.ProCursor.Infrastructure.Remote", runtimeCache.GetType().Namespace);
        Assert.Equal("MeisterProPR.ProCursor.Workers", indexWorker.GetType().Namespace);
        Assert.Equal("MeisterProPR.ProCursor.Workers", rollupWorker.GetType().Namespace);
    }
}
