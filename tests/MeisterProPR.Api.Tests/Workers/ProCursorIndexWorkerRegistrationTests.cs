// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.ProCursor.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Tests.Workers;

/// <summary>
///     Verifies that the ProCursor module wiring resolves its gateway/coordinator services and hosted worker.
/// </summary>
public sealed class ProCursorIndexWorkerRegistrationTests
{
    [Fact]
    public void ApplicationServices_RegisterProCursorGatewayAndCoordinator()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProCursorOptions>>().Value;
        var gatewayRegistration = FindService<IProCursorGateway>(services);
        var coordinatorRegistration = FindService<ProCursorIndexCoordinator>(services);

        Assert.Equal(17, options.RefreshPollSeconds);
        Assert.Equal(4, options.MaxIndexConcurrency);
        Assert.NotNull(gatewayRegistration);
        Assert.NotNull(coordinatorRegistration);
    }

    [Fact]
    public void HostedServices_RegisterProCursorIndexWorkerAsSingletonHostedService()
    {
        var services = CreateServices();

        services.AddSingleton<ProCursorIndexWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ProCursorIndexWorker>());

        using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        var hostedServices = provider.GetServices<IHostedService>();

        Assert.Contains(hostedServices, hostedService => ReferenceEquals(hostedService, worker));
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        var environment = new TestHostEnvironment("Testing");

        services.AddLogging();
        services.AddProCursorModule(CreateConfiguration(), environment);

        return services;
    }

    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ADO_STUB_PR"] = "true",
                ["MEISTER_JWT_SECRET"] = "test-procursor-jwt-secret-32chars!!",
                ["PROCURSOR_REFRESH_POLL_SECONDS"] = "17",
                ["PROCURSOR_MAX_INDEX_CONCURRENCY"] = "4",
            })
            .Build();
    }

    private static ServiceDescriptor? FindService<TService>(IServiceCollection services)
    {
        return services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(ProCursorIndexWorkerRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
