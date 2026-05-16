// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.DependencyInjection;

public sealed class ReviewingExecutionServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReviewingExecution_RegistersScopedReviewHelpersForScopedDependencies()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<FileReviewDispatchPlanner>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<AgenticFileReviewDispatchPlanner>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<ReviewSynthesisExecutor>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<AgenticReviewSynthesisExecutor>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<AgenticCandidateFindingFactory>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<QualityFilterExecutor>(services));
    }

    [Fact]
    public void AddReviewingExecution_RegistersScopedStrategyDispatcher()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<IReviewStrategyDispatcher>(services));
    }

    [Fact]
    public void AddReviewingExecution_RegistersPipelineProfilesAndSharedPerFileRunner()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<IReviewPipelineProfileProvider>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<IReviewPipeline<PerFileReviewContext>>(services));
    }

    private static ServiceLifetime GetLifetime<TService>(IServiceCollection services)
    {
        return services.Single(descriptor => descriptor.ServiceType == typeof(TService)).Lifetime;
    }
}
