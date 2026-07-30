// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

/// <summary>
///     The module's own wiring. Collection is a passive observer, so a port that fails to resolve does not fail
///     loudly: it stops a review from being measured and nothing says so. These assertions are what make that
///     visible at build time instead of in production.
/// </summary>
public sealed class CodeInsightsModuleRegistrationTests
{
    [Fact]
    public void EveryCollectionBoundaryResolvesToOneStorePerScope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var findings = scope.ServiceProvider.GetRequiredService<ICodeInsightFindingStore>();
        var classification = scope.ServiceProvider.GetRequiredService<ICodeInsightClassificationStore>();
        var dispositions = scope.ServiceProvider.GetRequiredService<ICodeInsightDispositionStore>();
        var misses = scope.ServiceProvider.GetRequiredService<ICodeInsightMissStore>();
        var retention = scope.ServiceProvider.GetRequiredService<ICodeInsightRetentionStore>();

        // One instance behind all five: a request that collects findings and harvests a thread must not end up
        // with two stores, each holding its own change tracker over the same rows.
        Assert.IsType<CodeInsightFindingStore>(findings);
        Assert.Same(findings, classification);
        Assert.Same(findings, dispositions);
        Assert.Same(findings, misses);
        Assert.Same(findings, retention);
    }

    [Fact]
    public void TheIngestionConsumerAndTheGateResolve()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICodeInsightsCollectionGate>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICodeInsightFindingIngestionService>());
    }

    [Fact]
    public void WithoutADatabaseConnectionNothingIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddCodeInsightsModule(new ConfigurationBuilder().Build());

        // The module is composed on a database, so a host without one registers nothing rather than resolving
        // stores that cannot read.
        Assert.Empty(services);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretProtectionCodec>());
        // Supplied by the clients module in the real host, and the gate reads the per-client opt-in through it.
        services.AddSingleton(Substitute.For<IClientRegistry>());
        services.AddDbContext<MeisterProPRDbContext>(options =>
            options.UseInMemoryDatabase("code-insights-module-registration"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=test;Username=test;Password=test",
                })
            .Build();

        services.AddCodeInsightsModule(configuration);

        return services.BuildServiceProvider();
    }
}
