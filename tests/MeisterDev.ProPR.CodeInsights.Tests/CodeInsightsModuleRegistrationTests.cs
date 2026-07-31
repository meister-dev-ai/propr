// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.UsageReporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests;

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
    public void WithoutADatabaseConnectionOnlyTheSettingsAreRegistered()
    {
        var services = new ServiceCollection();
        services.AddCodeInsightsModule(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        // Everything that reads or writes is composed on a database, so a host without one gets none of it
        // rather than stores that cannot read.
        Assert.Null(provider.GetService<ICodeInsightFindingStore>());
        Assert.Null(provider.GetService<ICodeInsightsCollectionGate>());
        Assert.Null(provider.GetService<ICodeInsightFindingIngestionService>());

        // The settings are the exception: they are bound ahead of the gate so a worker hosted without a database
        // still starts with its intervals rather than throwing on resolve.
        Assert.NotNull(provider.GetRequiredService<IOptions<CodeInsightsOptions>>().Value);
    }

    [Fact]
    public void EveryClassifierResolves()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // The classifiers are the only services here with dependencies this module does not register, so they
        // are the ones a missing external registration breaks. It breaks quietly: resolution fails inside a
        // background sweep whose handler swallows the exception, so classification simply stops happening. This
        // is the assertion that turns that into a build failure.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IFindingTypeClassifier>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDisregardedFindingClassifier>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IHumanMissClassifier>());
    }

    [Fact]
    public void TheUsageRecorderComesFromTheUsageReportingModule()
    {
        var services = new ServiceCollection();
        services.AddUsageReportingModule(DatabaseConfiguration());

        // The classifiers need IModelUsageRecorder and this module does not register it. Pinning where it comes
        // from means moving or dropping that registration fails here rather than at the first sweep of a host
        // that composes the two modules in the wrong order, or only one of them.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IModelUsageRecorder));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretProtectionCodec>());
        // Supplied by the clients module in the real host, and the gate reads the per-client opt-in through it.
        services.AddSingleton(Substitute.For<IClientRegistry>());
        // Supplied by the AI module and the usage-reporting module in the real host. Named here because the
        // classifiers require them and this module registers neither.
        services.AddSingleton(Substitute.For<IAiRuntimeResolver>());
        services.AddSingleton(Substitute.For<IModelUsageRecorder>());
        services.AddDbContext<MeisterProPRDbContext>(options =>
            options.UseInMemoryDatabase("code-insights-module-registration"));

        services.AddCodeInsightsModule(DatabaseConfiguration());

        return services.BuildServiceProvider();
    }

    private static IConfiguration DatabaseConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=test;Username=test;Password=test",
                })
            .Build();
    }
}
