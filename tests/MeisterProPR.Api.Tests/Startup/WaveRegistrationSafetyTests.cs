// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Startup;

public sealed class WaveRegistrationSafetyTests
{
    [Fact]
    public void ReviewingModule_WithoutDatabaseConnectionString_StillRegistersFeatureBoundaries()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(withDatabaseConnectionString: false);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        Assert.NotNull(FindService<IReviewJobExecutionStore>(services));
        Assert.NotNull(FindService<IReviewDiagnosticsReader>(services));
        Assert.NotNull(FindService<IReviewThreadMemoryStore>(services));
        Assert.NotNull(FindService<IReviewThreadMemoryService>(services));
        Assert.NotNull(FindService<GetReviewJobProtocolHandler>(services));
        Assert.Null(FindService<IJobRepository>(services));
    }

    [Fact]
    public void ReviewingModule_WithDatabaseConnectionString_RegistersLegacyAndFeatureReviewingContractsTogether()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(withDatabaseConnectionString: true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        Assert.NotNull(FindService<IJobRepository>(services));
        Assert.NotNull(FindService<IProtocolRecorder>(services));
        Assert.NotNull(FindService<IThreadMemoryRepository>(services));
        Assert.NotNull(FindService<IReviewJobExecutionStore>(services));
        Assert.NotNull(FindService<IReviewProtocolRecorder>(services));
        Assert.NotNull(FindService<IReviewThreadMemoryStore>(services));
        Assert.NotNull(FindService<IReviewThreadMemoryService>(services));
    }

    private static IConfiguration CreateConfiguration(bool withDatabaseConnectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
            ["ADO_STUB_PR"] = "true",
            ["MEISTER_JWT_SECRET"] = "test-wave-registration-jwt-secret-32!",
            ["DB_CONNECTION_STRING"] = withDatabaseConnectionString ? "Host=localhost;Database=meister;Username=test;Password=test" : null,
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static ServiceDescriptor? FindService<TService>(IServiceCollection services)
    {
        return services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService));
    }
}
