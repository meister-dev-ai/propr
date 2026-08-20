// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.ProRV.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.DependencyInjection;

public sealed class ReviewingExecutionServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReviewingExecution_RegistersScopedReviewHelpersForScopedDependencies()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<FileReviewDispatchPlanner>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<ReviewSynthesisExecutor>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<QualityFilterExecutor>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<IProRVPrefilter>(services));
    }

    [Fact]
    public void AddReviewingExecution_RegistersPipelineProfilesAndSharedPerFileRunner()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<IReviewPipelineProfileProvider>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<IReviewPipeline<PerFileReviewContext>>(services));
    }

    /// <summary>
    ///     Resolves the lease store from the database-backed composition rather than inspecting its
    ///     descriptor. The store is built by a factory that names each dependency, and a lifetime assertion
    ///     holds whether or not those are registered; the resolution fails if one of them is not.
    /// </summary>
    /// <remarks>
    ///     The connection string is never connected to. Constructing the context reads it and nothing opens a
    ///     connection, which is what makes resolving the graph a test rather than a database dependency.
    ///     <para>
    ///         What this does not cover is the option binding: <c>IOptions&lt;T&gt;</c> resolves from the
    ///         generic registration whether or not anything configured it, so a lease option left unbound
    ///         would still produce an instance carrying the defaults.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AddReviewingModule_WithADatabase_ResolvesTheLeaseStoreWithEveryDependencyItNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                    ["ADO_STUB_PR"] = "true",
                    ["MEISTER_JWT_SECRET"] = "test-reviewing-execution-jwt-secret-32!",
                    ["DB_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ReviewJobLeaseStore>(scope.ServiceProvider.GetRequiredService<IReviewJobLeaseStore>());
    }

    private static ServiceLifetime GetLifetime<TService>(IServiceCollection services)
    {
        return services.Single(descriptor => descriptor.ServiceType == typeof(TService)).Lifetime;
    }
}
