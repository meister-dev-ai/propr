// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.UsageReporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Features.UsageReporting;

public sealed class RemoteProCursorReportingBoundaryTests
{
    [Fact]
    public void ManagedRemoteUsageReporting_ResolvesRemoteOperationalClientsOnly()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=propr;Username=test;Password=test",
                    ["PROCURSOR_REMOTE_MODE"] = "proprManagedRemote",
                    ["PROCURSOR_SERVICE_BASE_URL"] = "http://procursor.internal:8080",
                    ["PROCURSOR_SHARED_KEY"] = "shared-test-key",
                })
            .Build();

        services.AddOptions();
        services.AddLogging();
        services.AddProCursorRemoteMode(configuration);
        services.AddUsageReportingModule(configuration);

        var readDescriptor = services.Single(descriptor => descriptor.ServiceType == typeof(IProCursorTokenUsageReadRepository));
        var rebuildDescriptor = services.Single(descriptor => descriptor.ServiceType == typeof(IProCursorTokenUsageRebuildService));

        Assert.NotNull(readDescriptor.ImplementationFactory);
        Assert.NotNull(rebuildDescriptor.ImplementationFactory);
        Assert.Null(services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IProCursorTokenUsageRecorder)));
    }
}
